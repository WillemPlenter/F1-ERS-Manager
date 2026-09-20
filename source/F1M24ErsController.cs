using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// Controller for the one F1 Manager 2024 Steam build whose executable and
// runtime layout have been verified. Runtime addresses always derive from the
// loaded module base; the battery field is the only field ever written.
internal sealed class F1M24ErsController : IErsController
{
    internal const string SupportedSha256 = "1198feb7b1f39653fe51f04c9b0acb4d125a67d0e8ba6bda1fa353b3368ab356";

    const ulong CarBaseRootRva = 0x798F570UL;
    const ulong VehicleStride = 0x10D8UL;
    const ulong VehicleDriverOffset = 0x708UL;
    const ulong VehicleFuelOffset = 0x778UL;
    const ulong VehicleBatteryOffset = 0x878UL;
    const ulong DriverTeamIdOffset = 0x579UL;
    const ulong DriverNumberOffset = 0x58CUL;
    const ulong DriverStaffIdOffset = 0x590UL;
    const int VehicleCount = 22;
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
    DateTime processStartUtc = DateTime.MinValue;
    string selectedSavePath = String.Empty;
    long selectedSaveLength;
    DateTime selectedSaveLastWriteUtc = DateTime.MinValue;
    DateTime nextSaveStampCheckUtc = DateTime.MinValue;
    DateTime nextObjectScanUtc = DateTime.MinValue;
    string scanStatus = String.Empty;
    ErsSnapshot snapshot = ErsSnapshot.Disconnected("Start F1 Manager to connect.");
    bool disposed;

    internal F1M24ErsController(Action<string> logAction)
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

