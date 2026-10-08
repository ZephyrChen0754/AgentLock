using System.Diagnostics;
using System.Security.Principal;
using AgentLock.Ui;

namespace AgentLock;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => RuntimeState.FailSafe(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RuntimeState.FailSafe(e.ExceptionObject as Exception);
        if (args.Length >= 3 && args[0] == "--watchdog")
        {
            Watchdog.Run(int.Parse(args[1]), args[2], args.Contains("--dry-run"));
            return;
        }
        if (args.Contains("--diagnostics"))
        {
            Application.Run(new DiagnosticForm(args));
            return;
        }
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(true, "Local\\AgentLock-" + sid, out bool created);
        if (!created)
        {
            MessageBox.Show("AgentLock 已在运行。请在任务栏右下角打开，或在保护中按 F12 解锁。", "AgentLock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var main = new MainForm();
            if (args.Contains("--smoke"))
            {
                main.Shown += async (_, _) =>
                {
                    await Task.Delay(800);
                    int index = Array.IndexOf(args, "--output");
                    string output = index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : Path.Combine(RuntimeState.Root, "diagnostics", "startup");
                    main.SaveSmokeReport(output);
                    main.CloseForSmoke();
                };
            }
            Application.Run(main);
        }
        catch (Exception e)
        {
            RuntimeState.FailSafe(e);
            MessageBox.Show("程序遇到错误，已尝试切回 Windows 锁屏保护。\n" + e.Message, "AgentLock", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
