using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class ShortcutField : TextBox
{
    internal ShortcutField()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        BackColor = Color.FromArgb(239, 242, 246);
        ForeColor = Color.FromArgb(18, 22, 29);
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    public override bool PreProcessMessage(ref Message message)
    {
        int kind = message.Msg;
        bool down = kind == 0x100 || kind == 0x104;
        if (down || kind == 0x101 || kind == 0x105)
        {
            if (!down) return true;
            Keys key = (Keys)message.WParam.ToInt32();
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu ||
                (key >= Keys.LShiftKey && key <= Keys.RMenu) || key == Keys.LWin || key == Keys.RWin)
                return true;
            uint modifiers = ((ModifierKeys & Keys.Control) != 0 ? 2u : 0u) |
                ((ModifierKeys & Keys.Alt) != 0 ? 1u : 0u) |
                ((ModifierKeys & Keys.Shift) != 0 ? 4u : 0u);
            try
            {
                Text = new Shortcut(modifiers, key).Text;
                SelectAll();
            }
            catch (ArgumentException) { }
            return true;
        }
        return base.PreProcessMessage(ref message);
    }
}

internal sealed class HotkeyDialog : Form
{
    readonly ShortcutField[] fields =
        new ShortcutField[Enum.GetValues(typeof(HotkeyAction)).Length];
    readonly Label error = new Label();
    internal HotkeySettings Result { get; private set; }

    internal HotkeyDialog(HotkeySettings settings)
    {
        Text = AppVersion.WindowTitle + " — Hotkeys";
        ClientSize = new Size(520, 369);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(19, 23, 31);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        Controls.Add(new Label {
            Text = "Click a field, then press the desired key or combination.",
            Bounds = new Rectangle(18, 15, 480, 24)
        });

        foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
        {
            int index = (int)action;
            Controls.Add(new Label {
                Text = HotkeySettings.Label(action),
                Bounds = new Rectangle(18, 53 + index * 45, 292, 30),
                TextAlign = ContentAlignment.MiddleLeft
            });
            fields[index] = new ShortcutField {
                Text = settings[action].Text,
                Bounds = new Rectangle(320, 55 + index * 45, 180, 28),
                AccessibleName = HotkeySettings.Label(action)
            };
            Controls.Add(fields[index]);
        }

        error.SetBounds(18, 278, 482, 36);
        error.ForeColor = Color.FromArgb(255, 132, 132);
        Controls.Add(error);

        var defaults = new Button { Text = "Restore defaults", Bounds = new Rectangle(18, 325, 145, 30) };
        defaults.Click += delegate {
            HotkeySettings values = HotkeySettings.Defaults;
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction))) fields[(int)action].Text = values[action].Text;
            error.Text = String.Empty;
        };
        var save = new Button { Text = "Save", Bounds = new Rectangle(304, 325, 94, 30) };
        save.Click += delegate { SaveValues(); };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(406, 325, 94, 30), DialogResult = DialogResult.Cancel };
        Controls.Add(defaults);
        Controls.Add(save);
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    void SaveValues()
    {
        try
        {
            var values = new Shortcut[Enum.GetValues(typeof(HotkeyAction)).Length];
            foreach (HotkeyAction action in Enum.GetValues(typeof(HotkeyAction)))
                values[(int)action] = Shortcut.Parse(fields[(int)action].Text);
            Result = new HotkeySettings(values);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ArgumentException exception) { error.Text = exception.Message; }
    }
}
