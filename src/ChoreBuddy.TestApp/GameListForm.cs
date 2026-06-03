using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

public class GameListForm : Form
{
    readonly List<InstalledGame> _games;
    readonly string _sourceLabel;

    public GameListForm(string sourceLabel, List<InstalledGame> games)
    {
        _games = games;
        _sourceLabel = sourceLabel;

        Text = $"{sourceLabel} Games";
        Size = new Size(640, 600);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(15, 17, 23);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        var title = new Label
        {
            Text = $"Found {_games.Count} installed {sourceLabel} game{(_games.Count == 1 ? "" : "s")}",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 200, 66),
            Dock = DockStyle.Top,
            Height = 50,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var hint = new Label
        {
            Text = "Toggle BLOCK to kill+block all .exes for that game",
            ForeColor = Color.FromArgb(123, 132, 168),
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9)
        };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(20, 10, 20, 20)
        };

        foreach (var game in _games)
            panel.Controls.Add(CreateGameRow(game));

        Controls.Add(panel);
        Controls.Add(hint);
        Controls.Add(title);
    }

    Control CreateGameRow(InstalledGame game)
    {
        var row = new Panel
        {
            Width = 560,
            Height = 70,
            BackColor = Color.FromArgb(26, 29, 39),
            Margin = new Padding(0, 4, 0, 4)
        };

        var label = new Label
        {
            Text = game.Name,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(15, 10),
            Size = new Size(360, 22),
            AutoEllipsis = true
        };

        var meta = new Label
        {
            Text = $"AppID {game.AppId}  •  {game.Executables.Count} exe(s)",
            Font = new Font("Segoe UI", 8),
            ForeColor = Color.FromArgb(100, 200, 100),
            Location = new Point(15, 34),
            Size = new Size(360, 18)
        };

        var pathHint = new Label
        {
            Text = game.InstallPath,
            Font = new Font("Segoe UI", 7),
            ForeColor = Color.FromArgb(80, 85, 110),
            Location = new Point(15, 50),
            Size = new Size(360, 14),
            AutoEllipsis = true
        };

        var toggle = new CheckBox
        {
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(390, 20),
            Size = new Size(100, 30),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Enabled = game.Executables.Count > 0
        };
        toggle.FlatAppearance.BorderSize = 0;
        toggle.Checked = AnyExeBlocked(game);
        StyleToggle(toggle);

        toggle.CheckedChanged += (s, e) =>
        {
            try
            {
                foreach (var exe in game.Executables)
                {
                    var key = MakeKey(game, exe);
                    if (toggle.Checked) FirewallManager.Block(key, exe);
                    else FirewallManager.Unblock(key, exe);
                }
                StyleToggle(toggle);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed:\n\n{ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var details = new Button
        {
            Text = "Exes",
            FlatStyle = FlatStyle.Flat,
            Location = new Point(495, 20),
            Size = new Size(50, 30),
            Font = new Font("Segoe UI", 8),
            BackColor = Color.FromArgb(46, 51, 80),
            ForeColor = Color.White
        };
        details.FlatAppearance.BorderSize = 0;
        details.Click += (s, e) =>
        {
            var list = string.Join("\n", game.Executables.Select(p =>
                p.Replace(game.InstallPath, "...")));
            MessageBox.Show(list.Length > 0 ? list : "(no executables found)",
                game.Name, MessageBoxButtons.OK);
        };

        row.Controls.Add(label);
        row.Controls.Add(meta);
        row.Controls.Add(pathHint);
        row.Controls.Add(toggle);
        row.Controls.Add(details);
        return row;
    }

    static string MakeKey(InstalledGame game, string exePath)
        => $"{game.Source}_{game.AppId}_{Path.GetFileNameWithoutExtension(exePath)}";

    static bool AnyExeBlocked(InstalledGame game)
    {
        foreach (var exe in game.Executables)
            if (FirewallManager.IsBlocked(MakeKey(game, exe), exe)) return true;
        return false;
    }

    static void StyleToggle(CheckBox cb)
    {
        if (!cb.Enabled)
        {
            cb.BackColor = Color.FromArgb(50, 55, 70);
            cb.ForeColor = Color.Gray;
            cb.Text = "—";
        }
        else if (cb.Checked)
        {
            cb.BackColor = Color.FromArgb(220, 60, 60);
            cb.ForeColor = Color.White;
            cb.Text = "BLOCKED";
        }
        else
        {
            cb.BackColor = Color.FromArgb(34, 197, 94);
            cb.ForeColor = Color.White;
            cb.Text = "Allowed";
        }
    }
}
