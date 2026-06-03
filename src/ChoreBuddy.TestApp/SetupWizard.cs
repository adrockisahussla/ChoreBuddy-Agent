using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace ChoreBuddy.TestApp;

public class SetupWizard : Form
{
    WebView2 _webview = null!;
    WizardApi _api = null!;

    public SetupWizard()
    {
        Text = "ChoreBuddy Agent — Setup";
        Size = new System.Drawing.Size(820, 760);
        MinimumSize = new System.Drawing.Size(640, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(15, 17, 23);

        _webview = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webview);

        Shown += async (s, e) => await InitAsync();
    }

    public void CloseFromApi()
    {
        BeginInvoke(() => Close());
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(Path.GetTempPath(), "ChoreBuddyWizardWebView2"));
            await _webview.EnsureCoreWebView2Async(env);

            _api = new WizardApi(_webview, this);
            _webview.CoreWebView2.AddHostObjectToScript("agent", _api);

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "wizard-ui", "index.html");
            if (!File.Exists(htmlPath))
            {
                MessageBox.Show($"Wizard UI files missing at:\n{htmlPath}\n\nRebuild the project.",
                    "Setup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            _webview.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webview.Source = new Uri(htmlPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 init failed:\n\n{ex.Message}\n\nMake sure Microsoft Edge WebView2 Runtime is installed.",
                "Setup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class WizardApi
{
    readonly WebView2 _webview;
    readonly SetupWizard _form;

    public WizardApi(WebView2 webview, SetupWizard form)
    {
        _webview = webview;
        _form = form;
    }

    public bool IsAdmin()
    {
        return FirewallManager.IsRunningAsAdmin();
    }

    public void CloseWizard()
    {
        _form.CloseFromApi();
    }

    /** Returns JSON: { signedIn, managerUid, managerName, familyId }. */
    public string GetAuthStatus()
    {
        var cfg = ConfigStore.Load();
        var auth = new AuthClient(cfg);
        return JsonSerializer.Serialize(new
        {
            signedIn = auth.IsSignedIn,
            managerUid = cfg.ManagerUid,
            managerName = cfg.ManagerName,
            familyId = cfg.FamilyId,
        });
    }

    /** Opens the OS browser for Google OAuth, blocks until the manager
     *  finishes (or cancels). Returns JSON: { ok, error?, managerName?,
     *  familyId? }. Bridge calls from JS are sync-style so we marshal
     *  the async work onto a thread pool. */
    public string SignInWithGoogle()
    {
        try
        {
            var cfg = ConfigStore.Load();
            var auth = new AuthClient(cfg);
            var result = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                return await auth.SignInWithGoogleAsync(cts.Token);
            }).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                managerUid = result.ManagerUid,
                managerName = result.ManagerName,
                email = result.Email,
                familyId = result.FamilyId,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    public string GetBuddies()
    {
        var fetched = Task.Run(async () =>
        {
            var cfg = ConfigStore.Load();
            var auth = new AuthClient(cfg);
            var fs = new FirestoreClient(auth, cfg);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            return await fs.GetBuddiesAsync(cts.Token);
        }).GetAwaiter().GetResult();

        var list = fetched.Select(b => new
        {
            id = b.Uid,
            name = b.DisplayName,
            avatar = b.Avatar
        }).ToList<object>();

        if (list.Count == 0)
        {
            list.Add(new { id = "Kid1", name = "Buddy 1 (default)", avatar = "B" });
            list.Add(new { id = "Kid2", name = "Buddy 2 (default)", avatar = "B" });
        }
        return JsonSerializer.Serialize(list);
    }

    public string GetInstalledApps()
    {
        var list = KnownApps.All().Select(a => new
        {
            key = a.Key,
            label = a.Label,
            path = a.Path,
            isLauncher = a.IsLauncher,
            installed = a.Installed
        });
        return JsonSerializer.Serialize(list);
    }

    public string PickCustomAppPath()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe",
                Title = "Pick an .exe to control"
            };
            if (dlg.ShowDialog() == DialogResult.OK) result = dlg.FileName;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result ?? "";
    }

    public void RunInstall(string payloadJson)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DoInstall(payloadJson);
            }
            catch (Exception ex)
            {
                Log("err", $"FATAL: {ex.Message}");
                Status("Install failed", "error");
                Done(false);
            }
        });
    }

    record InstallPayload(string buddyId, List<InstallAppEntry> apps);
    record InstallAppEntry(string key, string label, string path, bool isLauncher, bool remoteEnabled, bool killRelated);

    async Task DoInstall(string payloadJson)
    {
        var payload = JsonSerializer.Deserialize<InstallPayload>(payloadJson)!;

        Status("Locating agent .exe...", "info");
        var exe = Assembly.GetExecutingAssembly().Location;
        if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            exe = Path.ChangeExtension(exe, ".exe");
        Log("info", $"Agent: {exe}");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Agent .exe not found at " + exe);

        Status("Creating Windows Service...", "info");
        await RunProcessQuiet("sc.exe", "stop ChoreBuddyAgent", ignoreErr: true);
        await Task.Delay(1500);
        await RunProcessQuiet("sc.exe", "delete ChoreBuddyAgent", ignoreErr: true);
        await Task.Delay(1500);

        Log("info", "Creating service ChoreBuddyAgent...");
        await RunProcess("sc.exe", $"create ChoreBuddyAgent binPath= \"\\\"{exe}\\\" --service\" start= auto displayName= \"ChoreBuddy Firewall Agent\"");
        await RunProcess("sc.exe", "description ChoreBuddyAgent \"Polls ChoreBuddy cloud for app shutoff commands.\"");
        await RunProcess("sc.exe", "failure ChoreBuddyAgent reset= 86400 actions= restart/5000/restart/5000/restart/30000");
        Log("info", "Starting service...");
        await RunProcess("sc.exe", "start ChoreBuddyAgent");

        Status("Registering watchdog task...", "info");
        var watchdogCmd =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Unregister-ScheduledTask -TaskName 'ChoreBuddyAgentWatchdog' -Confirm:$false;" +
            "$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -Command \"if ((Get-Service -Name ChoreBuddyAgent -ErrorAction SilentlyContinue).Status -ne ''Running'') { Start-Service -Name ChoreBuddyAgent }\"';" +
            "$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 1);" +
            "$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;" +
            "$settings = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable;" +
            "Register-ScheduledTask -TaskName 'ChoreBuddyAgentWatchdog' -Action $action -Trigger $trigger -Principal $principal -Settings $settings | Out-Null";
        await RunProcessQuiet("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"" + watchdogCmd.Replace("\"", "\\\"") + "\"");
        Log("ok", "Watchdog task registered");

        Status("Registering overlay autostart...", "info");
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            key!.SetValue("ChoreBuddyOverlay", $"\"{exe}\" --overlay");
        }
        Log("ok", "HKCU\\Run\\ChoreBuddyOverlay set");

        try
        {
            Process.Start(new ProcessStartInfo(exe, "--overlay") { UseShellExecute = false });
            Log("ok", "Overlay started in this session");
        }
        catch (Exception ex) { Log("err", "Overlay start failed: " + ex.Message); }

        Status("Pairing to buddy...", "info");
        var machineId = Environment.MachineName;
        var cfgForInstall = ConfigStore.Load();
        var authForInstall = new AuthClient(cfgForInstall);
        var fs = new FirestoreClient(authForInstall, cfgForInstall);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await fs.HeartbeatAsync(machineId, cts.Token);
        await PairToBuddy(machineId, payload.buddyId, authForInstall);
        Log("ok", $"Paired {machineId} → {payload.buddyId}");

        Status("Pushing app config...", "info");
        int pushed = 0;
        foreach (var app in payload.apps)
        {
            try
            {
                await fs.PushAppConfigAsync(machineId, app.key,
                    new AppConfigEntry(app.remoteEnabled, app.killRelated), cts.Token);
                Log("ok", $"  {app.label}: remote={app.remoteEnabled}" + (app.isLauncher ? $", killGames={app.killRelated}" : ""));
                pushed++;
            }
            catch (Exception ex) { Log("err", $"  {app.label}: {ex.Message}"); }
        }
        Log("info", $"Pushed {pushed} app configs");

        var cfg = ConfigStore.Load();
        cfg.KidId = payload.buddyId;
        ConfigStore.Save(cfg);
        Log("ok", "Local config saved");

        Status("✓ Install complete", "done");
        Done(true);
    }

    async Task PairToBuddy(string machineId, string kidId, AuthClient auth)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const string projectId = "chorebuddy-67a5f";
        const string apiKey = "AIzaSyCn5Vj7oFm-76PsL4vRWfmbGjqEboIhl1M";
        var url = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/firewallControl/{Uri.EscapeDataString(machineId)}?key={apiKey}&updateMask.fieldPaths=kidId";
        var payload = "{\"fields\":{\"kidId\":{\"stringValue\":\"" + kidId + "\"}}}";
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Patch, url)
        {
            Content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        if (auth.IsSignedIn)
        {
            var token = await auth.GetValidIdTokenAsync();
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Firestore pairing returned {(int)resp.StatusCode}");
    }

    Task RunProcess(string fileName, string args) => RunProcessQuiet(fileName, args, ignoreErr: false);

    async Task RunProcessQuiet(string fileName, string args, bool ignoreErr = false)
    {
        await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                var clean = stdout.Trim();
                if (!clean.Contains("FAILED 1060") && !clean.Contains("does not exist"))
                    Log("info", "  " + clean.Replace("\n", "\n  "));
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var clean = stderr.Trim();
                if (!clean.Contains("FAILED 1060") && !clean.Contains("does not exist"))
                    Log("err", "  " + clean.Replace("\n", "\n  "));
            }
            if (!ignoreErr && p.ExitCode != 0)
                throw new Exception($"{fileName} {args} exited with code {p.ExitCode}");
        });
    }

    void Post(object msg)
    {
        try
        {
            var json = JsonSerializer.Serialize(msg);
            _webview.Invoke(() =>
            {
                if (_webview.CoreWebView2 != null)
                    _webview.CoreWebView2.PostWebMessageAsString(json);
            });
        }
        catch { }
    }

    void Log(string level, string text) => Post(new { type = "log", level, text });
    void Status(string text, string level) => Post(new { type = "status", text, level });
    void Done(bool success) => Post(new { type = "done", success });
}
