using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed class MainForm : Form
{
    const int WmHotkey = 0x0312;
    readonly IErsController controller;
    readonly Action<string> log;
    readonly ComboBox target = new ComboBox();
    readonly Label connection = new Label();
    readonly Label game = new Label();
    readonly Label lockState = new Label();
    readonly Label notice = new Label();
    readonly Label car1Driver = new Label();
    readonly Label car2Driver = new Label();
    readonly Label car1Value = new Label();
    readonly Label car2Value = new Label();
    readonly ErsGauge car1Gauge = new ErsGauge();
    readonly ErsGauge car2Gauge = new ErsGauge();
    readonly Button lockButton = new Button();
    readonly Button fillButton = new Button();
    readonly Button drainButton = new Button();
    readonly Label lockKey = new Label();
    readonly Label fillKey = new Label();
    readonly Label drainKey = new Label();
    readonly Timer refresh = new Timer();
    readonly Timer overlayRefresh = new Timer();
    readonly CheckBox overlayToggle = new CheckBox();
    readonly ComboBox overlayPosition = new ComboBox();
    readonly ErsOverlay overlay = new ErsOverlay();
    HotkeySettings settings;
    HotkeyManager hotkeys;
    DateTime noticeUntil;

    internal MainForm(IErsController ersController, Action<string> logAction)
    {
        controller = ersController;
        log = logAction ?? delegate { };
        Text = AppVersion.WindowTitle;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (ArgumentException) { }
        ClientSize = new Size(780, 548);
        MinimumSize = new Size(796, 587);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(14, 18, 25);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);
        BuildInterface();
        LoadSettings();
        refresh.Interval = 400;
        refresh.Tick += delegate { RefreshController(); };
        overlayRefresh.Interval = 100;
        overlayRefresh.Tick += delegate { RefreshOverlay(); };
        Shown += delegate {
            StartHotkeys();
            refresh.Start();
            overlayRefresh.Start();
            RefreshController();
            RefreshOverlay();
        };
    }

    void BuildInterface()
    {
        var title = new Label {
            Text = "F1  ERS  MANAGER",
            Font = new Font("Segoe UI Semibold", 22F, FontStyle.Bold),
            ForeColor = Color.FromArgb(235, 241, 247),
            Bounds = new Rectangle(25, 18, 430, 44)
        };
        Controls.Add(title);
        Controls.Add(new Label {
            Text = "ENERGY CONTROL",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 219, 163),
            Bounds = new Rectangle(29, 62, 230, 22)
        });

        connection.TextAlign = ContentAlignment.MiddleRight;
        connection.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        connection.Bounds = new Rectangle(468, 22, 282, 24);
        game.TextAlign = ContentAlignment.MiddleRight;
        game.ForeColor = Color.FromArgb(149, 160, 177);
        game.Bounds = new Rectangle(468, 48, 282, 24);
        Controls.Add(connection);
        Controls.Add(game);

        var line = new Panel { BackColor = Color.FromArgb(46, 54, 66), Bounds = new Rectangle(25, 92, 730, 1) };
        line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(line);

        Controls.Add(new Label { Text = "Target", ForeColor = Color.FromArgb(158, 170, 187), Bounds = new Rectangle(25, 112, 70, 25), TextAlign = ContentAlignment.MiddleLeft });
        target.DropDownStyle = ComboBoxStyle.DropDownList;
        target.FlatStyle = FlatStyle.Flat;
        target.BackColor = Color.FromArgb(36, 43, 54);
        target.ForeColor = Color.White;
        target.Bounds = new Rectangle(92, 110, 205, 28);
        target.Items.AddRange(new object[] { "Automatic", "Car 1", "Car 2", "Both cars" });
        target.SelectedIndex = 3;
        target.SelectedIndexChanged += delegate {
            UpdateSnapshot(controller.Snapshot);
            if (overlayPosition.SelectedIndex >= 0) RefreshOverlay();
        };
        Controls.Add(target);

        overlayToggle.SetBounds(310, 108, 156, 31);
        overlayToggle.Appearance = Appearance.Button;
        overlayToggle.Text = "Overlay: Off · F11";
        overlayToggle.TextAlign = ContentAlignment.MiddleCenter;
        overlayToggle.FlatStyle = FlatStyle.Flat;
        overlayToggle.BackColor = Color.FromArgb(35, 42, 53);
        overlayToggle.ForeColor = Color.White;
        overlayToggle.Cursor = Cursors.Hand;
        overlayToggle.AutoEllipsis = true;
        overlayToggle.AccessibleName = "In-game overlay";
        overlayToggle.CheckedChanged += delegate {
            overlayToggle.BackColor = overlayToggle.Checked
                ? Color.FromArgb(105, 32, 40)
                : Color.FromArgb(35, 42, 53);
            UpdateHotkeyLabels();
            log("Overlay " + (overlayToggle.Checked ? "enabled." : "hidden."));
            RefreshOverlay();
        };
        Controls.Add(overlayToggle);

        overlayPosition.SetBounds(476, 110, 140, 28);
        overlayPosition.DropDownStyle = ComboBoxStyle.DropDownList;
        overlayPosition.FlatStyle = FlatStyle.Flat;
        overlayPosition.BackColor = Color.FromArgb(36, 43, 54);
        overlayPosition.ForeColor = Color.White;
        overlayPosition.Items.AddRange(new object[] {
            "Top right", "Top left", "Bottom right", "Bottom left", "Top center",
            "Left HUD gap"
        });
        overlayPosition.SelectedIndex = 5;
        overlayPosition.AccessibleName = "Overlay position";
        overlayPosition.SelectedIndexChanged += delegate {
            log("Overlay position: " + overlayPosition.Text + ".");
            RefreshOverlay();
        };
        Controls.Add(overlayPosition);

        var settingsButton = MakeSmallButton("Hotkeys…", new Rectangle(628, 108, 127, 31));
        settingsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        settingsButton.Click += delegate { ConfigureHotkeys(); };
        Controls.Add(settingsButton);

        var card1 = MakeCarCard("CAR 1", 25, car1Driver, car1Value, car1Gauge);
        var card2 = MakeCarCard("CAR 2", 397, car2Driver, car2Value, car2Gauge);
        card2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(card1);
        Controls.Add(card2);

        lockButton.SetBounds(25, 310, 235, 92);
        fillButton.SetBounds(272, 310, 235, 92);
        drainButton.SetBounds(519, 310, 236, 92);
        drainButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        StyleActionButton(lockButton, "HOLD ERS", Color.FromArgb(224, 165, 63));
        StyleActionButton(fillButton, "SET ERS TO 100%", Color.FromArgb(37, 194, 136));
        StyleActionButton(drainButton, "DRAIN ERS", Color.FromArgb(221, 71, 84));
        lockButton.Enabled = fillButton.Enabled = drainButton.Enabled = false;
        lockButton.Click += delegate { Execute(HotkeyAction.ToggleLock); };
        fillButton.Click += delegate { Execute(HotkeyAction.Fill); };
        drainButton.Click += delegate { Execute(HotkeyAction.Drain); };
        Controls.Add(lockButton);
        Controls.Add(fillButton);
        Controls.Add(drainButton);

        lockKey.SetBounds(25, 407, 235, 22);
        fillKey.SetBounds(272, 407, 235, 22);
        drainKey.SetBounds(519, 407, 236, 22);
        drainKey.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        foreach (Label label in new[] { lockKey, fillKey, drainKey }) {
            label.ForeColor = Color.FromArgb(142, 153, 170);
            label.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(label);
        }

        lockState.Bounds = new Rectangle(25, 446, 730, 31);
        lockState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        lockState.BackColor = Color.FromArgb(30, 36, 46);
        lockState.ForeColor = Color.FromArgb(167, 177, 193);
        lockState.TextAlign = ContentAlignment.MiddleCenter;
        lockState.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
        Controls.Add(lockState);

        notice.Bounds = new Rectangle(25, 488, 730, 42);
        notice.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        notice.ForeColor = Color.FromArgb(166, 177, 193);
        notice.TextAlign = ContentAlignment.TopCenter;
        Controls.Add(notice);
    }

    Panel MakeCarCard(string heading, int x, Label driver, Label value, ErsGauge gauge)
    {
        var panel = new Panel { BackColor = Color.FromArgb(25, 30, 39), Bounds = new Rectangle(x, 158, 358, 128) };
        panel.Controls.Add(new Label {
            Text = heading,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(52, 219, 163),
            Bounds = new Rectangle(16, 12, 100, 21)
        });
        driver.Text = "Not found";
        driver.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
        driver.Bounds = new Rectangle(16, 35, 240, 28);
        panel.Controls.Add(driver);
        value.Text = "—";
        value.Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold);
        value.TextAlign = ContentAlignment.MiddleRight;
        value.Bounds = new Rectangle(256, 28, 86, 39);
        panel.Controls.Add(value);
        gauge.Bounds = new Rectangle(16, 78, 326, 29);
        gauge.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        panel.Controls.Add(gauge);
        return panel;
    }

    static Button MakeSmallButton(string text, Rectangle bounds)
    {
        return new Button {
            Text = text,
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 42, 53),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
    }

    static void StyleActionButton(Button button, string text, Color accent)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 46, 57);
        button.BackColor = Color.FromArgb(25, 30, 39);
        button.ForeColor = accent;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
    }

    void LoadSettings()
    {
        try { settings = HotkeySettings.Load(); }
        catch (Exception exception)
        {
            settings = HotkeySettings.Defaults;
            log("Hotkey settings ignored: " + exception.Message);
            SetNotice("The hotkey file was invalid; F8–F11 and Ctrl+F12 were restored.", false);
        }
        UpdateHotkeyLabels();
    }

    void StartHotkeys()
    {
        if (hotkeys != null) hotkeys.Dispose();
        hotkeys = null;
        try
        {
            var manager = new HotkeyManager(Handle, settings);
            manager.Start();
            hotkeys = manager;
            log("Hotkeys active: " + settings[HotkeyAction.ToggleLock] + ", " +
                settings[HotkeyAction.Fill] + ", " + settings[HotkeyAction.Drain] + ", " +
                settings[HotkeyAction.ToggleOverlay] + ", " +
                settings[HotkeyAction.CycleTarget]);
        }
        catch (Exception exception)
        {
            log("Hotkeys unavailable: " + exception.Message);
            SetNotice("Hotkeys could not be activated: " + exception.Message, false);
        }
    }

    void ConfigureHotkeys()
    {
        HotkeySettings previous = settings;
        if (hotkeys != null) { hotkeys.Dispose(); hotkeys = null; }
        using (var dialog = new HotkeyDialog(settings))
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                settings = dialog.Result;
                try
                {
                    StartHotkeys();
                    if (hotkeys == null) throw new InvalidOperationException("The selected hotkeys are unavailable.");
                    settings.Save();
                    UpdateHotkeyLabels();
                    SetNotice("Hotkeys saved.", true);
                    return;
                }
                catch (Exception exception)
                {
                    log("New hotkeys rejected: " + exception.Message);
                    settings = previous;
                    StartHotkeys();
                    MessageBox.Show(exception.Message, AppVersion.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
        }
        settings = previous;
        StartHotkeys();
    }

    void UpdateHotkeyLabels()
    {
        lockKey.Text = settings[HotkeyAction.ToggleLock].Text + "  •  hold / release";
        fillKey.Text = settings[HotkeyAction.Fill].Text + "  •  fill once";
        drainKey.Text = settings[HotkeyAction.Drain].Text + "  •  drain once";
        string toggle = settings[HotkeyAction.ToggleOverlay].Text;
        overlayToggle.Text = "Overlay: " + (overlayToggle.Checked ? "On" : "Off") +
            " · " + toggle;
        overlay.TargetHint = settings[HotkeyAction.CycleTarget].Text;
        overlay.Invalidate();
    }

    ErsTarget SelectedTarget
    {
        get
        {
            switch (target.SelectedIndex)
            {
                case 1: return ErsTarget.Car1;
                case 2: return ErsTarget.Car2;
                case 3: return ErsTarget.Both;
                default: return ErsTarget.Automatic;
            }
        }
    }

    internal static ErsTarget NextTarget(ErsTarget current)
    {
        switch (current)
        {
            case ErsTarget.Car1: return ErsTarget.Car2;
            case ErsTarget.Car2: return ErsTarget.Both;
            default: return ErsTarget.Car1;
        }
    }

    void CycleTarget()
    {
        ErsTarget next = NextTarget(SelectedTarget);
        target.SelectedIndex = next == ErsTarget.Car1 ? 1 :
            next == ErsTarget.Car2 ? 2 : 3;
        string name = TargetName(next);
        log("Target changed to " + name + ".");
        SetNotice("Target: " + name + ".", true);
    }

    void Execute(HotkeyAction action)
    {
        try
        {
            ErsOperationResult result;
            if (action == HotkeyAction.ToggleLock)
                result = controller.ToggleLock(SelectedTarget);
            else if (action == HotkeyAction.Fill)
                result = controller.SetPercentage(SelectedTarget, 100F);
            else if (action == HotkeyAction.Drain)
                result = controller.SetPercentage(SelectedTarget, 0F);
            else
                throw new InvalidOperationException("This action does not change ERS.");
            log(action + " for " + SelectedTarget + ": " + result.Message);
            SetNotice(result.Message, result.Success);
            UpdateSnapshot(controller.Snapshot);
        }
        catch (Exception exception)
        {
            log("ERS action failed safely: " + exception.Message);
            SetNotice("Action stopped: " + exception.Message, false);
        }
    }

    void RefreshController()
    {
        try
        {
            controller.Refresh();
            UpdateSnapshot(controller.Snapshot);
        }
        catch (Exception exception)
        {
            log("Refresh failed: " + exception.Message);
            SetNotice("Connection stopped: " + exception.Message, false);
        }
    }

    void UpdateSnapshot(ErsSnapshot snapshot)
    {
        connection.Text = snapshot.Supported ? "● CONNECTED" : snapshot.Connected ? "● FOUND" : "● NOT CONNECTED";
        connection.ForeColor = snapshot.Supported ? Color.FromArgb(52, 219, 163) :
            snapshot.Connected ? Color.FromArgb(245, 176, 65) : Color.FromArgb(134, 145, 160);
        game.Text = snapshot.Game.Length == 0 ? "Waiting for F1 Manager" : snapshot.Game;
        lockButton.Enabled = snapshot.Supported;
        fillButton.Enabled = snapshot.Supported;
        drainButton.Enabled = snapshot.Supported;
        UpdateCar(car1Driver, car1Value, car1Gauge, snapshot.Car1);
        UpdateCar(car2Driver, car2Value, car2Gauge, snapshot.Car2);

        if (snapshot.Locked)
        {
            string value = snapshot.LockedPercentage.HasValue ? " at " + snapshot.LockedPercentage.Value.ToString("0.0") + "%" : String.Empty;
            lockState.Text = "●  ERS HELD — " + TargetName(snapshot.LockedTarget) + value;
            lockState.ForeColor = Color.FromArgb(244, 184, 77);
        }
        else
        {
            lockState.Text = "○  ERS HOLD OFF";
            lockState.ForeColor = Color.FromArgb(156, 167, 183);
        }
        lockButton.Text = SelectedTargetIsLocked(snapshot)
            ? "RELEASE ERS" : "HOLD ERS";
        if (DateTime.UtcNow >= noticeUntil) notice.Text = snapshot.Status;
    }

    bool SelectedTargetIsLocked(ErsSnapshot snapshot)
    {
        if (!snapshot.Locked) return false;
        ErsTarget selected = SelectedTarget;
        if (selected == ErsTarget.Automatic || selected == ErsTarget.Both)
            return snapshot.LockedTarget == ErsTarget.Both;
        if (selected == ErsTarget.Car1)
            return snapshot.LockedTarget == ErsTarget.Car1 ||
                snapshot.LockedTarget == ErsTarget.Both;
        return snapshot.LockedTarget == ErsTarget.Car2 ||
            snapshot.LockedTarget == ErsTarget.Both;
    }

    static string TargetName(ErsTarget value)
    {
        switch (value)
        {
            case ErsTarget.Car1: return "Car 1";
            case ErsTarget.Car2: return "Car 2";
            case ErsTarget.Both: return "both cars";
            default: return "automatically selected car";
        }
    }

    static void UpdateCar(Label driver, Label value, ErsGauge gauge, ErsCarState state)
    {
        if (!state.Available)
        {
            driver.Text = "Not found";
            value.Text = "—";
            gauge.Percentage = null;
            return;
        }
        driver.Text = (state.DriverNumber.HasValue ? "#" + state.DriverNumber.Value + "  " : String.Empty) +
            (state.Driver.Length == 0 ? "Car" : state.Driver);
        value.Text = state.Percentage.HasValue ? state.Percentage.Value.ToString("0.0") + "%" : "—";
        gauge.Percentage = state.Percentage;
    }

    void SetNotice(string text, bool success)
    {
        notice.Text = text;
        notice.ForeColor = success ? Color.FromArgb(77, 221, 169) : Color.FromArgb(255, 132, 132);
        noticeUntil = DateTime.UtcNow.AddSeconds(5);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey)
        {
            HotkeyAction action;
            if (HotkeyManager.TryResolve(message.WParam.ToInt32(), out action) && HotkeyAllowed(action))
            {
                if (action == HotkeyAction.ToggleOverlay)
                    overlayToggle.Checked = !overlayToggle.Checked;
                else if (action == HotkeyAction.CycleTarget)
                    CycleTarget();
                else
                    Execute(action);
            }
        }
        base.WndProc(ref message);
    }

    bool HotkeyAllowed(HotkeyAction action)
    {
        IntPtr foreground = WindowNative.GetForegroundWindow();
        if (foreground == Handle)
            return action == HotkeyAction.ToggleOverlay ||
                action == HotkeyAction.CycleTarget ||
                controller.Snapshot.Supported;
        if (!controller.Snapshot.Supported) return false;
        uint processId;
        WindowNative.GetWindowThreadProcessId(foreground, out processId);
        return processId != 0 && controller.AcceptsForegroundProcess((int)processId);
    }

    void RefreshOverlay()
    {
        if (IsDisposed) return;
        try
        {
            overlay.RefreshDisplay(controller.Snapshot, overlayToggle.Checked,
                (OverlayCorner)overlayPosition.SelectedIndex, SelectedTarget);
        }
        catch (Exception exception)
        {
            overlay.Hide();
            if (overlayToggle.Checked) overlayToggle.Checked = false;
            log("Overlay disabled: " + exception.Message);
            SetNotice("Overlay disabled: " + exception.Message, false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        overlayRefresh.Stop();
        overlay.Hide();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Stop();
        overlayRefresh.Stop();
        if (hotkeys != null) hotkeys.Dispose();
        try { controller.ReleaseLock(); }
        catch (Exception exception) { log("Release lock on close failed: " + exception.Message); }
        controller.Dispose();
        refresh.Dispose();
        overlayRefresh.Dispose();
        overlay.Dispose();
        base.OnFormClosed(e);
    }
}