                if (detection.F1M24Found)
                {
                    string suffix = detection.FirstF1M24Hash.Length >= 12
                        ? " (SHA-256 " + detection.FirstF1M24Hash.Substring(0, 12) + "...)"
                        : String.Empty;
                    string status = detection.ValidationError.Length == 0
                        ? "This F1 Manager 2024 build is not supported" + suffix + "; nothing was written."
                        : "F1 Manager 2024 was found, but its build could not be verified safely: " +
                            detection.ValidationError;
                    snapshot = new ErsSnapshot(true, false, "F1 Manager 2024", status,
                        ErsCarState.Empty, ErsCarState.Empty, false, ErsTarget.Automatic, null);
                }
                else snapshot = ErsSnapshot.Disconnected("Start F1 Manager 2024 to connect.");
            }
            catch (Exception exception)
            {
                DetachLocked("connection error");
                snapshot = ErsSnapshot.Disconnected("Connection stopped safely: " + exception.Message);
                log("F1M24 refresh stopped safely: " + exception.Message);
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
        log("F1M24 ERS controller stopped; all locks released.");
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

    internal static bool IsValidVehicleTelemetry(float fuel, float battery)
    {
        return IsFinite(fuel) && fuel >= 0F && fuel <= 150F &&
            IsValidBatteryFraction(battery);
    }

    DetectionResult DetectGamesLocked()
    {
        var result = new DetectionResult();
        DetectNamedProcessesLocked("F1Manager24", true, result);
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
                if (!inspectBuild)
                {
                    result.F1M24Found = true;
                    continue;
                }

                result.F1M24Found = true;
                string path = process.MainModule.FileName;
                ulong baseAddress = unchecked((ulong)process.MainModule.BaseAddress.ToInt64());
                string hash = GetExecutableHashLocked(path);
                if (result.FirstF1M24Hash.Length == 0) result.FirstF1M24Hash = hash;
                if (IsSupportedHash(hash))
                    result.SupportedF1M24.Add(
                        new ProcessCandidate(process.Id, path, baseAddress, hash,
                            process.StartTime.ToUniversalTime()));
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
        for (int i = 0; i < detection.SupportedF1M24.Count; i++)
        {
            ProcessCandidate candidate = detection.SupportedF1M24[i];
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
            processStartUtc = candidate.StartUtc;
            cars.Clear();
            lockedValues.Clear();
            ResetSaveStampLocked();
            nextObjectScanUtc = DateTime.MinValue;
            scanStatus = String.Empty;
            log("Attached to verified F1Manager24 process " + processId +
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
                log("Detached from F1Manager24 process " + processId + ": " + reason + ".");
        }
        processId = 0;
        moduleBase = 0;
        attachedHash = String.Empty;
        processStartUtc = DateTime.MinValue;
        scanStatus = String.Empty;
        cars.Clear();
        lockedValues.Clear();
        ResetSaveStampLocked();
        lockTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    bool RevalidateCarCacheLocked()
    {
        if (cars.Count == 0) return false;
        if (!SaveStampIsCurrentLocked(false))
        {
            cars.Clear();
            lockedValues.Clear();
            UpdateLockTimerLocked();
            scanStatus = "The F1M24 save changed; the player cars are being checked again.";
            return false;
        }
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
        ResetSaveStampLocked();
        UpdateLockTimerLocked();

        ulong carBase;
        ulong repeatedBase;
        if (!TryResolveCarBaseLocked(out carBase) ||
            !TryResolveCarBaseLocked(out repeatedBase) || carBase != repeatedBase)
        {
            scanStatus = "Open or resume a race; the F1M24 car list is not available yet.";
            return;
        }

        var all = new List<VehicleIdentity>();
        var staffIds = new HashSet<int>();
        var driverAddresses = new HashSet<ulong>();
        for (int i = 0; i < VehicleCount; i++)
        {
            VehicleIdentity vehicle;
            if (!TryCreateVehicleIdentityLocked(i, carBase, out vehicle) ||
                !staffIds.Add(vehicle.DriverStaffId) ||
                !driverAddresses.Add(vehicle.DriverAddress))
            {
                scanStatus = "The 22 F1M24 race cars could not be validated unambiguously.";
                log("F1M24 vehicle list validation stopped at slot " + i + ".");
                return;
            }
            all.Add(vehicle);
        }

        F1M24PlayerTeamInfo selected;
        string saveError;
        if (!F1M24SaveTeamReader.TryReadCurrentTeamInfo(out selected,
            out saveError))
        {
            scanStatus = "Player team not found in the current F1M24 save: " +
                saveError + ".";
            return;
        }
        if (processStartUtc != DateTime.MinValue &&
            selected.LastWriteUtc < processStartUtc.AddMinutes(-2))
        {
            scanStatus = "The autosave predates this game session. Wait until " +
                "F1 Manager has saved the current career.";
            log("F1M24 refused stale save " + Path.GetFileName(selected.Source) +
                " from " + selected.LastWriteUtc.ToString("o") + ".");
            return;
        }

        var matches = new List<VehicleIdentity>();
        for (int i = 0; i < all.Count; i++)
            if (all[i].TeamId == selected.TeamId) matches.Add(all[i]);
        if (matches.Count != 2 ||
            matches[1].Index != matches[0].Index + 1 ||
            matches[0].Index / 2 != matches[1].Index / 2)
        {
            scanStatus = "The current save and race do not point to exactly two " +
                "adjacent team cars.";
            log("F1M24 save/live team cross-check refused TeamID " +
                selected.TeamId + " with " + matches.Count + " live match(es).");
            return;
        }

        VehicleIdentity selectedFirst =
            matches[0].DriverStaffId == selected.DriverOneStaffId ? matches[0] :
            matches[1].DriverStaffId == selected.DriverOneStaffId ? matches[1] : null;
        VehicleIdentity selectedSecond =
            matches[0].DriverStaffId == selected.DriverTwoStaffId ? matches[0] :
            matches[1].DriverStaffId == selected.DriverTwoStaffId ? matches[1] : null;
        if (selectedFirst == null || selectedSecond == null ||
            Object.ReferenceEquals(selectedFirst, selectedSecond))
        {
            scanStatus = "The two drivers in the current save do not exactly " +
                "match the live player team.";
            log("F1M24 save/live driver cross-check refused TeamID " +
                selected.TeamId + ".");
            return;
        }

        FileInfo selectedSave = new FileInfo(selected.Source);
        selectedSave.Refresh();
        if (!selectedSave.Exists ||
            selectedSave.LastWriteTimeUtc != selected.LastWriteUtc ||
            selectedSave.Length <= 0)
        {
            scanStatus = "The current F1M24 save changed during verification.";
            return;
        }
        selectedSavePath = selectedSave.FullName;
        selectedSaveLength = selectedSave.Length;
        selectedSaveLastWriteUtc = selectedSave.LastWriteTimeUtc;
        nextSaveStampCheckUtc = DateTime.UtcNow.AddMilliseconds(500);

        CarSlot first = new CarSlot(1, selectedFirst.Index,
            selectedFirst.DriverAddress, selectedFirst.VehicleAddress,
            selectedFirst.DriverNumber, selectedFirst.DriverStaffId,
            selectedFirst.TeamId);
        CarSlot second = new CarSlot(2, selectedSecond.Index,
            selectedSecond.DriverAddress, selectedSecond.VehicleAddress,
            selectedSecond.DriverNumber, selectedSecond.DriverStaffId,
            selectedSecond.TeamId);
        float ignored;
        if (!TryReadValidatedBatteryLocked(first, out ignored) ||
            !TryReadValidatedBatteryLocked(second, out ignored))
        {
            ResetSaveStampLocked();
            scanStatus = "The race changed during player-car verification.";
            return;
        }

        cars[1] = first;
        cars[2] = second;
        scanStatus = String.Empty;
        log("F1M24 player cars verified from " + Path.GetFileName(selected.Source) +
            ": TeamID " + selected.TeamId + ", staff " +
            selectedFirst.DriverStaffId + "/" + selectedSecond.DriverStaffId +
            ", slots " + selectedFirst.Index + "/" + selectedSecond.Index + ".");
    }

    bool TryCreateVehicleIdentityLocked(int index, ulong carBase,
        out VehicleIdentity identity)
    {
        identity = null;
        if (index < 0 || index >= VehicleCount) return false;
        ulong vehicleAddress = carBase + (ulong)index * VehicleStride;
        ulong driverAddress;
        if (!TryReadPointer(vehicleAddress + VehicleDriverOffset,
            out driverAddress)) return false;

        int span = checked((int)(DriverStaffIdOffset + 4 - DriverTeamIdOffset));
        byte[] driver;
        if (!TryReadExact(driverAddress + DriverTeamIdOffset, span, out driver))
            return false;
        int driverNumber = BitConverter.ToInt32(driver,
            checked((int)(DriverNumberOffset - DriverTeamIdOffset)));
        int staffId = BitConverter.ToInt32(driver,
            checked((int)(DriverStaffIdOffset - DriverTeamIdOffset)));
        int teamId = driver[0];
        float ignored;
        if (teamId <= 0 || driverNumber < 1 || driverNumber > 99 ||
            staffId <= 0 ||
            !TryReadValidatedVehicleLocked(vehicleAddress, out ignored))
            return false;
        identity = new VehicleIdentity(index, vehicleAddress, driverAddress,
            driverNumber, staffId, teamId);
        return true;
    }
    bool TryReadValidatedBatteryLocked(CarSlot car, out float battery)
    {
        battery = 0F;
        if (!HasExactBuildGateLocked() || car == null) return false;

        ulong carBase;
        if (!TryResolveCarBaseLocked(out carBase) ||
            carBase + (ulong)car.Index * VehicleStride != car.VehicleAddress)
            return false;

        ulong driverAddress;
        if (!TryReadPointer(car.VehicleAddress + VehicleDriverOffset,
            out driverAddress) || driverAddress != car.DriverAddress)
            return false;
        int span = checked((int)(DriverStaffIdOffset + 4 - DriverTeamIdOffset));
        byte[] driver;
        if (!TryReadExact(driverAddress + DriverTeamIdOffset, span, out driver))
            return false;
        int driverNumber = BitConverter.ToInt32(driver,
            checked((int)(DriverNumberOffset - DriverTeamIdOffset)));
        int staffId = BitConverter.ToInt32(driver,
            checked((int)(DriverStaffIdOffset - DriverTeamIdOffset)));
        if (driver[0] != car.TeamId || driverNumber != car.DriverNumber ||
            staffId != car.DriverStaffId) return false;

        return TryReadValidatedVehicleLocked(car.VehicleAddress, out battery);
    }

    bool TryReadValidatedVehicleLocked(ulong vehicleAddress, out float battery)
    {
        battery = 0F;
        byte[] data;
        if (!TryReadExact(vehicleAddress,
            checked((int)VehicleBatteryOffset + 4), out data)) return false;
        float fuel = BitConverter.ToSingle(data,
            checked((int)VehicleFuelOffset));
        battery = BitConverter.ToSingle(data,
            checked((int)VehicleBatteryOffset));
        return IsValidVehicleTelemetry(fuel, battery);
    }

    bool TryResolveCarBaseLocked(out ulong carBase)
    {
        carBase = 0;
        ulong first;
        ulong second;
        ulong third;
        ulong fourth;
        ulong fifth;
        return TryReadPointer(moduleBase + CarBaseRootRva, out first) &&
            TryReadPointer(first + 0x150UL, out second) &&
            TryReadPointer(second + 0x3E8UL, out third) &&
            TryReadPointer(third + 0x130UL, out fourth) &&
            TryReadPointer(fourth, out fifth) &&
            TryReadPointer(fifth + 0x28UL, out carBase);
    }

    ErsOperationResult EnsureTargetsReadyLocked(byte[] ids)
    {
        if (!HasExactBuildGateLocked())
            return ErsOperationResult.Failed(
                "No exactly supported F1 Manager 2024 build is connected; nothing was written.");
        if (!SaveStampIsCurrentLocked(true))
        {
            lockedValues.Clear();
            cars.Clear();
            UpdateLockTimerLocked();
            return ErsOperationResult.Failed(
                "The current F1M24 save changed; wait for the new player-car verification.");
        }
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
            if (!SaveStampIsCurrentLocked(false))
            {
                lockedValues.Clear();
                cars.Clear();
                UpdateLockTimerLocked();
                UpdateConnectedSnapshotLocked(
                    "ERS hold stopped because the current save changed.");
                return;
            }
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

    bool SaveStampIsCurrentLocked(bool force)
    {
        if (String.IsNullOrEmpty(selectedSavePath) ||
            selectedSaveLastWriteUtc == DateTime.MinValue ||
            selectedSaveLength <= 0) return false;
        DateTime now = DateTime.UtcNow;
        if (!force && now < nextSaveStampCheckUtc) return true;
        nextSaveStampCheckUtc = now.AddMilliseconds(500);
        try
        {
            var info = new FileInfo(selectedSavePath);
            info.Refresh();
            return info.Exists && info.Length == selectedSaveLength &&
                info.LastWriteTimeUtc == selectedSaveLastWriteUtc;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    void ResetSaveStampLocked()
    {
        selectedSavePath = String.Empty;
        selectedSaveLength = 0;
        selectedSaveLastWriteUtc = DateTime.MinValue;
        nextSaveStampCheckUtc = DateTime.MinValue;
    }

    void UpdateConnectedSnapshotLocked(string statusOverride)
    {
        ErsCarState car1 = BuildCarStateLocked(1);
        ErsCarState car2 = BuildCarStateLocked(2);
        bool bothResolved = car1.Available && car2.Available;
        string status = statusOverride;
        if (String.IsNullOrEmpty(status))
            status = !String.IsNullOrEmpty(scanStatus) ? scanStatus :
                bothResolved
                    ? "F1M24 connection active; the save, team, and both drivers are validated."
                    : "Exact F1M24 build connected; open or resume a race to find both player cars.";

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

        snapshot = new ErsSnapshot(true, bothResolved, "F1 Manager 2024",
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
        internal readonly ulong DriverAddress;
        internal readonly ulong VehicleAddress;
        internal readonly int DriverNumber;
        internal readonly int DriverStaffId;
        internal readonly int TeamId;

        internal CarSlot(byte teamCarId, int index, ulong driverAddress,
            ulong vehicleAddress, int driverNumber, int driverStaffId, int teamId)
        {
            TeamCarId = teamCarId;
            Index = index;
            DriverAddress = driverAddress;
            VehicleAddress = vehicleAddress;
            DriverNumber = driverNumber;
            DriverStaffId = driverStaffId;
            TeamId = teamId;
        }
    }

    sealed class VehicleIdentity
    {
        internal readonly int Index;
        internal readonly ulong VehicleAddress;
        internal readonly ulong DriverAddress;
        internal readonly int DriverNumber;
        internal readonly int DriverStaffId;
        internal readonly int TeamId;

        internal VehicleIdentity(int index, ulong vehicleAddress,
            ulong driverAddress, int driverNumber, int driverStaffId, int teamId)
        {
            Index = index;
            VehicleAddress = vehicleAddress;
            DriverAddress = driverAddress;
            DriverNumber = driverNumber;
            DriverStaffId = driverStaffId;
            TeamId = teamId;
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
        internal readonly DateTime StartUtc;

        internal ProcessCandidate(int processId, string path,
            ulong moduleBase, string hash, DateTime startUtc)
        {
            ProcessId = processId;
            Path = path;
            ModuleBase = moduleBase;
            Hash = hash;
            StartUtc = startUtc;
        }
    }

    sealed class DetectionResult
    {
        internal bool F1M24Found;
        internal string FirstF1M24Hash = String.Empty;
        internal string ValidationError = String.Empty;
        internal readonly List<ProcessCandidate> SupportedF1M24 =
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
