using System;
using System.Drawing;

internal static class SelfTests
{
    static int passed;

    internal static int Run()
    {
        try
        {
            Equal("F8", HotkeySettings.Defaults[HotkeyAction.ToggleLock].Text, "default lock hotkey");
            Equal("F9", HotkeySettings.Defaults[HotkeyAction.Fill].Text, "default fill hotkey");
            Equal("F10", HotkeySettings.Defaults[HotkeyAction.Drain].Text, "default drain hotkey");
            Equal("F11", HotkeySettings.Defaults[HotkeyAction.ToggleOverlay].Text, "default overlay hotkey");
            Equal("Ctrl+F12", HotkeySettings.Defaults[HotkeyAction.CycleTarget].Text, "default target-cycle hotkey");
            HotkeySettings parsed = HotkeySettings.Parse(HotkeySettings.Defaults.Serialize());
            Equal("F10", parsed[HotkeyAction.Drain].Text, "settings round trip");
            Equal("F11", parsed[HotkeyAction.ToggleOverlay].Text, "overlay setting round trip");
            Equal("Ctrl+F12", parsed[HotkeyAction.CycleTarget].Text, "target-cycle setting round trip");
            HotkeySettings legacy = HotkeySettings.Parse(
                "ToggleLock=F8\nFill=F9\nDrain=F10\n");
            Equal("F11", legacy[HotkeyAction.ToggleOverlay].Text,
                "version 1.0 settings gain default overlay hotkey");
            Equal("Ctrl+F12", legacy[HotkeyAction.CycleTarget].Text,
                "version 1.0 settings gain default target-cycle hotkey");
            HotkeySettings legacyF11 = HotkeySettings.Parse(
                "ToggleLock=F11\nFill=F9\nDrain=F10\n");
            Equal("Ctrl+F11", legacyF11[HotkeyAction.ToggleOverlay].Text,
                "legacy F11 binding is preserved during migration");
            Equal("Ctrl+F12", legacyF11[HotkeyAction.CycleTarget].Text,
                "legacy F11 binding receives the next two free migration hotkeys");
            HotkeySettings version11 = HotkeySettings.Parse(
                "ToggleLock=F8\nFill=F9\nDrain=F10\nToggleOverlay=F11\n");
            Equal("Ctrl+F12", version11[HotkeyAction.CycleTarget].Text,
                "version 1.1 settings gain default target-cycle hotkey");
            HotkeySettings legacyCtrlF12 = HotkeySettings.Parse(
                "ToggleLock=Ctrl+F12\nFill=F9\nDrain=F10\nToggleOverlay=F11\n");
            Equal("Shift+F12", legacyCtrlF12[HotkeyAction.CycleTarget].Text,
                "occupied target-cycle default migrates to a free shortcut");
            HotkeyAction resolvedAction;
            Check(HotkeyManager.TryResolve(4703, out resolvedAction) &&
                resolvedAction == HotkeyAction.ToggleOverlay,
                "overlay hotkey id resolves");
            Check(HotkeyManager.TryResolve(4704, out resolvedAction) &&
                resolvedAction == HotkeyAction.CycleTarget,
                "target-cycle hotkey id resolves");
            Check(!HotkeyManager.TryResolve(4705, out resolvedAction),
                "unknown hotkey id rejected");
            Equal("Ctrl+Shift+K", Shortcut.Parse("Ctrl+Shift+K").Text, "modified hotkey");
            Throws(delegate { HotkeySettings.Parse("ToggleLock=F8\nFill=F8\nDrain=F10\n"); }, "duplicate hotkey");
            using (IErsController controller = new SafeErsController(delegate { }))
            {
                ErsOperationResult result = controller.SetPercentage(ErsTarget.Both, 100F);
                Check(!result.Success, "safe fallback refuses writes");
                Check(!controller.Snapshot.Locked, "safe fallback never locks");
            }
            Equal(ErsTarget.Both,
                F1M23ErsController.NormalizeTarget(ErsTarget.Automatic),
                "automatic targets both cars");
            Equal(ErsTarget.Car1, MainForm.NextTarget(ErsTarget.Automatic),
                "target cycle starts with car 1");
            Equal(ErsTarget.Car2, MainForm.NextTarget(ErsTarget.Car1),
                "target cycle advances to car 2");
            Equal(ErsTarget.Both, MainForm.NextTarget(ErsTarget.Car2),
                "target cycle advances to both cars");
            Equal(ErsTarget.Car1, MainForm.NextTarget(ErsTarget.Both),
                "target cycle wraps to car 1");
            Equal("AUTO", ErsOverlay.TargetLabel(ErsTarget.Automatic),
                "overlay automatic target label");
            Equal("CAR 1", ErsOverlay.TargetLabel(ErsTarget.Car1),
                "overlay car 1 target label");
            Equal("CAR 2", ErsOverlay.TargetLabel(ErsTarget.Car2),
                "overlay car 2 target label");
            Equal("BOTH", ErsOverlay.TargetLabel(ErsTarget.Both),
                "overlay both-cars target label");
            byte[] both = F1M23ErsController.TargetIds(ErsTarget.Both);
            Check(both.Length == 2 && both[0] == 1 && both[1] == 2,
                "both target ids");
            byte[] car2 = F1M23ErsController.TargetIds(ErsTarget.Car2);
            Check(car2.Length == 1 && car2[0] == 2, "car 2 target id");
            Equal(0, F1M23ErsController.PercentageToRaw(0F),
                "zero battery value");
            Equal(100, F1M23ErsController.PercentageToRaw(100F),
                "full battery value");
            Check(F1M23ErsController.IsValidBattery(0) &&
                F1M23ErsController.IsValidBattery(100),
                "presentation battery endpoints valid");
            Check(!F1M23ErsController.IsValidBattery(-1) &&
                !F1M23ErsController.IsValidBattery(101),
                "presentation battery outside range rejected");
            Check(F1M23ErsController.IsSupportedHash(
                F1M23ErsController.SupportedSha256.ToUpperInvariant()),
                "build hash comparison");
            Check(F1M23ErsController.IsValidVehicleTelemetry(
                1, 67.419F, 0.503F), "vehicle telemetry valid");
            Check(!F1M23ErsController.IsValidVehicleTelemetry(
                0, 67.419F, 0.503F), "vehicle marker rejected");
            Check(!F1M23ErsController.IsValidVehicleTelemetry(
                1, Single.NaN, 0.503F), "non-finite fuel rejected");
            Check(!F1M23ErsController.IsValidVehicleTelemetry(
                1, 67.419F, 1.01F), "battery overflow rejected");
            Check(F1M24ErsController.IsSupportedHash(
                F1M24ErsController.SupportedSha256.ToUpperInvariant()),
                "F1M24 build hash comparison");
            Check(F1M24ErsController.IsValidVehicleTelemetry(
                67.419F, 0.503F), "F1M24 telemetry valid");
            Check(!F1M24ErsController.IsValidVehicleTelemetry(
                Single.NaN, 0.503F), "F1M24 non-finite fuel rejected");
            Check(!F1M24ErsController.IsValidVehicleTelemetry(
                67.419F, 1.01F), "F1M24 battery overflow rejected");
            TestOverlaySafetyAndPositioning();
            TestF1M24SaveFixture();
            TestMultiGameSelection();
            ThrowsOutOfRange(delegate {
                F1M23ErsController.PercentageToRaw(Single.NaN);
            }, "NaN percentage rejected");
            Equal(Program.Mode.SelfTest, Program.ParseArguments(new[] { "--self-test" }), "self test argument");
            Console.WriteLine("All " + passed + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("SELF-TEST FAILED: " + exception.Message);
            return 1;
        }
    }

    static void TestOverlaySafetyAndPositioning()
    {
        const int processId = 4242;
        const long frequency = 1000;
        const long stamp = 5000;
        ErsSnapshot snapshot = new ErsSnapshot(true, true,
            "F1 Manager 2023", "test",
            new ErsCarState(true, "Car 1", 1, 48.5F),
            new ErsCarState(true, "Car 2", 2, 72.3F),
            true, ErsTarget.Car1, 48.5F, processId, stamp);
        Check(ErsOverlay.CanDisplay(snapshot, true, processId,
            stamp + 799, frequency), "fresh verified overlay snapshot accepted");
        Check(!ErsOverlay.CanDisplay(snapshot, true, processId + 1,
            stamp + 100, frequency), "overlay rejects other foreground process");
        Check(!ErsOverlay.CanDisplay(snapshot, true, processId,
            stamp + 801, frequency), "overlay rejects stale snapshot");
        Check(!ErsOverlay.CanDisplay(snapshot, false, processId,
            stamp + 100, frequency), "disabled overlay stays hidden");

        var gameArea = new Rectangle(100, 200, 1920, 1080);
        Rectangle topLeft = ErsOverlay.CalculateBounds(gameArea,
            OverlayCorner.TopLeft, 96);
        Rectangle topRight = ErsOverlay.CalculateBounds(gameArea,
            OverlayCorner.TopRight, 96);
        Rectangle bottomLeft = ErsOverlay.CalculateBounds(gameArea,
            OverlayCorner.BottomLeft, 96);
        Rectangle topCenter = ErsOverlay.CalculateBounds(gameArea,
            OverlayCorner.TopCenter, 96);
        Rectangle leftHudGap = ErsOverlay.CalculateBounds(gameArea,
            OverlayCorner.LeftHudGap, 96);
        Equal(new Rectangle(112, 284, 300, 48), topLeft,
            "overlay top-left bounds");
        Equal(new Rectangle(1708, 284, 300, 48), topRight,
            "overlay top-right bounds");
        Equal(new Rectangle(112, 1148, 300, 48), bottomLeft,
            "overlay bottom-left bounds");
        Equal(910, topCenter.X, "overlay top-center position");
        Equal(new Rectangle(112, 974, 300, 48), leftHudGap,
            "overlay left HUD gap bounds");
        Check(ErsOverlay.CalculateBounds(new Rectangle(0, 0, 200, 40),
            OverlayCorner.TopLeft, 96).IsEmpty,
            "overlay hides when game area is too small");
    }

    static void TestF1M24SaveFixture()
    {
        const string encoded =
            "U1lOVEhFVElDLUYxTTI0AAAFAAAATm9uZQAFAAAATm9uZQAAAAAAzQIAAAASAAAAAAAAAAAAAHja7VjNaxNBFH9vZjebXUwhhxJ66hyTJrWKZ6mx2UowTT+yAXsK02ZWFpJNu7uV9hj10H/A/6TgoQcv3nqqHj2Kh9LSgigU60Fm06xJaBVUUEN+sLvzPoaZee/Ne4+tLJecQDC75TV5wO4AAUS4xxgAaACgw3col08XCD+HBjdfHKhSGS8kreMF7MOfwawSS1WmERy3Lrb9zYYTiBrfClohXasE3LZrBc95IrwCD3jt9iBHbZdQS01M4FM/4GsNsdTgO8LrvMncipm3TGbl75dM1uGxtMHmHc8PyrwpmGU+slh50WLlaqmUM1iJXyOwBG8WC6xYtswH5gormPP5aimSVl1nc0tUhKhHGt25keqtnMGWVooL+ZVV9tBcZeloF7lo2YyRyZJYanbiOnt0zlC73fnSjgljNBH65gjwCEYYZoyjOj6JxBJ+sOQ57rqzwRtMeWOE/j8FPB2ZaPiRVMYn9b4QkPef4hngBX7Gs5GFhhz0eZoCKDYFJDRJQTdC/58AfsFPeDIy0DBDo4SQNEmrFNG25TuZTMA+UJgC+IoHMAX7eH7d7PYY1VLT09heDLulwW5qkFb6OqhBqeylQl5PZ1QtF5erZm+nkzNY3vedx66oz3GvvNVcE15X3cjgOeRH/d+v939x6VWZ/1XcA/yI7/Etvsa90T351zGmaihBZigjde0koWgkjjoSXGB17XiMShKRkDSjde04QTQMaVtqH99ASepIklLZCP1/CHiOh3iG7/DDyL7/NXRVhsYMZaquhmGwwJTOSIZDXJWhYDOiyYGeZAl8CRR2AV/BLm7/vW23m6qWymbxWbKnusy13MDj64E/QGpX1JZIeEVpyRmsK7Z2NkQPuz85yzzb8ouu5PbwKgH3ggLf6WGZbr0iuN9ye6f2pujLHeT6Fs5drpcxMndjsdRy9sfFKzpSt3ZFjHg3ff/2P5Bv/vquVw==";
        byte[] save = Convert.FromBase64String(encoded);
        F1M24PlayerTeamInfo profile;
        Check(F1M24SaveTeamReader.TryReadTeamInfoFromSave(save,
            "synthetic.sav", new DateTime(2026, 1, 2, 3, 4, 5,
                DateTimeKind.Utc), out profile), "F1M24 save profile parsed");
        Equal(32, profile.TeamId, "F1M24 save TeamID");
        Equal(17, profile.DriverOneStaffId, "F1M24 save driver one");
        Equal(102, profile.DriverTwoStaffId, "F1M24 save driver two");

        byte[] corrupted = (byte[])save.Clone();
        corrupted[corrupted.Length - 1] ^= 1;
        Check(!F1M24SaveTeamReader.TryReadTeamInfoFromSave(corrupted,
            "corrupt.sav", DateTime.UtcNow, out profile),
            "F1M24 corrupt save rejected");
    }

    static void TestMultiGameSelection()
    {
        var controller23 = new FakeController(
            ErsSnapshot.Disconnected("no F1M23"), 23);
        var controller24 = new FakeController(SupportedSnapshot(
            "F1 Manager 2024"), 24);
        using (var combined = new MultiGameErsController(
            controller23, controller24))
        {
            combined.Refresh();
            Equal("F1 Manager 2024", combined.Snapshot.Game,
                "F1M24 controller selected");
            Check(combined.SetPercentage(ErsTarget.Both, 100F).Success &&
                controller24.SetCount == 1 && controller23.SetCount == 0,
                "operation delegated to selected game");

            controller23.State = SupportedSnapshot("F1 Manager 2023");
            combined.Refresh();
            Check(combined.Snapshot.Connected &&
                !combined.Snapshot.Supported,
                "simultaneous games fail closed");
            Check(!combined.SetPercentage(ErsTarget.Both, 0F).Success,
                "simultaneous game write refused");
            Check(!combined.AcceptsForegroundProcess(23) &&
                !combined.AcceptsForegroundProcess(24),
                "simultaneous game hotkeys refused");
        }
    }

    static ErsSnapshot SupportedSnapshot(string game)
    {
        return new ErsSnapshot(true, true, game, "test",
            new ErsCarState(true, "Car 1", 1, 50F),
            new ErsCarState(true, "Car 2", 2, 50F),
            false, ErsTarget.Automatic, null);
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        passed++;
    }

    static void Equal(object expected, object actual, string name)
    {
        Check(Object.Equals(expected, actual), name + ": expected " + expected + ", got " + actual);
    }

    static void Throws(Action action, string name)
    {
        try { action(); }
        catch (ArgumentException) { passed++; return; }
        throw new InvalidOperationException(name + ": exception expected");
    }

    static void ThrowsOutOfRange(Action action, string name)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { passed++; return; }
        throw new InvalidOperationException(name + ": exception expected");
    }

    sealed class FakeController : IErsController
    {
        readonly int acceptedProcessId;
        internal ErsSnapshot State;
        internal int SetCount;
        bool disposed;

        internal FakeController(ErsSnapshot state, int processId)
        {
            State = state;
            acceptedProcessId = processId;
        }

        public ErsSnapshot Snapshot { get { return State; } }
        public void Refresh() { }
        public ErsOperationResult ToggleLock(ErsTarget target)
        {
            return disposed ? ErsOperationResult.Failed("disposed") :
                ErsOperationResult.Ok("ok");
        }
        public ErsOperationResult SetPercentage(ErsTarget target, float percentage)
        {
            if (disposed) return ErsOperationResult.Failed("disposed");
            SetCount++;
            return ErsOperationResult.Ok("ok");
        }
        public ErsOperationResult ReleaseLock()
        {
            return ErsOperationResult.Ok("ok");
        }
        public bool AcceptsForegroundProcess(int processId)
        {
            return !disposed && processId == acceptedProcessId;
        }
        public void Dispose() { disposed = true; }
    }
}
