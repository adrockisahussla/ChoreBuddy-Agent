using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ChoreBuddy.TestApp;

public static class FirewallManager
{
    const string RulePrefix = "ChoreBuddy_";
    const string IfeoKeyBase = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /** Path to our own agent .exe. When Windows IFEO intercepts a launch
     *  of a blocked game, it actually runs:
     *      "<this exe>" --banned   <path-to-original-game.exe>
     *  Our --banned handler pops a friendly "you're banned" card.
     *  Falls back to systray.exe (silent no-op) if for some reason the
     *  agent path can't be resolved — keeps the block working even
     *  without the popup. */
    static string LaunchBlockerCommand
    {
        get
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    return $"\"{exe}\" --banned";
            }
            catch { }
            return @"C:\Windows\System32\systray.exe";
        }
    }

    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int KillRelatedProcesses(string exePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(exePath);
        var killed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return killed;
    }

    public static void Block(string appKey, string exePath)
    {
        KillRelatedProcesses(exePath);
        SetLaunchBlocked(exePath, true);
        EnsureBlockRule(RulePrefix + appKey, exePath, enabled: true);
    }

    public static void Unblock(string appKey, string exePath)
    {
        SetLaunchBlocked(exePath, false);
        // DISABLE the rule in place — never delete it. A disabled outbound
        // block lets all traffic through, so the app is fully unblocked, but
        // we avoid the delete/add churn that corrupted the Windows firewall
        // policy store and left Steam stranded offline. The rule just sits
        // there disabled and gets re-enabled on the next SHUTOFF.
        SetRuleEnabled(RulePrefix + appKey, false);
    }

    public static bool IsBlocked(string appKey, string exePath)
    {
        var name = RulePrefix + appKey;
        var (output, exit) = RunNetshRaw($"firewall show rule name=\"{name}\"");
        var firewallBlocked = exit == 0 && output.Contains("Enabled:") && output.Contains("Yes");
        var launchBlocked = IsLaunchBlocked(exePath);
        return firewallBlocked || launchBlocked;
    }

    static void SetLaunchBlocked(string exePath, bool blocked)
    {
        var fileName = Path.GetFileName(exePath);
        var keyPath = $@"{IfeoKeyBase}\{fileName}";

        if (blocked)
        {
            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true)
                ?? throw new InvalidOperationException($"Couldn't create IFEO key for {fileName}");
            key.SetValue("Debugger", LaunchBlockerCommand, RegistryValueKind.String);
            key.SetValue("ChoreBuddyManaged", 1, RegistryValueKind.DWord);
        }
        else
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            if (key == null) return;
            var ours = key.GetValue("ChoreBuddyManaged") != null;
            key.Close();
            if (ours)
            {
                Registry.LocalMachine.DeleteSubKey(keyPath, throwOnMissingSubKey: false);
            }
        }
    }

    static bool IsLaunchBlocked(string exePath)
    {
        var fileName = Path.GetFileName(exePath);
        using var key = Registry.LocalMachine.OpenSubKey($@"{IfeoKeyBase}\{fileName}");
        if (key == null) return false;
        return key.GetValue("Debugger") != null && key.GetValue("ChoreBuddyManaged") != null;
    }

    /// Ensure a single outbound-block rule of this name exists and sits in the
    /// requested enable state. Creates it ONCE if missing; otherwise just flips
    /// the enable flag. Never deletes — that's what kept corrupting the policy
    /// store and producing undeletable 0x2 orphans.
    static void EnsureBlockRule(string name, string exePath, bool enabled)
    {
        if (RuleExists(name))
        {
            SetRuleEnabled(name, enabled);
        }
        else
        {
            var en = enabled ? "yes" : "no";
            RunNetsh($"firewall add rule name=\"{name}\" dir=out action=block program=\"{exePath}\" enable={en}");
        }
    }

    /// Flip a rule's enable flag in place (no delete/add). Safe no-op if the
    /// rule doesn't exist.
    static void SetRuleEnabled(string name, bool enabled)
    {
        if (!RuleExists(name)) return;
        var en = enabled ? "yes" : "no";
        RunNetsh($"firewall set rule name=\"{name}\" new enable={en}", allowFailure: true);
    }

    static bool RuleExists(string name)
    {
        var (_, exit) = RunNetshRaw($"firewall show rule name=\"{name}\"");
        return exit == 0; // non-zero => "No rules match the specified criteria."
    }

    /// Returns every distinct firewall rule name starting with "ChoreBuddy_"
    /// currently present in the firewall (whatever store netsh reports).
    public static List<string> ListAllRuleNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (output, _) = RunNetshRaw("firewall show rule name=all");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf("Rule Name:", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var name = line.Substring(idx + "Rule Name:".Length).Trim();
            if (name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                names.Add(name);
        }
        return names;
    }

    /// DISABLE every ChoreBuddy block rule (set enable=no), regardless of who
    /// created it or whether it's tracked in config. Called on ALLOW so an
    /// out-of-sync RemotelyBlocked list can never leave an enabled orphan
    /// blocking traffic. Non-destructive (no deletes) → can't corrupt the
    /// store. Returns the rule names it touched.
    public static List<string> DisableAllRules(Action<string>? log = null)
    {
        var touched = new List<string>();
        foreach (var name in ListAllRuleNames())
        {
            try { SetRuleEnabled(name, false); touched.Add(name); }
            catch (Exception ex) { log?.Invoke($"DisableAll: {name} failed: {ex.Message}"); }
        }
        return touched;
    }

    static void RunNetsh(string args, bool allowFailure = false)
    {
        var (output, exit) = RunNetshRaw(args);
        if (exit != 0 && !allowFailure)
        {
            throw new InvalidOperationException(
                $"netsh failed (exit {exit}). Output:\n{output.Trim()}");
        }
    }

    static (string output, int exit) RunNetshRaw(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = "advfirewall " + args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (stdout + stderr, p.ExitCode);
    }
}
