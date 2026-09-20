using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    internal enum Mode { Gui, SelfTest }

    internal static Mode ParseArguments(string[] args)
    {
        if (args.Length == 0) return Mode.Gui;
        if (args.Length == 1 && args[0] == "--self-test") return Mode.SelfTest;
        throw new ArgumentException("Use no arguments, or --self-test.");
    }

    static readonly object LogLock = new object();

    internal static void Log(string text)
    {
        lock (LogLock)
        {
            string line = DateTimeOffset.Now.ToString("o") + " " + text;
            Console.WriteLine(line);
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "F1 ERS Manager.log"), line + Environment.NewLine); }
            catch (IOException exception) { Debug.WriteLine("Log failed: " + exception.Message); }
            catch (UnauthorizedAccessException exception) { Debug.WriteLine("Log failed: " + exception.Message); }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Mode mode = ParseArguments(args);
            if (mode == Mode.SelfTest) return SelfTests.Run();

            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "F1 ERS Manager.log"),
                DateTimeOffset.Now.ToString("o") + " Starting " + AppVersion.WindowTitle + Environment.NewLine);
            bool created;
            using (var mutex = new Mutex(true, @"Local\F1ManagerERSManager", out created))
            {
                if (!created) throw new InvalidOperationException("F1 ERS Manager is already running.");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (IErsController controller = ControllerFactory.Create(Log))
                using (var form = new MainForm(controller, Log))
                    Application.Run(form);
                return 0;
            }
        }
        catch (Exception exception)
        {
            var win32 = exception as Win32Exception;
            string message = exception.Message + (win32 == null ? String.Empty : " (Windows error " + win32.NativeErrorCode + ")");
            Log("ERROR: " + message);
            if (args.Length == 0)
                MessageBox.Show(message, AppVersion.WindowTitle + " — stopped safely", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
