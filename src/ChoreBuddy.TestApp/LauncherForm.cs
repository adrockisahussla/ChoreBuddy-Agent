using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

public enum LauncherChoice { None, Wizard, MainForm }

/**
 * Tiny picker shown at app start when no --service / --setup / --admin
 * flag was passed. Two big buttons: Run Setup Wizard (re-pair / sign in
 * fresh) or Open Management UI (skim status, toggle per-app settings).
 * Stays out of the way for power users by accepting the corresponding
 * CLI flags directly.
 */
public class LauncherForm : Form
{
    public LauncherChoice Choice { get; private set; } = LauncherChoice.None;

    public LauncherForm()
    {
        Text = "ChoreBuddy Agent";
        Size = new Size(520, 360);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 23);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;

        var title = new Label
        {
            Text = "ChoreBuddy Agent",
            Font = new Font("Segoe UI", 22F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 199, 0),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 60,
        };

        var subtitle = new Label
        {
            Text = "What would you like to do?",
            Font = new Font("Segoe UI", 11F),
            ForeColor = Color.FromArgb(160, 168, 200),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 30,
        };

        var wizardBtn = MakeBigButton(
            "🪄  Run Setup Wizard",
            "Sign in with Google, pair this PC to a buddy, install the service.",
            Color.FromArgb(255, 199, 0), Color.Black);
        wizardBtn.Click += (s, e) => { Choice = LauncherChoice.Wizard; Close(); };

        var existingBtn = MakeBigButton(
            "🛠  Open Management UI",
            "Tweak per-app settings on this already-paired PC.",
            Color.FromArgb(40, 44, 56), Color.White);
        existingBtn.Click += (s, e) => { Choice = LauncherChoice.MainForm; Close(); };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(28, 12, 28, 28),
        };
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        stack.Controls.Add(wizardBtn, 0, 0);
        stack.Controls.Add(existingBtn, 0, 1);

        Controls.Add(stack);
        Controls.Add(subtitle);
        Controls.Add(title);
    }

    static Button MakeBigButton(string headline, string sub, Color bg, Color fg)
    {
        var b = new Button
        {
            Text = headline + "\n\n" + sub,
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = fg,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 8, 0, 8),
            Dock = DockStyle.Fill,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }
}
