using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

/**
 * GameTimeForm — the kid-facing "Game Time" dashboard.
 *
 * Light, branded window (matches the product mockup): a gradient header, a
 * left card showing time-remaining-today + allowed/locked status, and a right
 * card showing the kid's BANKED minutes with a free-entry field to spend some
 * of them for instant game time NOW.
 *
 * It runs in the kid's session and has no Firebase auth, so:
 *   • it READS live status from GameTimeStatus (written by the service), and
 *   • it SPENDS by writing an ExtendRequest (consumed by the service, which
 *     debits users.minutesRemaining and extends the session).
 *
 * Minutes are *banked in the app* (chore points → screen-time redemption);
 * the desktop only spends them.
 */
public class GameTimeForm : Form
{
    // ---- palette (light) ----
    static readonly Color Bg        = Color.White;
    static readonly Color CardBg     = Color.White;
    static readonly Color CardBorder = Color.FromArgb(235, 236, 244);
    static readonly Color Ink        = Color.FromArgb(17, 24, 39);
    static readonly Color InkSoft    = Color.FromArgb(107, 114, 128);
    static readonly Color InkFaint   = Color.FromArgb(156, 163, 175);
    static readonly Color Purple     = Color.FromArgb(124, 58, 237);
    static readonly Color Pink       = Color.FromArgb(236, 41, 123);
    static readonly Color Red        = Color.FromArgb(255, 59, 92);
    static readonly Color Good       = Color.FromArgb(34, 197, 94);
    static readonly Color GoodBg     = Color.FromArgb(236, 253, 243);
    static readonly Color GoodBorder = Color.FromArgb(187, 247, 208);
    static readonly Color LockBg     = Color.FromArgb(254, 242, 242);
    static readonly Color LockBorder = Color.FromArgb(254, 202, 202);
    static readonly Color TrackBg    = Color.FromArgb(233, 234, 242);

    // ---- live state ----
    GameTimeStateData _state = new();
    int _displaySeconds = -1;   // interpolated countdown
    int _maxSeconds = 1;        // most we've seen this session — scales the bar
    int _banked;
    long _lastStamp;

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    // ---- controls ----
    Label _kidPill = null!;
    Label _bigTime = null!;
    Label _timeSub = null!;
    Bar _bar = null!;
    Label _statusPill = null!;
    Label _bankedNum = null!;
    NumericUpDown _spendInput = null!;
    AccentButton _spendBtn = null!;
    Label _spendNote = null!;

    public GameTimeForm()
    {
        Text = "Chore Buddy — Game Time";
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Font = new Font("Segoe UI", 10F);
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());

