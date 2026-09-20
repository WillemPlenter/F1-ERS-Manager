using System;

internal enum ErsTarget
{
    Automatic,
    Car1,
    Car2,
    Both
}

internal sealed class ErsCarState
{
    internal readonly bool Available;
    internal readonly string Driver;
    internal readonly int? DriverNumber;
    internal readonly float? Percentage;

    internal ErsCarState(bool available, string driver, int? driverNumber, float? percentage)
    {
        Available = available;
        Driver = driver ?? String.Empty;
        DriverNumber = driverNumber;
        Percentage = percentage;
    }

    internal static ErsCarState Empty
    {
        get { return new ErsCarState(false, String.Empty, null, null); }
    }
}

internal sealed class ErsSnapshot
{
    internal readonly bool Connected;
    internal readonly bool Supported;
    internal readonly string Game;
    internal readonly string Status;
    internal readonly ErsCarState Car1;
    internal readonly ErsCarState Car2;
    internal readonly bool Locked;
    internal readonly ErsTarget LockedTarget;
    internal readonly float? LockedPercentage;
    internal readonly int VerifiedProcessId;
    internal readonly long VerifiedStamp;

    internal ErsSnapshot(
        bool connected,
        bool supported,
        string game,
        string status,
        ErsCarState car1,
        ErsCarState car2,
        bool locked,
        ErsTarget lockedTarget,
        float? lockedPercentage)
        : this(connected, supported, game, status, car1, car2, locked,
            lockedTarget, lockedPercentage, 0, 0)
    {
    }

    internal ErsSnapshot(
        bool connected,
        bool supported,
        string game,
        string status,
        ErsCarState car1,
        ErsCarState car2,
        bool locked,
        ErsTarget lockedTarget,
        float? lockedPercentage,
        int verifiedProcessId,
        long verifiedStamp)
    {
        Connected = connected;
        Supported = supported;
        Game = game ?? String.Empty;
        Status = status ?? String.Empty;
        Car1 = car1 ?? ErsCarState.Empty;
        Car2 = car2 ?? ErsCarState.Empty;
        Locked = locked;
        LockedTarget = lockedTarget;
        LockedPercentage = lockedPercentage;
        VerifiedProcessId = verifiedProcessId;
        VerifiedStamp = verifiedStamp;
    }

    internal static ErsSnapshot Disconnected(string status)
    {
        return new ErsSnapshot(false, false, String.Empty, status, ErsCarState.Empty, ErsCarState.Empty, false, ErsTarget.Automatic, null);
    }
}

internal sealed class ErsOperationResult
{
    internal readonly bool Success;
    internal readonly string Message;

    internal ErsOperationResult(bool success, string message)
    {
        Success = success;
        Message = message ?? String.Empty;
    }

    internal static ErsOperationResult Ok(string message) { return new ErsOperationResult(true, message); }
    internal static ErsOperationResult Failed(string message) { return new ErsOperationResult(false, message); }
}

// The UI only talks to this boundary. A game-specific implementation must validate
// its build and every target address before it performs a write.
internal interface IErsController : IDisposable
{
    ErsSnapshot Snapshot { get; }
    void Refresh();
    ErsOperationResult ToggleLock(ErsTarget target);
    ErsOperationResult SetPercentage(ErsTarget target, float percentage);
    ErsOperationResult ReleaseLock();
    bool AcceptsForegroundProcess(int processId);
}
