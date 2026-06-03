using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChoreBuddy.TestApp;

public class OverlayForm : Form
{
    Label _title = null!;
    Label _subtitle = null!;
    Button _dismissBtn = null!;
    OverlayStateData _state;
    Action _onDismiss;

    public OverlayForm(OverlayStateData initial, Action onDismiss)
    {
        _state = initial;
        _onDismiss = onDismiss;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.92;
        DoubleBuffered = true;

        // Primary monitor only
        var screen = Screen.PrimaryScreen!.Bounds;
        Bounds = screen;

        BuildUI();
        ApplyState(initial);

        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !_state.Dismissable)
                e.Cancel = true;
        };
        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape && _state.Dismissable) Dismiss();
            else if (e.KeyCode == Keys.F4 && e.Alt) e.SuppressKeyPress = true;
        };
    }

    void BuildUI()
    {
        var center = new Panel
        {
            Width = 1100,
            Height = 360,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.None
        };

        _title = new Label
        {
            Text = "Game Time Suspended",
            Font = new Font("Segoe UI", 48, FontStyle.Bold),
            ForeColor = Color.FromArgb(245, 200, 66),
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Height = 110,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false
        };

        _subtitle = new Label
        {
            Text = "Finish your chores to unlock.",
            Font = new Font("Segoe UI", 22),
            ForeColor = Color.FromArgb(220, 220, 230),
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Height = 70,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoEllipsis = false
        };

        var divider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Color.Transparent
        };

        _dismissBtn = new Button
        {
            Text = "Got it",
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Width = 160,
            Height = 50,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 197, 94),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top
        };
        _dismissBtn.FlatAppearance.BorderSize = 0;
        _dismissBtn.Click += (s, e) => Dismiss();

        var btnHolder = new Panel
        {
            Dock = DockStyle.Top,
            Height = 80,
            BackColor = Color.Transparent
        };
        btnHolder.Resize += (s, e) =>
        {
            _dismissBtn.Left = (btnHolder.Width - _dismissBtn.Width) / 2;
            _dismissBtn.Top = 15;
        };
        btnHolder.Controls.Add(_dismissBtn);

        center.Controls.Add(btnHolder);
        center.Controls.Add(divider);
        center.Controls.Add(_subtitle);
        center.Controls.Add(_title);

        Controls.Add(center);
        Resize += (s, e) =>
        {
            center.Left = (ClientSize.Width - center.Width) / 2;
            center.Top = (ClientSize.Height - center.Height) / 2;
        };
        center.Left = (ClientSize.Width - center.Width) / 2;
        center.Top = (ClientSize.Height - center.Height) / 2;
    }

    public void ApplyState(OverlayStateData state)
    {
        _state = state;
        if (!string.IsNullOrEmpty(state.KidName))
            _subtitle.Text = $"{state.KidName}, finish your chores to unlock.";
        else
            _subtitle.Text = "Finish your chores to unlock.";

        _dismissBtn.Visible = state.Dismissable;
    }

    void Dismiss()
    {
        if (!_state.Dismissable) return;
        _onDismiss();
        Hide();
    }
}
