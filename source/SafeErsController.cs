using System;
using System.Collections.Generic;
using System.Diagnostics;

// Safe fallback used until a verified, game-specific resolver is installed.
// It intentionally contains no process-memory write API.
internal sealed class SafeErsController : IErsController
{
    readonly Action<string> log;
    readonly HashSet<int> detected = new HashSet<int>();
    ErsSnapshot snapshot = ErsSnapshot.Disconnected("Start F1 Manager to connect.");
    bool disposed;

    internal SafeErsController(Action<string> logAction)
    {
        log = logAction ?? delegate { };
    }

    public ErsSnapshot Snapshot { get { return snapshot; } }

    public void Refresh()
    {
        if (disposed) return;
        detected.Clear();
        string game = String.Empty;
        Detect("F1Manager23", "F1 Manager 2023", ref game);
        Detect("F1Manager24", "F1 Manager 2024", ref game);

        if (detected.Count == 0)
            snapshot = ErsSnapshot.Disconnected("Start F1 Manager to connect.");
        else
            snapshot = new ErsSnapshot(true, false, game,
                "Game found; this safe fallback does not contain a validated ERS resolver.",
                ErsCarState.Empty, ErsCarState.Empty, false, ErsTarget.Automatic, null);
    }

    void Detect(string processName, string displayName, ref string game)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch (InvalidOperationException) { return; }

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited) continue;
                detected.Add(process.Id);
                if (game.Length == 0) game = displayName;
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
    }

    static ErsOperationResult Unsupported()
    {
        return ErsOperationResult.Failed("No validated ERS addresses were found; nothing was written.");
    }

    public ErsOperationResult ToggleLock(ErsTarget target) { return Unsupported(); }
    public ErsOperationResult SetPercentage(ErsTarget target, float percentage) { return Unsupported(); }
    public ErsOperationResult ReleaseLock() { return ErsOperationResult.Ok("ERS hold is off."); }
    public bool AcceptsForegroundProcess(int processId) { return detected.Contains(processId); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        detected.Clear();
        log("Safe ERS controller stopped; no game memory was written.");
    }
}

internal static class ControllerFactory
{
    internal static IErsController Create(Action<string> log)
    {
        return new MultiGameErsController(log);
    }
}