        Load += (s, e) => Refresh_();
        _timer.Tick += (s, e) => OnTick();
        _timer.Start();
        FormClosed += (s, e) => _timer.Stop();
    }

    // ---------------- header ----------------
    Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 96 };
        header.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var br = new LinearGradientBrush(header.ClientRectangle, Purple, Red, 0f);
            var blend = new ColorBlend
            {
                Colors = new[] { Purple, Pink, Red },
                Positions = new[] { 0f, 0.55f, 1f },
            };
            br.InterpolationColors = blend;
            g.FillRectangle(br, header.ClientRectangle);

            using var titleFont = new Font("Segoe UI", 22F, FontStyle.Bold);
            g.DrawString("🎮  Game Time", titleFont, Brushes.White, 28, 26);
        };

        _kidPill = new Label
        {
            AutoSize = false,
            Size = new Size(190, 44),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(255, 255, 255),
            Text = "🐼  …",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        // translucent white pill, painted via a rounded region + custom paint
        _kidPill.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            using var path = Rounded(new Rectangle(0, 0, _kidPill.Width - 1, _kidPill.Height - 1), 22);
            g.FillPath(bg, path);
            TextRenderer.DrawText(g, _kidPill.Text, _kidPill.Font,
                new Rectangle(0, 0, _kidPill.Width, _kidPill.Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        _kidPill.BackColor = Color.Transparent;
        _kidPill.Location = new Point(header.Width - 190 - 28, 26);
        header.Controls.Add(_kidPill);
        header.Resize += (s, e) => _kidPill.Location = new Point(header.Width - _kidPill.Width - 28, 26);
        return header;
    }

    // ---------------- body ----------------
    Control BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 24) };

        // Two cards laid out with a table so they share width and resize.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        grid.Controls.Add(BuildLeftCard(), 0, 0);
        grid.Controls.Add(BuildRightCard(), 1, 0);
        body.Controls.Add(grid);
        return body;
    }

    Card BuildLeftCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0), Padding = new Padding(28) };

        _bigTime = new Label
        {
            Text = "—",
            Font = new Font("Segoe UI", 42F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _timeSub = new Label
        {
            Text = "checking…",
            Font = new Font("Segoe UI", 10.5F),
            ForeColor = InkSoft,
            AutoSize = true,
        };
        _bar = new Bar { Height = 12 };
        _statusPill = new Label
        {
            Text = "● …",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            AutoSize = false,
            Height = 46,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusPill.Paint += (s, e) => PaintPill(e.Graphics, _statusPill);
        var foot = new Label
        {
            Text = "The desktop app locks the games until tomorrow — or until you spend more banked minutes.",
            Font = new Font("Segoe UI", 10.5F),
            ForeColor = Ink,
            AutoSize = false,
            Height = 64,
            UseMnemonic = false,
        };

        // Lay the rows out in a TableLayoutPanel so the per-row Margin (the
        // vertical rhythm) is actually honored — a plain Dock=Top stack ignores
        // Margin entirely, which is what made the spacing look ragged.
        var tlp = NewCardLayout();
        AddRow(tlp, MakeCaption("TIME REMAINING TODAY"), 0, false);
        AddRow(tlp, _bigTime, 2, false);
        AddRow(tlp, _timeSub, 0, false);
        AddRow(tlp, _bar, 18, true);
        AddRow(tlp, _statusPill, 18, true);
        AddRow(tlp, MakeCaption("WHEN TIME HITS 0:00"), 22, false);
        AddRow(tlp, foot, 6, true);
        card.Controls.Add(tlp);
        return card;
    }

    Card BuildRightCard()
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(14, 0, 0, 0), Padding = new Padding(28) };

        _bankedNum = new Label
        {
            Text = "— min",
            Font = new Font("Segoe UI", 34F, FontStyle.Bold),
            ForeColor = Pink,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var bankedSub = new Label
        {
            Text = "banked minutes — earn more in the ChoreBuddy app",
            Font = new Font("Segoe UI", 10F),
            ForeColor = InkSoft,
            AutoSize = false,
            Height = 22,
        };
        var spendHdr = new Label
        {
            Text = "Spend minutes to play now",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
        };

        var row = new Panel { Height = 52 };
        _spendInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 600,
            Value = 15,
            Font = new Font("Segoe UI", 16F),
            Width = 110,
            Height = 44,
            Location = new Point(0, 3),
            TextAlign = HorizontalAlignment.Center,
        };
        var minLbl = new Label
        {
            Text = "min",
            Font = new Font("Segoe UI", 12F),
            ForeColor = InkSoft,
            AutoSize = true,
            Location = new Point(122, 15),
        };
        _spendBtn = new AccentButton
        {
            Text = "Add time",
            Size = new Size(150, 46),
            Location = new Point(172, 3),
        };
        _spendBtn.Click += OnSpend;
        row.Controls.Add(_spendInput);
        row.Controls.Add(minLbl);
        row.Controls.Add(_spendBtn);

        _spendNote = new Label
        {
            Text = "Adds game time instantly and syncs to the app.",
            UseMnemonic = false,
            Font = new Font("Segoe UI", 10F),
            ForeColor = InkFaint,
            AutoSize = false,
            Height = 44,
        };

        var tlp = NewCardLayout();
        AddRow(tlp, MakeCaption("YOUR SCREEN TIME"), 0, false);
        AddRow(tlp, _bankedNum, 2, false);
        AddRow(tlp, bankedSub, 0, true);
        AddRow(tlp, spendHdr, 22, false);
        AddRow(tlp, row, 10, true);
        AddRow(tlp, _spendNote, 14, true);
        card.Controls.Add(tlp);
        return card;
    }

    Label MakeCaption(string text) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
        ForeColor = InkFaint,
        AutoSize = true,
    };

    /** A 1-column TableLayoutPanel that fills the card; rows are added via
     *  AddRow. Unlike a Dock=Top stack, this honors each row's Margin, so the
     *  vertical rhythm is exactly what AddRow's `topMargin` specifies. */
    static TableLayoutPanel NewCardLayout()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 0,
            BackColor = CardBg,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    /** Append one auto-height row. `top` is the gap above it (px). `fill`
     *  stretches the control to the column width (for bars, pills, wrapping
     *  text); otherwise it hugs its content, left-aligned. */
    static void AddRow(TableLayoutPanel t, Control c, int top, bool fill)
    {
        c.Margin = new Padding(0, top, 0, 0);
        c.Anchor = fill
            ? (AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top)
            : (AnchorStyles.Left | AnchorStyles.Top);
        var r = t.RowCount;
        t.RowCount = r + 1;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, r);
    }

    // ---------------- spend ----------------
    void OnSpend(object? sender, EventArgs e)
    {
        var want = (int)_spendInput.Value;
        if (want <= 0) return;
        if (_banked <= 0)
        {
            Flash("No banked minutes — redeem screen time in the app first.", Red);
            return;
        }
        if (want > _banked)
        {
            want = _banked;
            _spendInput.Value = want;
            Flash($"You only have {_banked} min — capped to that.", Red);
        }

        // Hand the spend to the service (it owns the wallet + the session).
        ExtendRequest.Write(new ExtendRequestData
        {
            Minutes = want,
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Optimistic local update so it feels instant; the service reconciles
        // within ~2 s and the next status write confirms.
        _banked = Math.Max(0, _banked - want);
        if (_displaySeconds < 0) _displaySeconds = 0;
        _displaySeconds += want * 60;
        _maxSeconds = Math.Max(_maxSeconds, _displaySeconds);
        _state.Allowed = true;
        RenderTime();
        RenderBanked();
        RenderStatus();
        Flash($"✓ Added {want} min — have fun!", Good);

        _spendBtn.Enabled = false;
        var reenable = new System.Windows.Forms.Timer { Interval = 1500 };
        reenable.Tick += (s2, e2) => { _spendBtn.Enabled = true; reenable.Stop(); reenable.Dispose(); };
        reenable.Start();
    }

    void Flash(string msg, Color color)
    {
        _spendNote.Text = msg;
        _spendNote.ForeColor = color;
        var reset = new System.Windows.Forms.Timer { Interval = 4000 };
        reset.Tick += (s, e) =>
        {
            _spendNote.Text = "Adds game time instantly and syncs to the app.";
            _spendNote.ForeColor = InkFaint;
            reset.Stop(); reset.Dispose();
        };
        reset.Start();
    }

    // ---------------- polling / interpolation ----------------
    void OnTick()
    {
        var fresh = GameTimeStatus.Read();
        // Adopt a fresh snapshot when the service wrote a newer one.
        if (fresh.Timestamp != _lastStamp)
        {
            _lastStamp = fresh.Timestamp;
            _state = fresh;
            _banked = fresh.MinutesBanked;
            // Resync the countdown to the authoritative value (unless we just
            // optimistically bumped it past what the service knows yet).
            if (fresh.SecondsRemaining >= 0 &&
                (_displaySeconds < 0 || Math.Abs(fresh.SecondsRemaining - _displaySeconds) > 5))
            {
                _displaySeconds = fresh.SecondsRemaining;
            }
            if (fresh.SecondsRemaining < 0) _displaySeconds = -1;
            if (_displaySeconds > _maxSeconds) _maxSeconds = _displaySeconds;
            Refresh_();
        }
        else if (_state.Allowed && _displaySeconds > 0)
        {
            // Between service writes — tick the local countdown down.
            _displaySeconds--;
            RenderTime();
            if (_displaySeconds == 0) { _state.Allowed = false; RenderStatus(); }
        }
    }

    void Refresh_()
    {
        _kidPill.Text = string.IsNullOrWhiteSpace(_state.KidName) ? "🐼  Buddy" : $"🐼  {_state.KidName}";
        _kidPill.Invalidate();
        RenderTime();
        RenderBanked();
        RenderStatus();
    }

    void RenderTime()
    {
        if (_displaySeconds < 0)
        {
            _bigTime.Text = _state.Allowed ? "∞" : "0:00";
            _timeSub.Text = _state.Allowed ? "no time limit right now" : "locked";
            _bar.Fraction = _state.Allowed ? 1f : 0f;
            return;
        }
        var min = _displaySeconds / 60;
        var sec = _displaySeconds % 60;
        _bigTime.Text = $"{min}:{sec:00}";
        _timeSub.Text = $"{min} min left · base + earned";
        _bar.Fraction = _maxSeconds > 0 ? Math.Min(1f, (float)_displaySeconds / _maxSeconds) : 0f;
    }

    void RenderBanked() => _bankedNum.Text = $"{_banked} min";

    void RenderStatus()
    {
        if (_state.Allowed)
        {
            _statusPill.Text = "●  Games allowed";
            _statusPill.ForeColor = Good;
        }
        else
        {
            _statusPill.Text = "●  Games locked";
            _statusPill.ForeColor = Red;
        }
        _statusPill.Invalidate();
    }

    void PaintPill(Graphics g, Label pill)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var allowed = _state.Allowed;
        using var bg = new SolidBrush(allowed ? GoodBg : LockBg);
        using var pen = new Pen(allowed ? GoodBorder : LockBorder, 1.5f);
        using var path = Rounded(new Rectangle(0, 0, pill.Width - 2, pill.Height - 2), 14);
        g.FillPath(bg, path);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, pill.Text, pill.Font,
            new Rectangle(18, 0, pill.Width - 18, pill.Height), pill.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------------- nested controls ----------------
    /** White rounded card with a hairline border. */
    class Card : Panel
    {
        public Card() { BackColor = Color.White; DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 18);
            using var fill = new SolidBrush(CardBg);
            using var pen = new Pen(CardBorder, 1.4f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(pen, path);
        }
    }

    /** Thin progress track with a purple→pink gradient fill. */
    class Bar : Panel
    {
        float _fraction;
        public float Fraction { get => _fraction; set { _fraction = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public Bar() { DoubleBuffered = true; BackColor = Color.White; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var h = Math.Min(Height, 10);
            var r = new Rectangle(0, (Height - h) / 2, Width - 1, h);
            using (var track = new SolidBrush(TrackBg))
            using (var tp = Rounded(r, h / 2))
                g.FillPath(track, tp);
            var w = (int)((Width - 1) * _fraction);
            if (w > h)
            {
                var fr = new Rectangle(0, (Height - h) / 2, w, h);
                using var grad = new LinearGradientBrush(fr, Purple, Red, 0f);
                using var fp = Rounded(fr, h / 2);
                g.FillPath(grad, fp);
            }
        }
    }

    /** Rounded, filled pink accent button. */
    class AccentButton : Button
    {
        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Pink;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Rounded(rect, Height / 2);
            using var fill = new SolidBrush(Enabled ? Pink : Color.FromArgb(244, 168, 200));
            g.FillPath(fill, path);
            TextRenderer.DrawText(g, Text, Font, rect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
