using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeckLinkStreamStudio.Forms;

public sealed class FullscreenPreviewForm : Form
{
    private readonly PictureBox _pictureBox = new();
    private readonly Label _titleLabel = new();

    public FullscreenPreviewForm(string channelName)
    {
        Text = $"{channelName} - Live Fullscreen Monitor";
        BackColor = Color.Black;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;

        _pictureBox.Dock = DockStyle.Fill;
        _pictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        _pictureBox.BackColor = Color.Black;
        _pictureBox.DoubleClick += (s, e) => Close();

        _titleLabel.Text = $"{channelName} | Press ESC or Double-Click to Exit";
        _titleLabel.ForeColor = Color.FromArgb(200, 220, 240);
        _titleLabel.BackColor = Color.FromArgb(160, 20, 24, 30);
        _titleLabel.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _titleLabel.AutoSize = true;
        _titleLabel.Padding = new Padding(12, 6, 12, 6);
        _titleLabel.Location = new Point(20, 20);

        Controls.Add(_titleLabel);
        Controls.Add(_pictureBox);

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F11)
            {
                Close();
            }
        };

        _titleLabel.BringToFront();
    }

    public void UpdateFrame(Bitmap? frame)
    {
        if (IsDisposed || frame == null) return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => UpdateFrame(frame)));
            }
            catch { }
            return;
        }

        var old = _pictureBox.Image;
        _pictureBox.Image = (Bitmap)frame.Clone();
        old?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _pictureBox.Image?.Dispose();
    }
}
