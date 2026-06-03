using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChoreBuddy.TestApp;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);

        // Self-update applier. Launched as a detached temp-copy by the running
        // service (AgentUpdater): stop the service, swap files, restart it.
        // Form: --apply-update <installDir> <stagingDir>
        var applyIdx = Array.FindIndex(args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (applyIdx >= 0 && applyIdx + 2 < args.Length)
        {
            var installDir = args[applyIdx + 1];
            var stagingDir = args[applyIdx + 2];
            var updLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ChoreBuddy", "update.log");
            void ULog(string m)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(updLog)!);
                    File.AppendAllText(updLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {m}\n");
                }
                catch { }
            }
            try { AgentUpdater.ApplyUpdate(installDir, stagingDir, ULog); }
            catch (Exception ex) { ULog("apply-update FATAL: " + ex); }
            return;
        }

        if (args.Any(a => a.Equals("--service", StringComparison.OrdinalIgnoreCase)))
        {
            RunService(args);
            return;
        }

        if (args.Any(a => a.Equals("--overlay", StringComparison.OrdinalIgnoreCase)))
        {
            OverlayApp.Run();
            return;
        }

        if (args.Any(a => a.Equals("--setup", StringComparison.OrdinalIgnoreCase)))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            ApplicationConfiguration.Initialize();
            Application.Run(new SetupWizard());
            return;
        }

        // IFEO debugger redirect: when Windows launches a blocked game,
        // it actually runs `agent.exe --banned <path-to-original.exe>`.
        // Pop a friendly "you're banned" card and exit.
        var bannedIdx = Array.FindIndex(args, a => a.Equals("--banned", StringComparison.OrdinalIgnoreCase));
        if (bannedIdx >= 0)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            var attempted = bannedIdx + 1 < args.Length ? args[bannedIdx + 1] : "";
            LogTrace($"--banned entered. attempted='{attempted}', argc={args.Length}, args=[{string.Join(" | ", args)}]");
            try { BannedPopupForm.Run(attempted); LogTrace("BannedPopupForm.Run returned cleanly"); }
            catch (Exception ex) { LogCrash(ex); }
            return;
        }

        Application.ThreadException += (s, e) => LogCrash(e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        try
        {
            ApplicationConfiguration.Initialize();

            // CLI shortcuts: --admin → MainForm, --setup → wizard. With
            // no flag, show a launcher with two big buttons so you can
            // pick. First-time / unsigned users still get nudged to the
            // wizard automatically (no point in showing the picker if
            // there's nothing to manage yet).
            var forceAdmin = args.Any(a => a.Equals("--admin", StringComparison.OrdinalIgnoreCase));
            var cfg = ConfigStore.Load();
            var firstRun = string.IsNullOrEmpty(cfg.KidId) || string.IsNullOrEmpty(cfg.RefreshToken);

            if (forceAdmin)
            {
                Application.Run(new MainForm());
            }
            else if (firstRun)
            {
                Application.Run(new SetupWizard());
            }
            else
            {
                var launcher = new LauncherForm();
                Application.Run(launcher);
                if (launcher.Choice == LauncherChoice.Wizard)
                    Application.Run(new SetupWizard());
                else if (launcher.Choice == LauncherChoice.MainForm)
                    Application.Run(new MainForm());
                // None → user closed the picker, exit silently.
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            MessageBox.Show($"Startup error:\n\n{ex.GetType().Name}: {ex.Message}\n\nLog written to:\n{LogPath}",
                "ChoreBuddy crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void RunService(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(opts =>
            {
                opts.ServiceName = "ChoreBuddyAgent";
            })
            .ConfigureServices(services =>
            {
                services.AddHostedService<AgentService>();
            })
            .Build();
        host.Run();
    }

    static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy", "crash.log");

    /** Also dump to the user's TEMP so non-admin --banned invocations
     *  leave a trace (kid can't write to ProgramData). */
    static string UserLogPath => Path.Combine(
        Path.GetTempPath(), "ChoreBuddyAgent-crash.log");

    static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        var msg = $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n{ex}\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, msg);
        }
        catch { }
        try { File.AppendAllText(UserLogPath, msg); } catch { }
    }

    /** Diagnostic — write a breadcrumb to the user's TEMP, used by
     *  --banned mode to confirm it actually started. */
    public static void LogTrace(string msg)
    {
        try
        {
            File.AppendAllText(UserLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TRACE: {msg}\n");
        }
        catch { }
    }
}
