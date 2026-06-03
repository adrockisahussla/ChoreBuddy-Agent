using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

/**
 * Full-screen take-over shown when the kid tries to launch a blocked
 * game. Windows IFEO redirects the .exe launch to our agent with
 * `--banned <orig path>` and we paint a hard-to-miss "no, you're
 * banned" screen. Auto-dismisses after 6 s or kid can hit Dismiss.
 */
public class BannedPopupForm : Form
{
    public BannedPopupForm(string attemptedExePath)
    {
        var fileName = string.IsNullOrEmpty(attemptedExePath)
            ? "this game"
            : Path.GetFileNameWithoutExtension(attemptedExePath);

        Text = "ChoreBuddy";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(8, 9, 14);
        ForeColor = Color.White;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // Stack everything in a centered card on the dark backdrop.
        var card = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 5,
            Anchor = AnchorStyles.None,
            AutoSize = false,
            BackColor = Color.FromArgb(15, 17, 23),
            Width = 700,
            Height = 540,
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));

        var emoji = new Label
        {
            Text = "🚫",
            Font = new Font("Segoe UI Emoji", 80F),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };

        var title = new Label
        {
            Text = "Game time suspended",
            Font = new Font("Segoe UI", 32F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 90, 90),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };

        var who = new Label
        {
            Text = $"\"{fileName}\" is blocked",
            Font = new Font("Segoe UI", 16F),
            ForeColor = Color.FromArgb(200, 205, 225),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };

        var body = new Label
        {
            Text = "Earn points in ChoreBuddy and redeem screen time\nto unlock games again.",
            Font = new Font("Segoe UI", 14F),
            ForeColor = Color.FromArgb(160, 168, 200),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
        };

        var btnRow = new Panel { Dock = DockStyle.Fill };
        var dismissBtn = new Button
        {
            Text = "Dismiss",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            BackColor = Color.FromArgb(255, 199, 0),
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(220, 56),
            Cursor = Cursors.Hand,
        };
        dismissBtn.FlatAppearance.BorderSize = 0;
        dismissBtn.Click += (s, e) => Close();
        void positionBtn() => dismissBtn.Location = new Point(
            (btnRow.Width - dismissBtn.Width) / 2,
            (btnRow.Height - dismissBtn.Height) / 2);
        btnRow.Resize += (s, e) => positionBtn();
        btnRow.Controls.Add(dismissBtn);

        card.Controls.Add(emoji, 0, 0);
        card.Controls.Add(title, 0, 1);
        card.Controls.Add(who, 0, 2);
        card.Controls.Add(body, 0, 3);
        card.Controls.Add(btnRow, 0, 4);

        // Hosting panel keeps the card centered on the maximized form.
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 9, 14) };
        host.Controls.Add(card);
        host.Resize += (s, e) =>
        {
            card.Location = new Point(
                (host.Width - card.Width) / 2,
                (host.Height - card.Height) / 2);
            positionBtn();
        };
        Load += (s, e) =>
        {
            card.Location = new Point(
                (host.Width - card.Width) / 2,
                (host.Height - card.Height) / 2);
            positionBtn();
            dismissBtn.Focus();
        };
        Controls.Add(host);

        // ESC dismisses, same as the button.
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        // Auto-dismiss after 6s so it can't be left lingering forever.
        var t = new Timer { Interval = 6000 };
        t.Tick += (s, e) => { t.Stop(); Close(); };
        t.Start();
    }

    /** Entry point for `--banned <originalExePath>` launches. */
    public static void Run(string attemptedExePath)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BannedPopupForm(attemptedExePath));
    }
}
