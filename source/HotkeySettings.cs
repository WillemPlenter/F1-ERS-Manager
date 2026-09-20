using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal enum HotkeyAction
{
    ToggleLock,
    Fill,
    Drain,
    ToggleOverlay,
    CycleTarget
}

internal sealed class Shortcut
{
    internal readonly uint Modifiers;
    internal readonly Keys Key;

    internal Shortcut(uint modifiers, Keys key)
    {
        Modifiers = modifiers;
        Key = key;
        Validate();
    }

    static bool FunctionKey(Keys key) { return key >= Keys.F1 && key <= Keys.F24; }
    static bool ModifierKey(Keys key)
    {
        return key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu ||
            (key >= Keys.LShiftKey && key <= Keys.RMenu) || key == Keys.LWin || key == Keys.RWin;
    }

    void Validate()
    {
        if (Modifiers > 7 || (int)Key < 8 || (int)Key > 254 || ModifierKey(Key) ||
            Key == Keys.Escape || !Enum.IsDefined(typeof(Keys), Key))
            throw new ArgumentException("Use a valid key, not Escape or a modifier by itself.");
        if (Modifiers == 0 && !FunctionKey(Key))
            throw new ArgumentException("Use Ctrl, Alt or Shift with a regular key, or choose F1-F24.");
    }

    internal static Shortcut Parse(string text)
    {
        uint modifiers = 0;
        Keys key = Keys.None;
        foreach (string raw in (text ?? String.Empty).Split('+'))
        {
            string part = raw.Trim();
            uint bit = 0;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) bit = 2;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) bit = 1;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) bit = 4;

            if (bit != 0)
            {
                if ((modifiers & bit) != 0) throw new ArgumentException("A modifier was specified twice.");
                modifiers |= bit;
                continue;
            }

            Keys parsed;
            if (part.Length == 0 || !Enum.TryParse<Keys>(part, true, out parsed) || key != Keys.None)
                throw new ArgumentException("Unknown key combination: " + text + ".");
            key = parsed;
        }
        if (key == Keys.None) throw new ArgumentException("Choose a main key as well.");
        return new Shortcut(modifiers, key);
    }

    internal string Text
    {
        get
        {
            return ((Modifiers & 2) != 0 ? "Ctrl+" : String.Empty) +
                ((Modifiers & 1) != 0 ? "Alt+" : String.Empty) +
                ((Modifiers & 4) != 0 ? "Shift+" : String.Empty) + Key;
        }
    }

    public override string ToString() { return Text; }
}

internal sealed class HotkeySettings
{
    readonly Shortcut[] bindings;

    internal static string ConfigPath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "F1 ERS Manager.hotkeys.ini"); }
    }

    internal Shortcut this[HotkeyAction action] { get { return bindings[(int)action]; } }

    internal HotkeySettings(Shortcut[] values)
    {
        bindings = (Shortcut[])values.Clone();
        Validate();
    }

    internal static HotkeySettings Defaults
    {
        get
        {
            return new HotkeySettings(new[] {
                Shortcut.Parse("F8"),
                Shortcut.Parse("F9"),
                Shortcut.Parse("F10"),
                Shortcut.Parse("F11"),
                Shortcut.Parse("Ctrl+F12")
            });
        }
    }

    void Validate()
    {
        int count = Enum.GetValues(typeof(HotkeyAction)).Length;
        if (bindings.Length != count ||
            bindings.Any(delegate(Shortcut value) { return value == null; }))
            throw new ArgumentException("All five hotkeys are required.");
        if (bindings.Select(delegate(Shortcut value) { return value.Text; })
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            throw new ArgumentException("Every action must have a unique hotkey.");
    }

    internal string Serialize()
    {
        var lines = new List<string> {
            "# F1 ERS Manager hotkeys. Change them through Hotkeys."
        };
        foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
            lines.Add(action + "=" + this[action].Text);
        return String.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    internal static HotkeySettings Parse(string text)
    {
        if (text == null || text.Length > 4096)
            throw new ArgumentException("Invalid settings.");
        var result = new Shortcut[Enum.GetValues(typeof(HotkeyAction)).Length];
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int split = line.IndexOf('=');
            HotkeyAction action;
            string name = split < 0 ? String.Empty : line.Substring(0, split).Trim();
            if (split < 0 || !Enum.TryParse<HotkeyAction>(name, true, out action) ||
                !Enum.IsDefined(typeof(HotkeyAction), action) || !name.Equals(action.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Unknown setting: " + line);
            if (result[(int)action] != null)
                throw new ArgumentException("Duplicate setting: " + action + ".");
            result[(int)action] = Shortcut.Parse(line.Substring(split + 1));
        }
        // Older settings files may not contain the newer display-only actions.
        AssignFirstFree(result, HotkeyAction.ToggleOverlay, new[] {
            "F11", "Ctrl+F11", "Shift+F11", "Ctrl+Shift+F11", "Alt+F11"
        });
        AssignFirstFree(result, HotkeyAction.CycleTarget, new[] {
            "Ctrl+F12", "Shift+F12", "Ctrl+Shift+F12", "Alt+F12",
            "Ctrl+F10"
        });
        return new HotkeySettings(result);
    }

    static void AssignFirstFree(Shortcut[] values, HotkeyAction action,
        string[] candidates)
    {
        if (values[(int)action] != null) return;
        foreach (string text in candidates)
        {
            Shortcut candidate = Shortcut.Parse(text);
            if (values.Any(delegate(Shortcut value) {
                return value != null && value.Text.Equals(candidate.Text,
                    StringComparison.OrdinalIgnoreCase);
            })) continue;
            values[(int)action] = candidate;
            return;
        }
        throw new ArgumentException("No free migration hotkey is available for " +
            Label(action) + ".");
    }

    internal static HotkeySettings Load()
    {
        if (!File.Exists(ConfigPath)) return Defaults;
        if (new FileInfo(ConfigPath).Length > 4096)
            throw new InvalidDataException("The hotkey file is too large.");
        try { return Parse(File.ReadAllText(ConfigPath)); }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Invalid F1 ERS Manager.hotkeys.ini: " + exception.Message,
                exception);
        }
    }

    internal void Save()
    {
        string temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, Serialize(), new UTF8Encoding(false));
        if (File.Exists(ConfigPath)) File.Replace(temporary, ConfigPath, null);
        else File.Move(temporary, ConfigPath);
    }

    internal static string Label(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleLock: return "Hold / release current ERS";
            case HotkeyAction.Fill: return "Set ERS to 100% once";
            case HotkeyAction.Drain: return "Drain ERS completely once";
            case HotkeyAction.ToggleOverlay: return "Show / hide in-game overlay";
            case HotkeyAction.CycleTarget: return "Cycle target: Car 1 / Car 2 / Both";
            default: throw new ArgumentOutOfRangeException("action");
        }
    }
}
