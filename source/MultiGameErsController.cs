using System;

// Coordinates the two build-specific controllers. Only one running game may
// own write operations at a time.
internal sealed class MultiGameErsController : IErsController
{
    readonly object gate = new object();
    readonly IErsController f1m23;
    readonly IErsController f1m24;
    IErsController active;
    ErsSnapshot snapshot =
        ErsSnapshot.Disconnected("Start F1 Manager to connect.");
    bool disposed;

    internal MultiGameErsController(Action<string> log)
        : this(new F1M23ErsController(log), new F1M24ErsController(log))
    {
    }

    internal MultiGameErsController(IErsController controller23,
        IErsController controller24)
    {
        if (controller23 == null) throw new ArgumentNullException("controller23");
        if (controller24 == null) throw new ArgumentNullException("controller24");
        f1m23 = controller23;
        f1m24 = controller24;
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
            f1m23.Refresh();
            f1m24.Refresh();
            ErsSnapshot state23 = f1m23.Snapshot;
            ErsSnapshot state24 = f1m24.Snapshot;

            if (state23.Connected && state24.Connected)
            {
                ReleaseActiveLocked();
                f1m23.ReleaseLock();
                f1m24.ReleaseLock();
                active = null;
                snapshot = new ErsSnapshot(true, false,
                    "F1 Manager 2023 + 2024",
                    "Both games are running. Close one game to enable ERS changes safely.",
                    ErsCarState.Empty, ErsCarState.Empty, false,
                    ErsTarget.Automatic, null);
                return;
            }

            IErsController selected = state23.Connected ? f1m23 :
                state24.Connected ? f1m24 : null;
            if (!Object.ReferenceEquals(active, selected))
            {
                ReleaseActiveLocked();
                active = selected;
            }

            snapshot = selected == null
                ? ErsSnapshot.Disconnected(
                    "Start F1 Manager 2023 or 2024 to connect.")
                : selected.Snapshot;
        }
    }

    public ErsOperationResult ToggleLock(ErsTarget target)
    {
        lock (gate)
        {
            if (disposed) return Stopped();
            if (active == null) return NoActiveGame();
            ErsOperationResult result = active.ToggleLock(target);
            snapshot = active.Snapshot;
            return result;
        }
    }

    public ErsOperationResult SetPercentage(ErsTarget target, float percentage)
    {
        lock (gate)
        {
            if (disposed) return Stopped();
            if (active == null) return NoActiveGame();
            ErsOperationResult result = active.SetPercentage(target, percentage);
            snapshot = active.Snapshot;
            return result;
        }
    }

    public ErsOperationResult ReleaseLock()
    {
        lock (gate)
        {
            if (disposed) return Stopped();
            ErsOperationResult first = f1m23.ReleaseLock();
            ErsOperationResult second = f1m24.ReleaseLock();
            if (active != null) snapshot = active.Snapshot;
            return first.Success && second.Success
                ? ErsOperationResult.Ok("ERS hold is off.")
                : ErsOperationResult.Failed(
                    "Not all ERS holds could be stopped.");
        }
    }

    public bool AcceptsForegroundProcess(int processId)
    {
        lock (gate)
            return !disposed && active != null &&
                active.AcceptsForegroundProcess(processId);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            try { f1m23.ReleaseLock(); }
            finally
            {
                try { f1m24.ReleaseLock(); }
                finally
                {
                    f1m23.Dispose();
                    f1m24.Dispose();
                    active = null;
                }
            }
        }
    }

    void ReleaseActiveLocked()
    {
        if (active != null) active.ReleaseLock();
    }

    static ErsOperationResult NoActiveGame()
    {
        return ErsOperationResult.Failed(
            "No supported game is unambiguously active; nothing was written.");
    }

    static ErsOperationResult Stopped()
    {
        return ErsOperationResult.Failed("F1 ERS Manager is shutting down.");
    }
}
