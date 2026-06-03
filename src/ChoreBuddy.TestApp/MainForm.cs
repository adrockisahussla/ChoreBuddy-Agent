using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

public class MainForm : Form
{
    record AppEntry(string Key, string Label, string DefaultPath, bool IsLauncher);

    static List<AppEntry> BuildKnownApps() =>
        KnownApps.All().Select(a => new AppEntry(a.Key, a.Label, a.Path, a.IsLauncher)).ToList();

    static readonly Color BgDark    = Color.FromArgb(15, 17, 23);
    static readonly Color BgCard    = Color.FromArgb(26, 29, 39);
    static readonly Color BgCardHover = Color.FromArgb(36, 40, 54);
    static readonly Color TextPri   = Color.FromArgb(240, 242, 255);
    static readonly Color TextSec   = Color.FromArgb(123, 132, 168);
    static readonly Color TextMute  = Color.FromArgb(80, 85, 110);
    static readonly Color Accent    = Color.FromArgb(245, 200, 66);
    static readonly Color Good      = Color.FromArgb(34, 197, 94);
    static readonly Color Bad       = Color.FromArgb(239, 68, 68);
    static readonly Color Border    = Color.FromArgb(46, 51, 80);
    static readonly Color CloudBlue = Color.FromArgb(102, 192, 244);

    readonly bool _isAdmin;
    readonly ToolTip _tooltip = new();
    readonly LocalConfig _config;
    readonly List<AppEntry> _apps;
    readonly Dictionary<string, Action> _refreshers = new();
    RemoteSync? _sync;
    Label? _syncLog;

