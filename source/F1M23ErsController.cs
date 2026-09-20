using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Controller for the one F1 Manager 2023 Steam build whose executable and
// runtime layout have been verified. Runtime addresses always derive from the
// loaded module base; the battery field is the only field ever written.
internal sealed class F1M23ErsController : IErsController
{
    internal const string SupportedSha256 = "d6e8f3ba892d65e947836f90e81ad590fef4720f6a2e6641832482427b8afc36";

    const ulong GEngineRva = 0x75AFCB0UL;
    const ulong CarActorVtableRva = 0x5BE67C0UL;
    const ulong GameViewportOffset = 0x9A0UL;
    const ulong WorldOffset = 0x78UL;
    const ulong GameStateOffset = 0x150UL;
    const ulong RaceManagerOffset = 0x380UL;
    const ulong RaceManagerCarsOffset = 0x8B8AD0UL;
    const ulong VehicleArrayOffset = 0x882A30UL;
    const ulong VehicleStride = 0x1118UL;
    const ulong VehicleFuelOffset = 0x7B8UL;
    const ulong VehicleBatteryOffset = 0x8A8UL;
    const ulong BatteryPresentationOffset = 0x55CUL;
    const ulong DriverNumberOffset = 0x57CUL;
    const ulong DriverStaffIdOffset = 0x580UL;
    const ulong TeamCarIdOffset = 0x5D3UL;
    const int LockIntervalMilliseconds = 75;

    readonly object gate = new object();
    readonly Action<string> log;
    readonly Timer lockTimer;
    readonly Dictionary<byte, CarSlot> cars = new Dictionary<byte, CarSlot>();
    readonly Dictionary<byte, LockedValue> lockedValues = new Dictionary<byte, LockedValue>();
    readonly Dictionary<string, HashCacheEntry> hashCache =
        new Dictionary<string, HashCacheEntry>(StringComparer.OrdinalIgnoreCase);

    SafeProcessHandle processHandle;
    int processId;
    ulong moduleBase;
    string attachedHash = String.Empty;
    DateTime nextObjectScanUtc = DateTime.MinValue;
    ErsSnapshot snapshot = ErsSnapshot.Disconnected("Start F1 Manager to connect.");
    bool disposed;

