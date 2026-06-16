using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace ChoreBuddy.TestApp;

public class AgentService : BackgroundService
{
    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ChoreBuddy");
    static readonly string LogFile = Path.Combine(LogDir, "service.log");

    LocalConfig _config = null!;
    AuthClient? _auth;
    RemoteSync? _sync;
    ScheduleEnforcer? _enforcer;
    UsageReporter? _usage;

    record AppEntry(string Key, string Label, string DefaultPath, bool IsLauncher);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(LogDir);
        Log("=== AgentService starting ===");

        try
        {
            _config = ConfigStore.Load();
            Log($"Loaded config. Machine={_config.MachineId} Kid={_config.KidId ?? "(unpaired)"} Manager={_config.ManagerName ?? "(unsigned)"}");

            _auth = new AuthClient(_config);
            if (!_auth.IsSignedIn)
            {
                Log("WARNING: agent is not signed in. Firestore reads/writes will be rejected by rules.");
                Log("Run the setup wizard to sign in with Google.");
            }

            _sync = new RemoteSync(
                _config,
                _auth,
                BuildAppList,
                Log,
                () => Log("Config updated from cloud"),
                () => Log("Command applied"),
                kidId => Log($"Pairing changed: {kidId ?? "(unpaired)"}"));

            _sync.Start();

            _enforcer = new ScheduleEnforcer(_config, _auth, BuildAppList, Log);
            _sync.AttachEnforcer(_enforcer);
            _enforcer.Start();

            // Desktop telemetry — game sessions + screen-time used + heartbeat,
            // reported to deviceUsage/gameSessions for the manager dashboard.
            _usage = new UsageReporter(_config, _auth, BuildAppList, Log);
            _usage.Start();

            Log($"Service running (agent v{AgentUpdater.CurrentVersionString}). Commands via RTDB push.");

            // Self-update watcher — checks GitHub releases shortly after start
            // then every 6h. If a newer build ships, it stages it and launches
            // the updater, which stops this service, swaps the files, restarts.
            _ = Task.Run(() => UpdateLoop(stoppingToken));

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            throw;
        }
        finally
        {
            _usage?.Stop();
            _enforcer?.Stop();
            _sync?.Stop();
            Log("=== AgentService stopped ===");
        }
    }

    async Task UpdateLoop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var started = await AgentUpdater.CheckAndUpdateAsync(Log, ct);
                if (started) return; // the updater will stop this service
            }
            catch (Exception ex) { Log($"Update loop error: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch { return; }
        }
    }

    static List<(string key, string label, string path, bool isLauncher)> BuildAppList() =>
        KnownApps.All().Select(a => (a.Key, a.Label, a.Path, a.IsLauncher)).ToList();

    static readonly object _logLock = new();
    static void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
        }
        catch { }
    }
}
