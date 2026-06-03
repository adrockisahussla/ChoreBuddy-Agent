using System;
using System.Diagnostics;
using System.Threading;

namespace ChoreBuddy.TestApp;

/** Cleanly restarts Steam after an ALLOW.
 *
 *  SHUTOFF hard-kills steam.exe (Process.Kill, not a graceful `steam -shutdown`).
 *  That leaves the "Steam Client Service" (steamservice.exe, SYSTEM, auto-restarted
 *  by the SCM) and orphaned steamwebhelper children alive. Launching a fresh
 *  steam.exe into that dirty state makes Steam come up in Offline mode
 *  ("Not logged into Steam"), which then fails every Steam game's init.
 *
 *  So before relaunching we tear ALL of Steam down — processes + the service —
 *  let handles settle, then start steam.exe fresh so it boots clean and signs in
 *  online. steam.exe restarts its own service and helpers on launch. */
public static class SteamRestarter
{
    static readonly string[] SteamProcesses = { "steam", "steamwebhelper", "steamservice" };
    const string SteamServiceName = "Steam Client Service";

    public static void CleanRestart(string steamExePath, Action<string> log)
    {
        // 1. Stop the Steam Client Service first so the SCM doesn't respawn
        //    steamservice.exe out from under us while we're killing processes.
        try
        {
            var (output, exit) = RunSc($"stop \"{SteamServiceName}\"");
            log($"SteamRestarter: stop service exit={exit}");
        }
        catch (Exception ex) { log($"SteamRestarter: stop service failed: {ex.Message}"); }

        // 2. Kill every lingering Steam process.
        int killed = 0;
        foreach (var name in SteamProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(entireProcessTree: true); killed++; }
                catch { }
                finally { p.Dispose(); }
            }
        }
        log($"SteamRestarter: killed {killed} Steam process(es)");

        // 3. Let file/socket handles release before a fresh boot.
        Thread.Sleep(2000);

        // 4. Start steam.exe fresh in the kid's interactive session.
        ProcessLauncher.LaunchInActiveSession(steamExePath, log);
    }

    static (string output, int exit) RunSc(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return (stdout + stderr, p.ExitCode);
    }
}
