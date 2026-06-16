using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

/** Tracks which one-shot notice/message ids the overlay has already shown.
 *  Stored in LocalApplicationData (per-user, writable by the overlay) so we
 *  never write the ProgramData state files (owned by the system/updater). */
public class OverlaySeenData { public long Notice { get; set; } public long Message { get; set; } }

static class OverlaySeen
{
    static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChoreBuddy", "overlay-seen.json");

    public static OverlaySeenData Load()
    {
        try { if (System.IO.File.Exists(Path_)) return System.Text.Json.JsonSerializer.Deserialize<OverlaySeenData>(System.IO.File.ReadAllText(Path_)) ?? new OverlaySeenData(); }
        catch { }
        return new OverlaySeenData();
    }

    public static void Save(OverlaySeenData d)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            System.IO.File.WriteAllText(Path_, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { }
    }
}

public static class OverlayApp
{
    public static void Run()
    {
        // Single instance — the service relaunches the overlay on startup,
        // so a duplicate (e.g. one already started via HKCU\Run) just exits.
        using var instance = new System.Threading.Mutex(true, "Global\\ChoreBuddyOverlay", out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();

        // Which notice/message ids we've already shown — persisted in a
        // USER-writable spot so we never touch the system-owned ProgramData files.
        var seen = OverlaySeen.Load();

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
                    if (warn.WarnSeconds > 0) new WarningToast(warn.WarnSeconds, warn.KidName, warn.AvailableMinutes).Show();
                }

                // "Agent updated" notice — show once per id. We DON'T write the
                // notice file here (it lives in ProgramData, written by the
                // system/updater — a user-session overlay can't overwrite it).
                // Instead we remember the last shown id in a user-writable file.
                var notice = NoticeState.Read();
                if (notice.NoticeId != 0 && notice.NoticeId != seen.Notice)
                {
                    seen.Notice = notice.NoticeId; OverlaySeen.Save(seen);
                    new UpdatedToast(notice.Message).Show();
                }

                // Manager broadcast message — show once per id (same approach).
                var msg = MessageState.Read();
                if (msg.MessageId != 0 && msg.MessageId != seen.Message)
                {
                    seen.Message = msg.MessageId; OverlaySeen.Save(seen);
                    if (!string.IsNullOrWhiteSpace(msg.Text)) new MessageToast(msg.Text).Show();
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

/** Brief banner shown after the agent self-updates. Same shape as
 *  UnlockToast but brand-purple and a touch longer on screen. */
public class UpdatedToast : Form
{
    public UpdatedToast(string message)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(124, 58, 237); // brand purple
        DoubleBuffered = true;
        Opacity = 0;

        var label = new Label
        {
            Text = "✓  " + (string.IsNullOrEmpty(message) ? "ChoreBuddy Agent updated" : message),
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };
        Controls.Add(label);

        Width = 620;
        Height = 76;
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

        var dismiss = new System.Windows.Forms.Timer { Interval = 4500 };
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

/** A message from the manager, shown centered until clicked (or 12s).
 *  Blue banner, wraps for longer text. */
public class MessageToast : Form
{
    public MessageToast(string text)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(37, 99, 235); // blue
        DoubleBuffered = true;
        Opacity = 0;

        var title = new Label
        {
            Text = "💬  Message from your manager",
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(220, 230, 255),
            Dock = DockStyle.Top,
            Height = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };
        var body = new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = Color.White,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Padding = new Padding(18, 0, 18, 10),
        };
        Controls.Add(body);
        Controls.Add(title);

        Width = 680;
        Height = 130;
        var screen = Screen.PrimaryScreen!.Bounds;
        Left = screen.Left + (screen.Width - Width) / 2;
        Top = screen.Top + 90;

        var fadeIn = new System.Windows.Forms.Timer { Interval = 25 };
        fadeIn.Tick += (s, e) => { if (Opacity < 0.96) Opacity += 0.08; else { Opacity = 0.96; fadeIn.Stop(); } };
        fadeIn.Start();

        var dismiss = new System.Windows.Forms.Timer { Interval = 12000 };
        dismiss.Tick += (s, e) =>
        {
            dismiss.Stop();
            var fadeOut = new System.Windows.Forms.Timer { Interval = 25 };
            fadeOut.Tick += (s2, e2) => { if (Opacity > 0.05) Opacity -= 0.1; else { fadeOut.Stop(); Close(); } };
            fadeOut.Start();
        };
        dismiss.Start();

        Click += (s, e) => Close();
        title.Click += (s, e) => Close();
        body.Click += (s, e) => Close();
    }
}

/** Interactive corner toast: warns the kid game time is about to end and,
 *  if they have banked minutes, lets them choose how many to spend to keep
 *  playing. "Use extra time" writes an ExtendRequest the service consumes;
 *  "OK" just dismisses and lets the machine lock at the cutoff. */
public class WarningToast : Form
{
    int _choice;
    readonly int _available;
    Label _choiceLabel = null!;
    Button _useBtn = null!;

    public WarningToast(int remainingSeconds, string kidName, int availableMinutes)
    {
        int minutes = Math.Max(1, (int)Math.Ceiling(remainingSeconds / 60.0));
        _available = Math.Max(0, availableMinutes);
        _choice = Math.Min(15, _available);
        if (_choice == 0 && _available > 0) _choice = _available;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(245, 158, 11); // amber
        DoubleBuffered = true;
        Opacity = 0;

        var who = string.IsNullOrEmpty(kidName) ? "" : $"{kidName}, ";
        var title = new Label
        {
            Text = $"⏰  {who}game time ends in {minutes} minute{(minutes == 1 ? "" : "s")}",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(16, 14, 428, 30),
        };
        Controls.Add(title);

        Width = 460;
        var screen = Screen.PrimaryScreen!.WorkingArea; // excludes taskbar

        if (_available <= 0)
        {
            // No banked time — just an acknowledge button.
            Height = 96;
            var ok = MakeButton("OK", 180, 52, Color.White, Color.FromArgb(180, 120, 0));
            ok.Click += (s, e) => Close();
            Controls.Add(ok);
        }
        else
        {
            Height = 196;
            var bank = new Label
            {
                Text = $"You have {_available} min banked",
                Font = new Font("Segoe UI", 11, FontStyle.Regular),
                ForeColor = Color.White, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(16, 46, 428, 22),
            };
            Controls.Add(bank);

            // Stepper: −  [N min]  +
            var minus = MakeButton("−", 56, 44, Color.White, Color.FromArgb(180, 120, 0));
            minus.Location = new Point(70, 76);
            minus.Click += (s, e) => Adjust(-5);
            Controls.Add(minus);

            _choiceLabel = new Label
            {
                Font = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = Color.White, BackColor = Color.Transparent,
                AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(132, 76, 196, 44),
            };
            Controls.Add(_choiceLabel);

            var plus = MakeButton("+", 56, 44, Color.White, Color.FromArgb(180, 120, 0));
            plus.Location = new Point(334, 76);
            plus.Click += (s, e) => Adjust(5);
            Controls.Add(plus);

            _useBtn = MakeButton("", 250, 50, Color.FromArgb(34, 197, 94), Color.White);
            _useBtn.Location = new Point(40, 130);
            _useBtn.Click += (s, e) => UseExtra();
            Controls.Add(_useBtn);

            var ok = MakeButton("OK", 120, 50, Color.White, Color.FromArgb(180, 120, 0));
            ok.Location = new Point(300, 130);
            ok.Click += (s, e) => Close();
            Controls.Add(ok);

            RefreshChoice();
        }

        Left = screen.Right - Width - 20;
        Top = screen.Bottom - Height - 20;

        var fadeIn = new System.Windows.Forms.Timer { Interval = 25 };
        fadeIn.Tick += (s, e) => { if (Opacity < 0.97) Opacity += 0.12; else { Opacity = 0.97; fadeIn.Stop(); } };
        fadeIn.Start();

        // Interactive toasts linger longer so the kid can decide.
        var dismiss = new System.Windows.Forms.Timer { Interval = _available > 0 ? 30000 : 8000 };
        dismiss.Tick += (s, e) => { dismiss.Stop(); Close(); };
        dismiss.Start();
    }

    static Button MakeButton(string text, int w, int h, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text, Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore,
            Font = new Font("Segoe UI", 12, FontStyle.Bold), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void Adjust(int delta)
    {
        _choice = Math.Max(5, Math.Min(_available, _choice + delta));
        RefreshChoice();
    }

    void RefreshChoice()
    {
        _choiceLabel.Text = $"{_choice} min";
        _useBtn.Text = $"▶ Use {_choice} min";
    }

    void UseExtra()
    {
        ExtendRequest.Write(new ExtendRequestData
        {
            Minutes = _choice,
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        // Acknowledge and close; the service applies it within ~2s.
        foreach (Control c in Controls) c.Visible = false;
        var done = new Label
        {
            Text = $"✓ Added {_choice} min — keep playing!",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.White, BackColor = Color.Transparent,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(done);
        var close = new System.Windows.Forms.Timer { Interval = 1600 };
        close.Tick += (s, e) => { close.Stop(); Close(); };
        close.Start();
    }
}
