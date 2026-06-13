using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

public static class OverlayApp
{
    public static void Run()
    {
        ApplicationConfiguration.Initialize();

        OverlayForm? form = null;
        bool prevBlocked = false;
        long lastTimestamp = 0;
        // Seed from the current file so a stale warning doesn't fire on launch.
        long lastWarnId = WarnState.Read().WarnId;

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (s, e) =>
        {
            try
            {
                var state = OverlayState.Read();

                // 5-minutes-left warning — fire the corner toast once per episode.
                var warn = WarnState.Read();
                if (warn.WarnId != 0 && warn.WarnId != lastWarnId)
                {
                    lastWarnId = warn.WarnId;
                    if (warn.WarnSeconds > 0) new WarningToast(warn.WarnSeconds, warn.KidName).Show();
                }

                // Detect transition: blocked -> unblocked → show "restored" toast
                if (prevBlocked && !state.Blocked)
                {
                    new UnlockToast(state.KidName).Show();
                }
                prevBlocked = state.Blocked;

                if (state.Blocked && !state.Acknowledged)
                {
                    if (form == null || form.IsDisposed)
                    {
                        form = new OverlayForm(state, () =>
                        {
                            state.Acknowledged = true;
                            OverlayState.Write(state);
                        });
                        form.Show();
                    }
                    else
                    {
                        if (state.Timestamp != lastTimestamp) form.ApplyState(state);
                        if (!form.Visible) form.Show();
                    }
                }
                else
                {
                    if (form != null && form.Visible) form.Hide();
                }

                lastTimestamp = state.Timestamp;
            }
            catch { }
        };
        timer.Start();

        Application.Run();
    }
}

public class UnlockToast : Form
{
    public UnlockToast(string kidName)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(34, 197, 94);
        DoubleBuffered = true;
        Opacity = 0;

        var label = new Label
        {
            Text = string.IsNullOrEmpty(kidName)
                ? "✓  Game time restored — have fun!"
                : $"✓  Game time restored — have fun, {kidName}!",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };
        Controls.Add(label);

        Width = 640;
        Height = 80;
        var screen = Screen.PrimaryScreen!.Bounds;
        Left = screen.Left + (screen.Width - Width) / 2;
        Top = screen.Top + 100;

        var fadeIn = new System.Windows.Forms.Timer { Interval = 25 };
        fadeIn.Tick += (s, e) =>
        {
            if (Opacity < 0.95) Opacity += 0.1;
            else { Opacity = 0.95; fadeIn.Stop(); }
        };
        fadeIn.Start();

        var dismiss = new System.Windows.Forms.Timer { Interval = 3000 };
        dismiss.Tick += (s, e) =>
        {
            dismiss.Stop();
            var fadeOut = new System.Windows.Forms.Timer { Interval = 25 };
            fadeOut.Tick += (s2, e2) =>
            {
                if (Opacity > 0.05) Opacity -= 0.1;
                else { fadeOut.Stop(); Close(); }
            };
            fadeOut.Start();
        };
        dismiss.Start();

        Click += (s, e) => Close();
        label.Click += (s, e) => Close();
    }
}

/** Corner toast warning the kid their game time is about to be cut off.
 *  Amber, bottom-right, auto-dismisses after ~6 s. Modeled on UnlockToast. */
public class WarningToast : Form
{
    public WarningToast(int remainingSeconds, string kidName)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(remainingSeconds / 60.0));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(245, 158, 11); // amber
        DoubleBuffered = true;
        Opacity = 0;

        var who = string.IsNullOrEmpty(kidName) ? "" : $"{kidName}, ";
        var label = new Label
        {
            Text = $"⏰  {who}game time ends in {minutes} minute{(minutes == 1 ? "" : "s")}",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };
        Controls.Add(label);

        Width = 460;
        Height = 76;
        var screen = Screen.PrimaryScreen!.WorkingArea; // excludes taskbar
        Left = screen.Right - Width - 20;
        Top = screen.Bottom - Height - 20;

        var fadeIn = new System.Windows.Forms.Timer { Interval = 25 };
        fadeIn.Tick += (s, e) =>
        {
            if (Opacity < 0.96) Opacity += 0.1;
            else { Opacity = 0.96; fadeIn.Stop(); }
        };
        fadeIn.Start();

        var dismiss = new System.Windows.Forms.Timer { Interval = 6000 };
        dismiss.Tick += (s, e) =>
        {
            dismiss.Stop();
            var fadeOut = new System.Windows.Forms.Timer { Interval = 25 };
            fadeOut.Tick += (s2, e2) =>
            {
                if (Opacity > 0.05) Opacity -= 0.1;
                else { fadeOut.Stop(); Close(); }
            };
            fadeOut.Start();
        };
        dismiss.Start();

        Click += (s, e) => Close();
        label.Click += (s, e) => Close();
    }
}
