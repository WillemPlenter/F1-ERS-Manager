using System;
using System.Collections.Generic;
using System.Windows.Forms;

internal sealed class HotkeyManager : IDisposable
{
    const int BaseId = 4700;
    const uint NoRepeat = 0x4000;
    readonly IntPtr window;
    readonly HotkeySettings settings;
    readonly List<int> registered = new List<int>();
    bool disposed;

    internal HotkeyManager(IntPtr windowHandle, HotkeySettings values)
    {
        window = windowHandle;
        settings = values;
    }

    internal void Start()
    {
        try
        {
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
            {
                Shortcut shortcut = settings[action];
                int id = BaseId + (int)action;
                WindowNative.Check(WindowNative.RegisterHotKey(window, id, shortcut.Modifiers | NoRepeat, (uint)shortcut.Key),
                    "Hotkey is already in use: " + shortcut.Text);
                registered.Add(id);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static bool TryResolve(int id, out HotkeyAction action)
    {
        int value = id - BaseId;
        action = HotkeyAction.ToggleLock;
        if (value < 0 || value >= Enum.GetValues(typeof(HotkeyAction)).Length)
            return false;
        action = (HotkeyAction)value;
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (int id in registered) WindowNative.UnregisterHotKey(window, id);
        registered.Clear();
    }
}