    public MainForm()
    {
        _isAdmin = FirewallManager.IsRunningAsAdmin();
        _config = ConfigStore.Load();
        _apps = BuildKnownApps();

        Text = "ChoreBuddy — Firewall Test";
        Size = new Size(720, 820);
        MinimumSize = new Size(640, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextPri;
        Font = new Font("Segoe UI", 10);

        var header = BuildHeader();
        var content = BuildContent();
        var footer = BuildFooter();

        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(header);

        Shown += (s, e) => { if (_isAdmin) StartSync(); };
        FormClosing += (s, e) => { _sync?.Stop(); ConfigStore.Save(_config); };
    }

    void StartSync()
    {
        var auth = new AuthClient(_config);
        _sync = new RemoteSync(
            _config,
            auth,
            () => _apps.Select(a => (a.Key, a.Label, a.DefaultPath, a.IsLauncher)).ToList(),
            msg => SafeInvoke(() => AppendLog(msg)),
            () => SafeInvoke(RefreshCheckboxesFromConfig),
            () => SafeInvoke(RefreshAllRows),
            kidId => SafeInvoke(() => UpdatePairingBanner(kidId)));
        _sync.Start();
    }

    Label? _pairingLabel;

    void UpdatePairingBanner(string? kidId)
    {
        if (_pairingLabel == null) return;
        _pairingLabel.Text = string.IsNullOrEmpty(kidId)
            ? $"⚠ Unpaired  •  Machine: {_config.MachineId}  •  Pair from manager app"
            : $"✓ Paired to {kidId}  •  Machine: {_config.MachineId}  •  Listening for commands";
        _pairingLabel.ForeColor = string.IsNullOrEmpty(kidId) ? Accent : Good;
    }

    readonly Dictionary<string, Action> _checkboxRefreshers = new();

    void RefreshCheckboxesFromConfig()
    {
        foreach (var r in _checkboxRefreshers.Values) r();
    }

    void SafeInvoke(Action action)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(action); } catch { }
    }

    void RefreshAllRows()
    {
        foreach (var r in _refreshers.Values) r();
    }

    void AppendLog(string msg)
    {
        if (_syncLog == null) return;
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var line = $"[{stamp}] {msg}";
        _syncLog.Text = line + "\n" + _syncLog.Text;
        if (_syncLog.Text.Length > 1200) _syncLog.Text = _syncLog.Text[..1200];
    }

    Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = BgDark };

        var title = new Label
        {
            Text = "App Internet Blocker",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Accent,
            Dock = DockStyle.Top,
            Height = 50,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var bannerBg = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = _isAdmin ? Color.FromArgb(13, 61, 42) : Color.FromArgb(61, 13, 13)
        };
        var banner = new Label
        {
            Text = _isAdmin
                ? (string.IsNullOrEmpty(_config.KidId)
                    ? $"⚠ Unpaired  •  Machine: {_config.MachineId}  •  Pair from manager app"
                    : $"✓ Paired to {_config.KidId}  •  Machine: {_config.MachineId}  •  Listening")
                : "✗ NOT admin — toggles will fail",
            ForeColor = _isAdmin
                ? (string.IsNullOrEmpty(_config.KidId) ? Accent : Good)
                : Bad,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
        bannerBg.Controls.Add(banner);
        _pairingLabel = banner;

        header.Controls.Add(bannerBg);
        header.Controls.Add(title);
        return header;
    }

    FlowLayoutPanel BuildContent()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(24, 16, 24, 16),
            BackColor = BgDark
        };

        var sectionLabel = new Label
        {
            Text = "APPS",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = TextSec,
            AutoSize = false,
            Width = 640,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(4, 0, 0, 8)
        };
        panel.Controls.Add(sectionLabel);

        foreach (var app in _apps)
            panel.Controls.Add(CreateAppRow(app));

        return panel;
    }

    Panel BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 200,
            BackColor = Color.FromArgb(20, 22, 30),
            Padding = new Padding(24, 12, 24, 16)
        };

        var label = new Label
        {
            Text = "GAME LIBRARIES",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = TextSec,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        buttonRow.Controls.Add(MakeFooterButton("🎮 Steam games", () => ScanGames("Steam", SteamScanner.ScanInstalledGames)), 0, 0);
        buttonRow.Controls.Add(MakeFooterButton("🎮 Epic games", () => ScanGames("Epic", EpicScanner.ScanInstalledGames)), 1, 0);
        buttonRow.Controls.Add(MakeFooterButton("+ Custom app", AddCustomApp), 2, 0);

        var logLabel = new Label
        {
            Text = "REMOTE SYNC LOG",
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            ForeColor = TextSec,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 0, 0)
        };

        _syncLog = new Label
        {
            Text = "Waiting for commands...",
            Font = new Font("Consolas", 8),
            ForeColor = TextSec,
            BackColor = BgCard,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(8, 6, 8, 6),
            AutoEllipsis = false
        };

        footer.Controls.Add(_syncLog);
        footer.Controls.Add(logLabel);
        footer.Controls.Add(buttonRow);
        footer.Controls.Add(label);
        return footer;
    }

    Button MakeFooterButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = BgCard,
            ForeColor = TextPri,
            Dock = DockStyle.Fill,
            Margin = new Padding(4, 4, 4, 4),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = Border;
        btn.FlatAppearance.MouseOverBackColor = BgCardHover;
        btn.Click += (s, e) => onClick();
        return btn;
    }

    void ScanGames(string source, Func<List<InstalledGame>> scan)
    {
        UseWaitCursor = true;
        try
        {
            var games = scan();
            if (games.Count == 0)
            {
                MessageBox.Show($"No {source} games found.\n({source} not installed, or no games installed)",
                    "Scan complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new GameListForm(source, games);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Scan failed:\n\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    void AddCustomApp()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe",
            Title = "Pick an .exe to block"
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        var entry = new AppEntry(name, name, dlg.FileName, false);
        _apps.Add(entry);

        var content = (FlowLayoutPanel)Controls.OfType<FlowLayoutPanel>().First();
        content.Controls.Add(CreateAppRow(entry));
    }

    Control CreateAppRow(AppEntry app)
    {
        var pathExists = !string.IsNullOrEmpty(app.DefaultPath) && File.Exists(app.DefaultPath);
        var settings = ConfigStore.GetOrInit(_config, app.Key);

        var row = new Panel
        {
            Width = 640,
            Height = 92,
            BackColor = BgCard,
            Margin = new Padding(0, 0, 0, 8)
        };

        var iconCircle = new Panel
        {
            Width = 40,
            Height = 40,
            Location = new Point(16, 14),
            BackColor = BgCard
        };
        iconCircle.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(pathExists ? Accent : Color.FromArgb(50, 55, 70));
            g.FillEllipse(brush, 0, 0, 40, 40);
            using var font = new Font("Segoe UI", 14, FontStyle.Bold);
            using var textBrush = new SolidBrush(pathExists ? Color.FromArgb(15, 17, 23) : TextMute);
            var letter = app.Label.Length > 0 ? app.Label[0].ToString() : "?";
            var size = g.MeasureString(letter, font);
            g.DrawString(letter, font, textBrush,
                (40 - size.Width) / 2, (40 - size.Height) / 2);
        };

        var label = new Label
        {
            Text = app.Label,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = pathExists ? TextPri : TextMute,
            Location = new Point(68, 14),
            AutoSize = true
        };

        var statusDot = new Panel
        {
            Width = 6,
            Height = 6,
            Location = new Point(68, 40),
            BackColor = BgCard
        };
        var status = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8),
            ForeColor = TextSec,
            Location = new Point(80, 36),
            AutoSize = true
        };
        Color currentStatusColor = TextMute;
        statusDot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(currentStatusColor);
            e.Graphics.FillEllipse(brush, 0, 0, 6, 6);
        };

        var remoteCheck = new CheckBox
        {
            Text = "Remote shutoff",
            ForeColor = CloudBlue,
            BackColor = BgCard,
            Font = new Font("Segoe UI", 8, FontStyle.Bold),
            Location = new Point(68, 60),
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Checked = settings.RemoteEnabled,
            Enabled = pathExists
        };

        CheckBox? killRelatedCheck = null;
        if (app.IsLauncher)
        {
            killRelatedCheck = new CheckBox
            {
                Text = "Kill related games",
                ForeColor = Accent,
                BackColor = BgCard,
                Font = new Font("Segoe UI", 8, FontStyle.Bold),
                Location = new Point(210, 60),
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Checked = settings.KillRelated,
                Enabled = pathExists
            };
        }

        bool suppressPush = false;
        void PushSettings()
        {
            if (suppressPush) return;
            settings.RemoteEnabled = remoteCheck.Checked;
            if (killRelatedCheck != null) settings.KillRelated = killRelatedCheck.Checked;
            ConfigStore.Save(_config);
            _ = _sync?.PushAppConfigAsync(app.Key, new AppConfigEntry(settings.RemoteEnabled, settings.KillRelated));
        }
        remoteCheck.CheckedChanged += (s, e) => PushSettings();
        if (killRelatedCheck != null) killRelatedCheck.CheckedChanged += (s, e) => PushSettings();

        _checkboxRefreshers[app.Key] = () =>
        {
            suppressPush = true;
            try
            {
                if (remoteCheck.Checked != settings.RemoteEnabled) remoteCheck.Checked = settings.RemoteEnabled;
                if (killRelatedCheck != null && killRelatedCheck.Checked != settings.KillRelated)
                    killRelatedCheck.Checked = settings.KillRelated;
            }
            finally { suppressPush = false; }
        };

        var toggle = new CheckBox
        {
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(110, 34),
            Location = new Point(510, 28),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Enabled = pathExists && _isAdmin,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = pathExists && _isAdmin ? Cursors.Hand : Cursors.Default
        };
        toggle.FlatAppearance.BorderSize = 0;

        void Refresh()
        {
            var running = pathExists && IsProcessRunning(app.DefaultPath);
            var blocked = pathExists && FirewallManager.IsBlocked(app.Key, app.DefaultPath);

            if (!pathExists) { status.Text = "Not installed"; currentStatusColor = TextMute; }
            else if (blocked) { status.Text = "Blocked"; currentStatusColor = Bad; }
            else if (running) { status.Text = "Running"; currentStatusColor = Accent; }
            else { status.Text = "Installed"; currentStatusColor = Good; }
            status.ForeColor = currentStatusColor;
            statusDot.Invalidate();

            toggle.CheckedChanged -= ToggleHandler;
            toggle.Checked = blocked;
            toggle.CheckedChanged += ToggleHandler;
            StyleToggle(toggle);
        }

        void ToggleHandler(object? s, EventArgs e)
        {
            try
            {
                if (toggle.Checked) FirewallManager.Block(app.Key, app.DefaultPath);
                else FirewallManager.Unblock(app.Key, app.DefaultPath);
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed:\n\n{ex.Message}", "Firewall error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                toggle.CheckedChanged -= ToggleHandler;
                toggle.Checked = !toggle.Checked;
                toggle.CheckedChanged += ToggleHandler;
                StyleToggle(toggle);
            }
        }
        toggle.CheckedChanged += ToggleHandler;

        Refresh();
        _refreshers[app.Key] = Refresh;

        if (pathExists)
            _tooltip.SetToolTip(label, app.DefaultPath);

        row.Controls.Add(iconCircle);
        row.Controls.Add(label);
        row.Controls.Add(statusDot);
        row.Controls.Add(status);
        row.Controls.Add(remoteCheck);
        if (killRelatedCheck != null) row.Controls.Add(killRelatedCheck);
        row.Controls.Add(toggle);
        return row;
    }

    static bool IsProcessRunning(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            return Process.GetProcessesByName(name).Length > 0;
        }
        catch { return false; }
    }

    static void StyleToggle(CheckBox cb)
    {
        if (!cb.Enabled)
        {
            cb.BackColor = Color.FromArgb(40, 44, 58);
            cb.ForeColor = TextMute;
            cb.Text = "—";
        }
        else if (cb.Checked)
        {
            cb.BackColor = Bad;
            cb.ForeColor = Color.White;
            cb.Text = "BLOCKED";
        }
        else
        {
            cb.BackColor = Good;
            cb.ForeColor = Color.White;
            cb.Text = "Allowed";
        }
    }
}