    internal F1M23ErsController(Action<string> logAction)
    {
        log = logAction ?? delegate { };
        lockTimer = new Timer(LockTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public ErsSnapshot Snapshot
    {
        get { lock (gate) return snapshot; }
    }

    public void Refresh()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                DetectionResult detection = DetectGamesLocked();
                if (!AttachedProcessIsAliveLocked()) DetachLocked("connected process stopped");
                if (processHandle == null || processHandle.IsInvalid) TryAttachLocked(detection);

                if (processHandle != null && !processHandle.IsInvalid)
                {
                    bool cacheValid = RevalidateCarCacheLocked();
                    if (!cacheValid && DateTime.UtcNow >= nextObjectScanUtc)
                    {
                        ScanCarsLocked();
                        nextObjectScanUtc = DateTime.UtcNow.AddSeconds(2);
                    }
                    UpdateConnectedSnapshotLocked(null);
                    return;
                }

                if (detection.F1M23Found)
                {
                    string suffix = detection.FirstF1M23Hash.Length >= 12
                        ? " (SHA-256 " + detection.FirstF1M23Hash.Substring(0, 12) + "...)"
                        : String.Empty;
                    string status = detection.ValidationError.Length == 0
                        ? "This F1 Manager 2023 build is not supported" + suffix + "; nothing was written."
                        : "F1 Manager 2023 was found, but its build could not be verified safely: " +
                            detection.ValidationError;
                    snapshot = new ErsSnapshot(true, false, "F1 Manager 2023", status,
                        ErsCarState.Empty, ErsCarState.Empty, false, ErsTarget.Automatic, null);
                }
                else snapshot = ErsSnapshot.Disconnected("Start F1 Manager 2023 to connect.");
            }
            catch (Exception exception)
            {
                DetachLocked("connection error");
                snapshot = ErsSnapshot.Disconnected("Connection stopped safely: " + exception.Message);
                log("F1M23 refresh stopped safely: " + exception.Message);
            }
        }
    }

    public ErsOperationResult ToggleLock(ErsTarget target)
    {
        lock (gate)
        {
            if (disposed) return ErsOperationResult.Failed("F1 ERS Manager is shutting down.");
            byte[] ids = TargetIds(target);
            ErsOperationResult ready = EnsureTargetsReadyLocked(ids);
            if (!ready.Success) return ready;

            bool allLocked = true;
            for (int i = 0; i < ids.Length; i++)
                if (!lockedValues.ContainsKey(ids[i])) allLocked = false;
            if (allLocked)
            {
                for (int i = 0; i < ids.Length; i++) lockedValues.Remove(ids[i]);
                UpdateLockTimerLocked();
                string stopped = "ERS hold stopped for " + TargetText(ids) + ".";
                UpdateConnectedSnapshotLocked(stopped);
                return ErsOperationResult.Ok(stopped);
            }

            var pending = new List<LockedValue>();
            for (int i = 0; i < ids.Length; i++)
            {
                CarSlot car = cars[ids[i]];
                float current;
                if (!TryReadValidatedBatteryLocked(car, out current))
                    return ErsOperationResult.Failed(
                        "The selected car could not be revalidated; nothing was written.");
                pending.Add(new LockedValue(car, current));
            }
            for (int i = 0; i < pending.Count; i++)
                lockedValues[pending[i].Car.TeamCarId] = pending[i];
            UpdateLockTimerLocked();
            string message = "Current ERS held for " + TargetText(ids) + ".";
            UpdateConnectedSnapshotLocked(message);
            return ErsOperationResult.Ok(message);
        }
    }

    public ErsOperationResult SetPercentage(ErsTarget target, float percentage)
    {
        lock (gate)
        {
            if (disposed) return ErsOperationResult.Failed("F1 ERS Manager is shutting down.");
            int raw;
            try { raw = PercentageToRaw(percentage); }
            catch (ArgumentOutOfRangeException exception) { return ErsOperationResult.Failed(exception.Message); }

            byte[] ids = TargetIds(target);
            ErsOperationResult ready = EnsureTargetsReadyLocked(ids);
            if (!ready.Success) return ready;
            var originals = new List<float>();
            for (int i = 0; i < ids.Length; i++)
            {
                float original;
                if (!TryReadValidatedBatteryLocked(cars[ids[i]], out original))
                    return ErsOperationResult.Failed(
                        "The selected car could not be revalidated; nothing was written.");
                originals.Add(original);
            }

            int written = 0;
            for (int i = 0; i < ids.Length; i++)
            {
                bool writeIssued;
                if (!TryWriteBatteryLocked(cars[ids[i]], raw / 100F,
                    out writeIssued))
                {
                    bool restored = true;
                    int lastRestore = writeIssued ? i : written - 1;
                    for (int restore = lastRestore; restore >= 0; restore--)
                        if (!WriteBatteryLocked(cars[ids[restore]], originals[restore]))
                            restored = false;
                    string stopped = restored
                        ? "ERS write stopped and the earlier change was rolled back."
                        : "ERS write stopped only partially; do not resume the race and try again.";
                    UpdateConnectedSnapshotLocked(stopped);
                    return ErsOperationResult.Failed(
                        stopped + " Check whether the race is still active.");
                }
                written++;
            }

            // A successful one-shot action stops only locks on the selected car(s).
            for (int i = 0; i < ids.Length; i++) lockedValues.Remove(ids[i]);
            UpdateLockTimerLocked();
            string message = "ERS for " + TargetText(ids) + " set to " + raw + "% once.";
            UpdateConnectedSnapshotLocked(message);
            return ErsOperationResult.Ok(message);
        }
    }

    public ErsOperationResult ReleaseLock()
    {
        lock (gate)
        {
            lockedValues.Clear();
            UpdateLockTimerLocked();
            if (processHandle != null && !processHandle.IsInvalid)
                UpdateConnectedSnapshotLocked("ERS hold is off.");
            return ErsOperationResult.Ok("ERS hold is off.");
        }
    }

    public bool AcceptsForegroundProcess(int foregroundProcessId)
    {
        lock (gate)
            return !disposed && foregroundProcessId == processId &&
                HasExactBuildGateLocked();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lockedValues.Clear();
            lockTimer.Change(Timeout.Infinite, Timeout.Infinite);
            DetachLocked("controller closed");
        }
        lockTimer.Dispose();
        log("F1M23 ERS controller stopped; all locks released.");
    }

    internal static ErsTarget NormalizeTarget(ErsTarget target)
    {
        return target == ErsTarget.Automatic ? ErsTarget.Both : target;
    }

    internal static byte[] TargetIds(ErsTarget target)
    {
        switch (NormalizeTarget(target))
        {
            case ErsTarget.Car1: return new byte[] { 1 };
            case ErsTarget.Car2: return new byte[] { 2 };
            case ErsTarget.Both: return new byte[] { 1, 2 };
            default: throw new ArgumentOutOfRangeException("target");
        }
    }

    internal static int PercentageToRaw(float percentage)
    {
        if (Single.IsNaN(percentage) || Single.IsInfinity(percentage) ||
            percentage < 0F || percentage > 100F)
            throw new ArgumentOutOfRangeException("percentage",
                "ERS percentage must be between 0 and 100.");
        return (int)Math.Round(percentage, MidpointRounding.AwayFromZero);
    }

    internal static bool IsValidBattery(int value)
    {
        return value >= 0 && value <= 100;
    }

    internal static bool IsSupportedHash(string hash)
    {
        return String.Equals(hash, SupportedSha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidVehicleTelemetry(
        byte stateMarker, float fuel, float battery)
    {
        return stateMarker == 1 && IsFinite(fuel) &&
            fuel >= 0F && fuel <= 150F && IsValidBatteryFraction(battery);
    }

    DetectionResult DetectGamesLocked()
    {
        var result = new DetectionResult();
        DetectNamedProcessesLocked("F1Manager23", true, result);
        return result;
    }

    void DetectNamedProcessesLocked(string processName, bool inspectBuild, DetectionResult result)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (InvalidOperationException exception)
        {
            result.ValidationError = exception.Message;
            return;
        }

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                result.F1M23Found = true;
                string path = process.MainModule.FileName;
                ulong baseAddress = unchecked((ulong)process.MainModule.BaseAddress.ToInt64());
                string hash = GetExecutableHashLocked(path);
                if (result.FirstF1M23Hash.Length == 0) result.FirstF1M23Hash = hash;
                if (IsSupportedHash(hash))
                    result.SupportedF1M23.Add(
                        new ProcessCandidate(process.Id, path, baseAddress, hash));
            }
            catch (Exception exception)
            {
                if (exception is System.ComponentModel.Win32Exception ||
                    exception is InvalidOperationException ||
                    exception is IOException ||
                    exception is UnauthorizedAccessException)
                    result.ValidationError = exception.Message;
                else throw;
            }
            finally { process.Dispose(); }
        }
    }

    string GetExecutableHashLocked(string path)
    {
        var info = new FileInfo(path);
        HashCacheEntry cached;
        if (hashCache.TryGetValue(path, out cached) &&
            cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            return cached.Hash;

        string hash;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (SHA256 sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(stream))
                .Replace("-", String.Empty).ToLowerInvariant();
        hashCache[path] = new HashCacheEntry(info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    void TryAttachLocked(DetectionResult detection)
    {
        for (int i = 0; i < detection.SupportedF1M23.Count; i++)
        {
            ProcessCandidate candidate = detection.SupportedF1M23[i];
            SafeProcessHandle handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessVmOperation | NativeMethods.ProcessVmRead |
                NativeMethods.ProcessVmWrite | NativeMethods.ProcessQueryLimitedInformation |
                NativeMethods.Synchronize, false, candidate.ProcessId);
            if (handle == null || handle.IsInvalid)
            {
                if (handle != null) handle.Dispose();
                continue;
            }

            processHandle = handle;
            processId = candidate.ProcessId;
            moduleBase = candidate.ModuleBase;
            attachedHash = candidate.Hash;
            cars.Clear();
            lockedValues.Clear();
            nextObjectScanUtc = DateTime.MinValue;
            log("Attached to verified F1Manager23 process " + processId +
                " at module base 0x" + moduleBase.ToString("X") + ".");
            return;
        }
    }

    bool AttachedProcessIsAliveLocked()
    {
        if (processHandle == null || processHandle.IsInvalid || processHandle.IsClosed) return false;
        uint exitCode;
        return NativeMethods.GetExitCodeProcess(processHandle, out exitCode) &&
            exitCode == NativeMethods.StillActive;
    }

    void DetachLocked(string reason)
    {
        if (processHandle != null)
        {
            processHandle.Dispose();
            processHandle = null;
            if (processId != 0)
                log("Detached from F1Manager23 process " + processId + ": " + reason + ".");
        }
        processId = 0;
        moduleBase = 0;
        attachedHash = String.Empty;
        cars.Clear();
        lockedValues.Clear();
        lockTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    bool RevalidateCarCacheLocked()
    {
        if (cars.Count == 0) return false;
        var invalid = new List<byte>();
        foreach (KeyValuePair<byte, CarSlot> pair in cars)
        {
            float ignored;
            if (!TryReadValidatedBatteryLocked(pair.Value, out ignored)) invalid.Add(pair.Key);
        }
        for (int i = 0; i < invalid.Count; i++)
        {
            cars.Remove(invalid[i]);
            lockedValues.Remove(invalid[i]);
        }
        UpdateLockTimerLocked();
        return cars.ContainsKey(1) && cars.ContainsKey(2);
    }

    void ScanCarsLocked()
    {
        cars.Clear();
        lockedValues.Clear();
        UpdateLockTimerLocked();
        ulong engine;
        ulong viewport;
        ulong world;
        ulong gameState;
        ulong raceManager;
        if (!TryReadPointer(moduleBase + GEngineRva, out engine) ||
            !TryReadPointer(engine + GameViewportOffset, out viewport) ||
            !TryReadPointer(viewport + WorldOffset, out world) ||
            !TryReadPointer(world + GameStateOffset, out gameState) ||
            !TryReadPointer(gameState + RaceManagerOffset, out raceManager))
        {
            log("F1M23 race object chain is not available yet.");
            return;
        }

        ulong expectedVtable = moduleBase + CarActorVtableRva;
        var candidates = new Dictionary<byte, List<CarSlot>>();
        candidates[1] = new List<CarSlot>();
        candidates[2] = new List<CarSlot>();
        for (int i = 0; i < 20; i++)
        {
            ulong objectAddress;
            if (!TryReadPointer(raceManager + RaceManagerCarsOffset + (ulong)(i * 8),
                out objectAddress)) continue;
            ulong vtable;
            if (!TryReadUInt64(objectAddress, out vtable)) continue;
            if (vtable != expectedVtable) continue;
            ulong vehicleAddress = raceManager + VehicleArrayOffset + (ulong)i * VehicleStride;
            CarSlot car;
            if (TryCreateCarSlotLocked(i, objectAddress, vehicleAddress, out car))
                candidates[car.TeamCarId].Add(car);
        }

        foreach (byte teamCarId in new byte[] { 1, 2 })
        {
            List<CarSlot> matches = candidates[teamCarId];
            if (matches.Count == 1) cars[teamCarId] = matches[0];
            else if (matches.Count > 1)
                log("Refused ambiguous CarActor cache for TeamCarId " +
                    teamCarId + ": " + matches.Count + " candidates.");
        }
        log("RaceManager car list checked: " + cars.Count +
            " validated player car(s).");
    }

    bool TryCreateCarSlotLocked(
        int index, ulong objectAddress, ulong vehicleAddress, out CarSlot car)
    {
        car = null;
        int span = checked((int)(TeamCarIdOffset - BatteryPresentationOffset + 1));
        byte[] data;
        if (!TryReadExact(objectAddress + BatteryPresentationOffset, span, out data)) return false;
        int battery = BitConverter.ToInt32(data, 0);
        int driverNumber = BitConverter.ToInt32(
            data, checked((int)(DriverNumberOffset - BatteryPresentationOffset)));
        int staffId = BitConverter.ToInt32(
            data, checked((int)(DriverStaffIdOffset - BatteryPresentationOffset)));
        byte teamCarId = data[checked((int)(TeamCarIdOffset - BatteryPresentationOffset))];
        if ((teamCarId != 1 && teamCarId != 2) || !IsValidBattery(battery) ||
            driverNumber < 1 || driverNumber > 99 || staffId <= 0) return false;
        float ignored;
        if (!TryReadValidatedVehicleLocked(vehicleAddress, out ignored)) return false;
        car = new CarSlot(teamCarId, index, objectAddress, vehicleAddress,
            driverNumber, staffId);
        return true;
    }

    bool TryReadValidatedBatteryLocked(CarSlot car, out float battery)
    {
        battery = 0F;
        if (!HasExactBuildGateLocked() || car == null) return false;

        ulong raceManager;
        ulong currentActor;
        if (!TryResolveRaceManagerLocked(out raceManager) ||
            !TryReadPointer(raceManager + RaceManagerCarsOffset +
                (ulong)(car.Index * 8), out currentActor) ||
            currentActor != car.ObjectAddress ||
            raceManager + VehicleArrayOffset + (ulong)car.Index * VehicleStride !=
                car.VehicleAddress)
            return false;

        ulong vtable;
        if (!TryReadUInt64(car.ObjectAddress, out vtable) ||
            vtable != moduleBase + CarActorVtableRva) return false;

        int span = checked((int)(TeamCarIdOffset - BatteryPresentationOffset + 1));
        byte[] actorData;
        if (!TryReadExact(car.ObjectAddress + BatteryPresentationOffset,
            span, out actorData)) return false;
        int presentationBattery = BitConverter.ToInt32(actorData, 0);
        int driverNumber = BitConverter.ToInt32(actorData,
            checked((int)(DriverNumberOffset - BatteryPresentationOffset)));
        int staffId = BitConverter.ToInt32(actorData,
            checked((int)(DriverStaffIdOffset - BatteryPresentationOffset)));
        byte teamCarId =
            actorData[checked((int)(TeamCarIdOffset - BatteryPresentationOffset))];
        if (!IsValidBattery(presentationBattery) || teamCarId != car.TeamCarId ||
            driverNumber != car.DriverNumber || staffId != car.DriverStaffId)
            return false;

        return TryReadValidatedVehicleLocked(car.VehicleAddress, out battery);
    }

    bool TryReadValidatedVehicleLocked(ulong vehicleAddress, out float battery)
    {
        battery = 0F;
        byte[] data;
        if (!TryReadExact(vehicleAddress, checked((int)VehicleBatteryOffset + 4),
            out data)) return false;
        float fuel = BitConverter.ToSingle(data, checked((int)VehicleFuelOffset));
        battery = BitConverter.ToSingle(data, checked((int)VehicleBatteryOffset));
        return IsValidVehicleTelemetry(data[8], fuel, battery);
    }

    bool TryResolveRaceManagerLocked(out ulong raceManager)
    {
        raceManager = 0;
        ulong engine;
        ulong viewport;
        ulong world;
        ulong gameState;
        return TryReadPointer(moduleBase + GEngineRva, out engine) &&
            TryReadPointer(engine + GameViewportOffset, out viewport) &&
            TryReadPointer(viewport + WorldOffset, out world) &&
            TryReadPointer(world + GameStateOffset, out gameState) &&
            TryReadPointer(gameState + RaceManagerOffset, out raceManager);
    }

    ErsOperationResult EnsureTargetsReadyLocked(byte[] ids)
    {
        if (!HasExactBuildGateLocked())
            return ErsOperationResult.Failed(
                "No exactly supported F1 Manager 2023 build is connected; nothing was written.");
        for (int i = 0; i < ids.Length; i++)
            if (!cars.ContainsKey(ids[i]))
                return ErsOperationResult.Failed("Car " + ids[i] +
                    " has not been found safely yet. Open or resume a race.");
        return ErsOperationResult.Ok(String.Empty);
    }

    bool HasExactBuildGateLocked()
    {
        return processHandle != null && !processHandle.IsInvalid &&
            !processHandle.IsClosed && moduleBase != 0 && processId != 0 &&
            IsSupportedHash(attachedHash) && AttachedProcessIsAliveLocked();
    }

    bool WriteBatteryLocked(CarSlot car, float fraction)
    {
        bool ignored;
        return TryWriteBatteryLocked(car, fraction, out ignored);
    }

    bool TryWriteBatteryLocked(CarSlot car, float fraction,
        out bool writeIssued)
    {
        writeIssued = false;
        if (!IsValidBatteryFraction(fraction) || !HasExactBuildGateLocked())
            return false;
        float ignored;
        if (!TryReadValidatedBatteryLocked(car, out ignored)) return false;

        byte[] bytes = BitConverter.GetBytes(fraction);
        UIntPtr written;
        bool ok = NativeMethods.WriteProcessMemory(processHandle,
            ToIntPtr(car.VehicleAddress + VehicleBatteryOffset), bytes,
            new UIntPtr((uint)bytes.Length), out written);
        writeIssued = ok || written.ToUInt64() != 0UL;
        if (!ok || written.ToUInt64() != (ulong)bytes.Length) return false;

        // Confirm the authoritative float was accepted. The simulation can
        // advance on another thread immediately after the write, hence the
        // small tolerance.
        float verified;
        return TryReadSingle(car.VehicleAddress + VehicleBatteryOffset,
            out verified) && IsValidBatteryFraction(verified) &&
            Math.Abs(verified - fraction) <= 0.01F;
    }

    void LockTick(object state)
    {
        lock (gate)
        {
            if (disposed || lockedValues.Count == 0) return;
            var failed = new List<byte>();
            foreach (KeyValuePair<byte, LockedValue> pair in lockedValues)
                if (!WriteBatteryLocked(pair.Value.Car, pair.Value.Value))
                    failed.Add(pair.Key);
            for (int i = 0; i < failed.Count; i++) lockedValues.Remove(failed[i]);
            if (failed.Count != 0)
            {
                UpdateLockTimerLocked();
                UpdateConnectedSnapshotLocked(
                    "ERS hold stopped safely for a car that is no longer valid.");
                log("ERS lock stopped after target revalidation failed.");
            }
        }
    }

    void UpdateLockTimerLocked()
    {
        lockTimer.Change(lockedValues.Count == 0 ? Timeout.Infinite : 0,
            lockedValues.Count == 0 ? Timeout.Infinite :
                LockIntervalMilliseconds);
    }

    void UpdateConnectedSnapshotLocked(string statusOverride)
    {
        ErsCarState car1 = BuildCarStateLocked(1);
        ErsCarState car2 = BuildCarStateLocked(2);
        bool bothResolved = car1.Available && car2.Available;
        string status = statusOverride;
        if (String.IsNullOrEmpty(status))
            status = bothResolved
                ? "F1M23 connection active; only the validated ERS battery field is changed."
                : "Exact F1M23 build connected; open or resume a race to find both player cars.";

        ErsTarget lockedTarget = ErsTarget.Automatic;
        float? lockedPercentage = null;
        if (lockedValues.Count != 0)
        {
            bool one = lockedValues.ContainsKey(1);
            bool two = lockedValues.ContainsKey(2);
            lockedTarget = one && two ? ErsTarget.Both :
                one ? ErsTarget.Car1 : ErsTarget.Car2;
            if (one && !two) lockedPercentage = lockedValues[1].Value * 100F;
            else if (two && !one) lockedPercentage = lockedValues[2].Value * 100F;
            else if (Math.Abs(
                lockedValues[1].Value - lockedValues[2].Value) < 0.000001F)
                lockedPercentage = lockedValues[1].Value * 100F;
        }

        snapshot = new ErsSnapshot(true, bothResolved, "F1 Manager 2023",
            status, car1, car2, lockedValues.Count != 0,
            lockedTarget, lockedPercentage, processId,
            Stopwatch.GetTimestamp());
    }

    ErsCarState BuildCarStateLocked(byte teamCarId)
    {
        CarSlot car;
        if (!cars.TryGetValue(teamCarId, out car)) return ErsCarState.Empty;
        float battery;
        if (!TryReadValidatedBatteryLocked(car, out battery))
            return ErsCarState.Empty;
        return new ErsCarState(true, "Player car " + teamCarId,
            car.DriverNumber, battery * 100F);
    }

    bool TryReadPointer(ulong address, out ulong value)
    {
        if (!TryReadUInt64(address, out value)) return false;
        return IsUserAddress(value) && (value & 7UL) == 0;
    }

    bool TryReadUInt64(ulong address, out ulong value)
    {
        value = 0;
        byte[] data;
        if (!TryReadExact(address, 8, out data)) return false;
        value = BitConverter.ToUInt64(data, 0);
        return true;
    }

    bool TryReadSingle(ulong address, out float value)
    {
        value = 0F;
        byte[] data;
        if (!TryReadExact(address, 4, out data)) return false;
        value = BitConverter.ToSingle(data, 0);
        return true;
    }

    bool TryReadExact(ulong address, int size, out byte[] data)
    {
        data = null;
        if (processHandle == null || processHandle.IsInvalid || size <= 0 ||
            !IsUserAddress(address)) return false;
        byte[] buffer = new byte[size];
        UIntPtr read;
        bool ok = NativeMethods.ReadProcessMemory(processHandle,
            ToIntPtr(address), buffer, new UIntPtr((uint)size), out read);
        if (!ok || read.ToUInt64() != (ulong)size) return false;
        data = buffer;
        return true;
    }

    static bool IsValidBatteryFraction(float value)
    {
        return IsFinite(value) && value >= 0F && value <= 1.001F;
    }

    static bool IsFinite(float value)
    {
        return !Single.IsNaN(value) && !Single.IsInfinity(value);
    }

    static IntPtr ToIntPtr(ulong address)
    {
        return new IntPtr(unchecked((long)address));
    }

    static bool IsUserAddress(ulong address)
    {
        return address >= 0x10000UL &&
            address <= 0x00007FFFFFFFFFFFUL;
    }

    static string TargetText(byte[] ids)
    {
        if (ids.Length == 2) return "both cars";
        return ids[0] == 1 ? "Car 1" : "Car 2";
    }

    sealed class CarSlot
    {
        internal readonly byte TeamCarId;
        internal readonly int Index;
        internal readonly ulong ObjectAddress;
        internal readonly ulong VehicleAddress;
        internal readonly int DriverNumber;
        internal readonly int DriverStaffId;

        internal CarSlot(byte teamCarId, int index, ulong objectAddress,
            ulong vehicleAddress, int driverNumber, int driverStaffId)
        {
            TeamCarId = teamCarId;
            Index = index;
            ObjectAddress = objectAddress;
            VehicleAddress = vehicleAddress;
            DriverNumber = driverNumber;
            DriverStaffId = driverStaffId;
        }
    }

    sealed class LockedValue
    {
        internal readonly CarSlot Car;
        internal readonly float Value;

        internal LockedValue(CarSlot car, float value)
        {
            Car = car;
            Value = value;
        }
    }

    sealed class ProcessCandidate
    {
        internal readonly int ProcessId;
        internal readonly string Path;
        internal readonly ulong ModuleBase;
        internal readonly string Hash;

        internal ProcessCandidate(int processId, string path,
            ulong moduleBase, string hash)
        {
            ProcessId = processId;
            Path = path;
            ModuleBase = moduleBase;
            Hash = hash;
        }
    }

    sealed class DetectionResult
    {
        internal bool F1M23Found;
        internal string FirstF1M23Hash = String.Empty;
        internal string ValidationError = String.Empty;
        internal readonly List<ProcessCandidate> SupportedF1M23 =
            new List<ProcessCandidate>();
    }

    sealed class HashCacheEntry
    {
        internal readonly long Length;
        internal readonly DateTime LastWriteUtc;
        internal readonly string Hash;

        internal HashCacheEntry(long length, DateTime lastWriteUtc,
            string hash)
        {
            Length = length;
            LastWriteUtc = lastWriteUtc;
            Hash = hash;
        }
    }

    sealed class SafeProcessHandle :
        SafeHandleZeroOrMinusOneIsInvalid
    {
        SafeProcessHandle() : base(true) { }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    static class NativeMethods
    {
        internal const uint ProcessVmOperation = 0x0008;
        internal const uint ProcessVmRead = 0x0010;
        internal const uint ProcessVmWrite = 0x0020;
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint Synchronize = 0x00100000;
        internal const uint StillActive = 259;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(
            SafeProcessHandle process, IntPtr baseAddress,
            [Out] byte[] buffer, UIntPtr size, out UIntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(
            SafeProcessHandle process, IntPtr baseAddress,
            byte[] buffer, UIntPtr size, out UIntPtr bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(
            SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
