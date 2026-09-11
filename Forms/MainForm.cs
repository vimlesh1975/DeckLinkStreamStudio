using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DeckLinkStreamStudio.Engines;
using DeckLinkStreamStudio.Models;

namespace DeckLinkStreamStudio.Forms;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly StreamConfig _config;
    private readonly FfmpegStreamRunner _runner;
    private readonly AudioMonitorRunner _audioMonitor = new();
    private readonly List<DeckLinkDeviceEntry> _devices;
    private FullscreenPreviewForm? _fullscreenForm;

    // Top Master Header
    private readonly Panel _topHeader = new();
    private readonly Label _appTitle = new();
    private readonly Label _lblCard = new();
    private readonly ComboBox _deviceComboBox = new();
    private readonly Label _lblFormat = new();
    private readonly ComboBox _formatComboBox = new();
    private readonly Label _statusBadge = new();
    private readonly Label _cpuBadge = new();
    private readonly Button _btnStartStream = new();
    private readonly Button _btnPreview = new();
    private readonly Button _btnListen = new();
    private readonly CheckBox _chkShowLogs = new();
    private readonly CheckBox _chkDarkMode = new();

    // Main Container Panel
    private readonly Panel _leftPanel = new();
    private readonly Panel _videoContainer = new();
    private readonly PictureBox _previewBox = new();
    private readonly Label _standbyWatermark = new();
    private readonly Panel _videoToolBar = new();
    private readonly CheckBox _chkDeinterlace = new();
    private readonly Label _lblAudioDelay = new();
    private readonly NumericUpDown _delayUpDown = new();
    private readonly Button _btnSnapshot = new();
    private readonly Button _btnFullscreen = new();

    private readonly TableLayoutPanel _statsTable = new();
    private readonly Label _lblDuration = new();
    private readonly Label _lblBitrate = new();
    private readonly Label _lblFps = new();
    private readonly Label _lblDropped = new();
    private readonly Label _lblCpu = new();
    private readonly Label _lblSpeed = new();

    private readonly Panel _hwSettingsPanel = new();
    private readonly Label _lblHwTitle = new();
    private readonly Label _lblEncoderTitle = new();
    private readonly ComboBox _encoderCombo = new();
    private readonly NumericUpDown _bitrateUpDown = new();
    private readonly Label _lblKbps = new();

    // Destinations Section (Directly below encoder, no middle gap)
    private readonly Panel _rightPanel = new();
    private readonly Label _lblDestTitle = new();

    // Destination 1: Sahyadri Facebook
    private readonly Panel _pnlFb = new();
    private readonly CheckBox _chkFb = new();
    private readonly Label _lblFbUrl = new();
    private readonly TextBox _txtFbUrl = new();
    private readonly Button _btnStreamFb = new();
    private readonly Label _lblFbKey = new();
    private readonly TextBox _txtFbKey = new();
    private readonly Button _btnToggleFbKey = new();

    // Destination 2: Sahyadri YouTube
    private readonly Panel _pnlYt = new();
    private readonly CheckBox _chkYt = new();
    private readonly Label _lblYtUrl = new();
    private readonly TextBox _txtYtUrl = new();
    private readonly Button _btnStreamYt = new();
    private readonly Label _lblYtKey = new();
    private readonly TextBox _txtYtKey = new();
    private readonly Button _btnToggleYtKey = new();

    // Destination 3: Sahyadri YouTube News
    private readonly Panel _pnlYtNews = new();
    private readonly CheckBox _chkYtNews = new();
    private readonly Label _lblYtNewsUrl = new();
    private readonly TextBox _txtYtNewsUrl = new();
    private readonly Button _btnStreamYtNews = new();
    private readonly Label _lblYtNewsKey = new();
    private readonly TextBox _txtYtNewsKey = new();
    private readonly Button _btnToggleYtNewsKey = new();

    // Bottom Diagnostic Console
    private readonly Panel _logPanel = new();
    private readonly Panel _logHeaderPanel = new();
    private readonly Label _lblLogTitle = new();
    private readonly Button _btnClearLog = new();
    private readonly TextBox _logTextBox = new();

    // CPU Timer
    private readonly System.Windows.Forms.Timer _cpuTimer = new() { Interval = 1000 };

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    private ulong _lastIdleTicks;
    private ulong _lastKernelTicks;
    private ulong _lastUserTicks;
    private bool _hasCpuSample;

    public MainForm()
    {
        _settings = AppSettings.Load();
        _config = _settings.StreamSettings;
        _devices = DeckLinkEnumerator.GetInstalledDevices();
        _runner = new FfmpegStreamRunner();

        InitializeForm();
        InitializeTopHeader();
        InitializeWorkspace();
        InitializeLogConsole();
        HookRunnerEvents();
        LoadConfigIntoUi();

        // Ensure proper WinForms docking order: _leftPanel (DockStyle.Fill) must be at front
        // so _topHeader (DockStyle.Top) does not overlap or cut off the top of the video preview.
        _leftPanel.BringToFront();

        ApplyTheme(_settings.DarkMode);

        _cpuTimer.Tick += (s, e) => UpdateCpuUsage();
        _cpuTimer.Start();
        UpdateCpuUsage();

        Shown += (s, e) =>
        {
            if (!_runner.IsRunning)
            {
                ToggleStandbyPreview();
            }
        };
    }

    private void InitializeForm()
    {
        Text = "Sahyadri DeckLink Broadcaster (x64 Release)";
        Size = new Size(720, 800);
        MinimumSize = new Size(680, 770);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;
        MaximizeBox = false;
    }

    private void InitializeTopHeader()
    {
        _topHeader.Dock = DockStyle.Top;
        _topHeader.Height = 68;
        _topHeader.Padding = new Padding(8, 6, 8, 6);

        // Row 1: Ingest Hardware & Status
        _appTitle.Text = "📡 LIVE";
        _appTitle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _appTitle.ForeColor = Color.FromArgb(56, 189, 248);
        _appTitle.AutoSize = true;
        _appTitle.Location = new Point(8, 10);

        _lblCard.Text = "CARD:";
        _lblCard.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _lblCard.AutoSize = true;
        _lblCard.Location = new Point(66, 12);

        _deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceComboBox.FlatStyle = FlatStyle.Flat;
        _deviceComboBox.Font = new Font("Segoe UI", 8.5f);
        _deviceComboBox.Width = 120;
        _deviceComboBox.Location = new Point(108, 8);

        foreach (var d in _devices) _deviceComboBox.Items.Add(d.Name);
        int devIdx = _deviceComboBox.FindStringExact(_config.DeckLinkDevice);
        if (devIdx < 0)
        {
            for (int i = 0; i < _deviceComboBox.Items.Count; i++)
            {
                var itemStr = _deviceComboBox.Items[i]?.ToString() ?? "";
                if (itemStr.Contains("go1080p25", StringComparison.OrdinalIgnoreCase) &&
                    _config.DeckLinkDevice.Contains("go1080p25", StringComparison.OrdinalIgnoreCase))
                {
                    devIdx = i;
                    break;
                }
            }
        }
        _deviceComboBox.SelectedIndex = devIdx >= 0 ? devIdx : 0;
        _deviceComboBox.SelectedIndexChanged += (s, e) =>
        {
            if (_deviceComboBox.SelectedItem != null)
            {
                _config.DeckLinkDevice = _deviceComboBox.SelectedItem.ToString()!;
                _settings.Save();

                bool isFile = FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
                _formatComboBox.Enabled = !isFile;

                if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.StandbyPreview)
                {
                    _runner.Stop();
                    _runner.StartStandbyPreview(_config);
                }
            }
        };

        // Video Standard Dropdown (STD) - 165px wide
        _lblFormat.Text = "STD:";
        _lblFormat.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _lblFormat.AutoSize = true;
        _lblFormat.Location = new Point(234, 12);

        _formatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatComboBox.FlatStyle = FlatStyle.Flat;
        _formatComboBox.Font = new Font("Segoe UI", 8.5f);
        _formatComboBox.Width = 165;
        _formatComboBox.Location = new Point(266, 8);

        foreach (var std in DeckLinkStandardItem.Standards) _formatComboBox.Items.Add(std);
        int stdIdx = 0;
        for (int i = 0; i < DeckLinkStandardItem.Standards.Count; i++)
        {
            if (DeckLinkStandardItem.Standards[i].Code.Equals(_config.VideoStandardCode, StringComparison.OrdinalIgnoreCase))
            {
                stdIdx = i;
                break;
            }
        }
        _formatComboBox.SelectedIndex = stdIdx;
        _formatComboBox.SelectedIndexChanged += (s, e) =>
        {
            if (_formatComboBox.SelectedItem is DeckLinkStandardItem item)
            {
                _config.VideoStandardCode = item.Code;
                _settings.Save();
                if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.StandbyPreview)
                {
                    _runner.Stop();
                    _runner.StartStandbyPreview(_config);
                }
            }
        };

        bool isFileInitial = FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
        _formatComboBox.Enabled = !isFileInitial;

        // Status Badge
        _statusBadge.Text = "OFFLINE";
        _statusBadge.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _statusBadge.Padding = new Padding(5, 3, 5, 3);
        _statusBadge.AutoSize = true;
        _statusBadge.Location = new Point(445, 9);

        // CPU Usage Badge
        _cpuBadge.Text = "CPU: 0%";
        _cpuBadge.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _cpuBadge.Padding = new Padding(5, 3, 5, 3);
        _cpuBadge.AutoSize = true;
        _cpuBadge.Location = new Point(525, 9);
        _cpuBadge.BackColor = Color.FromArgb(30, 41, 59);
        _cpuBadge.ForeColor = Color.FromArgb(56, 189, 248);

        // Row 2: Action Buttons & Options
        _btnStartStream.Text = "🔴 STREAM";
        _btnStartStream.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _btnStartStream.BackColor = Color.FromArgb(16, 185, 129);
        _btnStartStream.ForeColor = Color.White;
        _btnStartStream.FlatStyle = FlatStyle.Flat;
        _btnStartStream.FlatAppearance.BorderSize = 0;
        _btnStartStream.Width = 95;
        _btnStartStream.Height = 26;
        _btnStartStream.Location = new Point(8, 36);
        _btnStartStream.Click += (s, e) => ToggleStreaming();

        _btnPreview.Text = "👁 PREVIEW";
        _btnPreview.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
        _btnPreview.ForeColor = Color.White;
        _btnPreview.FlatStyle = FlatStyle.Flat;
        _btnPreview.FlatAppearance.BorderSize = 0;
        _btnPreview.Width = 90;
        _btnPreview.Height = 26;
        _btnPreview.Location = new Point(109, 36);
        _btnPreview.Click += (s, e) => ToggleStandbyPreview();

        _btnListen.Text = "🎧 LISTEN";
        _btnListen.Font = new Font("Segoe UI", 8f);
        _btnListen.BackColor = Color.FromArgb(51, 65, 85);
        _btnListen.ForeColor = Color.FromArgb(226, 232, 240);
        _btnListen.FlatStyle = FlatStyle.Flat;
        _btnListen.FlatAppearance.BorderSize = 0;
        _btnListen.Width = 75;
        _btnListen.Height = 26;
        _btnListen.Location = new Point(205, 36);
        _btnListen.Click += (s, e) => ToggleAudioListen();

        // Logs checkbox
        _chkShowLogs.Text = "Logs";
        _chkShowLogs.Font = new Font("Segoe UI", 8f);
        _chkShowLogs.AutoSize = true;
        _chkShowLogs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _chkShowLogs.Checked = false;
        _chkShowLogs.CheckedChanged += (s, e) =>
        {
            _logPanel.Visible = _chkShowLogs.Checked;
        };

        // Dark mode checkbox
        _chkDarkMode.Text = "Dark";
        _chkDarkMode.Font = new Font("Segoe UI", 8f);
        _chkDarkMode.AutoSize = true;
        _chkDarkMode.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _chkDarkMode.Checked = _settings.DarkMode;
        _chkDarkMode.CheckedChanged += (s, e) =>
        {
            _settings.DarkMode = _chkDarkMode.Checked;
            _settings.Save();
            ApplyTheme(_chkDarkMode.Checked);
        };

        _topHeader.Controls.Add(_appTitle);
        _topHeader.Controls.Add(_lblCard);
        _topHeader.Controls.Add(_deviceComboBox);
        _topHeader.Controls.Add(_lblFormat);
        _topHeader.Controls.Add(_formatComboBox);
        _topHeader.Controls.Add(_statusBadge);
        _topHeader.Controls.Add(_cpuBadge);
        _topHeader.Controls.Add(_btnStartStream);
        _topHeader.Controls.Add(_btnPreview);
        _topHeader.Controls.Add(_btnListen);
        _topHeader.Controls.Add(_chkShowLogs);
        _topHeader.Controls.Add(_chkDarkMode);

        _topHeader.Resize += (s, e) => LayoutTopHeaderRightControls();
        LayoutTopHeaderRightControls();

        Controls.Add(_topHeader);
    }

    private void LayoutTopHeaderRightControls()
    {
        int w = _topHeader.ClientSize.Width;
        if (w <= 0) return;
        _chkDarkMode.Location = new Point(w - 50, 40);
        _chkShowLogs.Location = new Point(w - 104, 40);
    }

    private void InitializeWorkspace()
    {
        _leftPanel.Dock = DockStyle.Fill;
        _leftPanel.Padding = new Padding(6, 0, 6, 6);
        _leftPanel.AutoScroll = false; // Everything fits cleanly, no scrollbars

        InitializeLeftVideoPanel();
        InitializeRightDestinationsPanel();

        // Stacked order: video container on top, then toolbar, stats, encoder, and destinations
        _leftPanel.Controls.Add(_rightPanel);
        _leftPanel.Controls.Add(_hwSettingsPanel);
        _leftPanel.Controls.Add(_statsTable);
        _leftPanel.Controls.Add(_videoToolBar);
        _leftPanel.Controls.Add(_videoContainer);

        Controls.Add(_leftPanel);
    }

    private void InitializeLeftVideoPanel()
    {
        // 1. Video Container (480x250 video preview + VU meters)
        _videoContainer.Dock = DockStyle.Top;
        _videoContainer.Height = 252;
        _videoContainer.BackColor = Color.FromArgb(12, 14, 18);

        _previewBox.Dock = DockStyle.None;
        _previewBox.Size = new Size(480, 250);
        _previewBox.SizeMode = PictureBoxSizeMode.Normal;
        _previewBox.BackColor = Color.Black;
        _previewBox.Location = new Point(Math.Max(0, (_videoContainer.ClientSize.Width - 480) / 2), 1);
        _previewBox.DoubleClick += (s, e) => OpenFullscreen();

        _standbyWatermark.Text = "STANDBY / NO SIGNAL\n(Left & Right Peak Audio Meters Ready)";
        _standbyWatermark.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        _standbyWatermark.TextAlign = ContentAlignment.MiddleCenter;
        _standbyWatermark.Dock = DockStyle.Fill;
        _standbyWatermark.BackColor = Color.Transparent;

        _previewBox.Controls.Add(_standbyWatermark);
        _videoContainer.Controls.Add(_previewBox);

        _videoContainer.Resize += (s, e) =>
        {
            _previewBox.Location = new Point(
                Math.Max(0, (_videoContainer.ClientSize.Width - 480) / 2),
                Math.Max(0, (_videoContainer.ClientSize.Height - 250) / 2)
            );
        };

        // 2. Toolbar under Video
        _videoToolBar.Dock = DockStyle.Top;
        _videoToolBar.Height = 30;
        _videoToolBar.Padding = new Padding(4, 2, 4, 2);

        _chkDeinterlace.Text = "YADIF";
        _chkDeinterlace.Font = new Font("Segoe UI", 8f);
        _chkDeinterlace.AutoSize = true;
        _chkDeinterlace.Location = new Point(4, 6);
        _chkDeinterlace.Checked = _config.Deinterlace;
        _chkDeinterlace.CheckedChanged += (s, e) =>
        {
            _config.Deinterlace = _chkDeinterlace.Checked;
            _settings.Save();
        };

        _lblAudioDelay.Text = "Delay:";
        _lblAudioDelay.Font = new Font("Segoe UI", 8f);
        _lblAudioDelay.AutoSize = true;
        _lblAudioDelay.Location = new Point(68, 8);

        _delayUpDown.BorderStyle = BorderStyle.FixedSingle;
        _delayUpDown.Font = new Font("Segoe UI", 8f);
        _delayUpDown.Minimum = 0;
        _delayUpDown.Maximum = 5000;
        _delayUpDown.Value = _config.AudioDelayMs;
        _delayUpDown.Increment = 50;
        _delayUpDown.Width = 55;
        _delayUpDown.Location = new Point(106, 5);
        _delayUpDown.ValueChanged += (s, e) =>
        {
            _config.AudioDelayMs = (int)_delayUpDown.Value;
            _settings.Save();
        };

        _btnSnapshot.Text = "📸 SNAP";
        _btnSnapshot.Font = new Font("Segoe UI", 7.5f);
        _btnSnapshot.FlatStyle = FlatStyle.Flat;
        _btnSnapshot.FlatAppearance.BorderSize = 0;
        _btnSnapshot.Width = 58;
        _btnSnapshot.Height = 22;
        _btnSnapshot.Location = new Point(168, 4);
        _btnSnapshot.Click += (s, e) => TakeSnapshot();

        _btnFullscreen.Text = "⛶ FULL";
        _btnFullscreen.Font = new Font("Segoe UI", 7.5f);
        _btnFullscreen.FlatStyle = FlatStyle.Flat;
        _btnFullscreen.FlatAppearance.BorderSize = 0;
        _btnFullscreen.Width = 55;
        _btnFullscreen.Height = 22;
        _btnFullscreen.Location = new Point(230, 4);
        _btnFullscreen.Click += (s, e) => OpenFullscreen();

        _videoToolBar.Controls.Add(_chkDeinterlace);
        _videoToolBar.Controls.Add(_lblAudioDelay);
        _videoToolBar.Controls.Add(_delayUpDown);
        _videoToolBar.Controls.Add(_btnSnapshot);
        _videoToolBar.Controls.Add(_btnFullscreen);

        // 3. Compact Live Broadcast HUD
        _statsTable.Dock = DockStyle.Top;
        _statsTable.Height = 36;
        _statsTable.ColumnCount = 6;
        _statsTable.RowCount = 1;
        _statsTable.Margin = new Padding(0, 3, 0, 3);

        for (int i = 0; i < 6; i++)
            _statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));

        FormatStatLabel(_lblDuration, "TIME", "00:00:00");
        FormatStatLabel(_lblBitrate, "BITRATE", "0 kbps");
        FormatStatLabel(_lblFps, "FPS", "0.0");
        FormatStatLabel(_lblDropped, "DROPS", "0");
        FormatStatLabel(_lblCpu, "CPU", "0%");
        FormatStatLabel(_lblSpeed, "SPEED", "1.00x");

        _statsTable.Controls.Add(_lblDuration, 0, 0);
        _statsTable.Controls.Add(_lblBitrate, 1, 0);
        _statsTable.Controls.Add(_lblFps, 2, 0);
        _statsTable.Controls.Add(_lblDropped, 3, 0);
        _statsTable.Controls.Add(_lblCpu, 4, 0);
        _statsTable.Controls.Add(_lblSpeed, 5, 0);

        // 4. Encoder Quick Panel (Compact single-line)
        _hwSettingsPanel.Dock = DockStyle.Top;
        _hwSettingsPanel.Height = 36;
        _hwSettingsPanel.Padding = new Padding(6, 2, 6, 2);
        _hwSettingsPanel.Margin = new Padding(0, 2, 0, 0);

        _lblHwTitle.Text = "ENCODER:";
        _lblHwTitle.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _lblHwTitle.AutoSize = true;
        _lblHwTitle.Location = new Point(8, 10);
        _hwSettingsPanel.Controls.Add(_lblHwTitle);

        _encoderCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _encoderCombo.FlatStyle = FlatStyle.Flat;
        _encoderCombo.Font = new Font("Segoe UI", 8.5f);
        _encoderCombo.Width = 135;
        _encoderCombo.Location = new Point(78, 6);
        _encoderCombo.Items.Add("NVENC (GPU)");
        _encoderCombo.Items.Add("CPU (libx264)");
        _encoderCombo.Items.Add("HEVC NVENC");
        _encoderCombo.SelectedIndex = 0;
        _encoderCombo.SelectedIndexChanged += (s, e) =>
        {
            _config.VideoEncoder = _encoderCombo.SelectedIndex switch
            {
                0 => VideoEncoderType.H264_NVENC,
                1 => VideoEncoderType.LibX264,
                2 => VideoEncoderType.HEVC_NVENC,
                _ => VideoEncoderType.H264_NVENC
            };
            _settings.Save();
        };
        _hwSettingsPanel.Controls.Add(_encoderCombo);

        _lblEncoderTitle.Text = "Bitrate:";
        _lblEncoderTitle.Font = new Font("Segoe UI", 8f);
        _lblEncoderTitle.AutoSize = true;
        _lblEncoderTitle.Location = new Point(222, 10);
        _hwSettingsPanel.Controls.Add(_lblEncoderTitle);

        _bitrateUpDown.BorderStyle = BorderStyle.FixedSingle;
        _bitrateUpDown.Font = new Font("Segoe UI", 8.5f);
        _bitrateUpDown.Minimum = 1000;
        _bitrateUpDown.Maximum = 30000;
        _bitrateUpDown.Increment = 500;
        _bitrateUpDown.Value = _config.VideoBitrateKbps;
        _bitrateUpDown.Width = 70;
        _bitrateUpDown.Location = new Point(270, 7);
        _bitrateUpDown.ValueChanged += (s, e) =>
        {
            _config.VideoBitrateKbps = (int)_bitrateUpDown.Value;
            _settings.Save();
        };
        _hwSettingsPanel.Controls.Add(_bitrateUpDown);

        _lblKbps.Text = "kbps";
        _lblKbps.Font = new Font("Segoe UI", 8f);
        _lblKbps.AutoSize = true;
        _lblKbps.Location = new Point(344, 10);
        _hwSettingsPanel.Controls.Add(_lblKbps);
    }

    private void InitializeRightDestinationsPanel()
    {
        _rightPanel.Dock = DockStyle.Top;
        _rightPanel.Height = 306;
        _rightPanel.Padding = new Padding(0, 2, 0, 0);
        _rightPanel.AutoScroll = false; // Zero scrollbars on URL part!

        int y = 2;

        _lblDestTitle.Text = "3 BROADCAST DESTINATIONS";
        _lblDestTitle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _lblDestTitle.ForeColor = Color.FromArgb(56, 189, 248);
        _lblDestTitle.AutoSize = true;
        _lblDestTitle.Location = new Point(6, y);
        _rightPanel.Controls.Add(_lblDestTitle);
        y += 24;

        // Destination 1: Sahyadri Facebook Card
        BuildDestinationCard(_pnlFb, "Sahyadri Facebook", Color.FromArgb(59, 130, 246), 0, y,
            _chkFb, _lblFbUrl, _txtFbUrl, _btnStreamFb, _lblFbKey, _txtFbKey, _btnToggleFbKey);
        _rightPanel.Controls.Add(_pnlFb);
        y += 94;

        // Destination 2: Sahyadri YouTube Card
        BuildDestinationCard(_pnlYt, "Sahyadri YouTube", Color.FromArgb(239, 68, 68), 1, y,
            _chkYt, _lblYtUrl, _txtYtUrl, _btnStreamYt, _lblYtKey, _txtYtKey, _btnToggleYtKey);
        _rightPanel.Controls.Add(_pnlYt);
        y += 94;

        // Destination 3: Sahyadri YouTube News Card
        BuildDestinationCard(_pnlYtNews, "Sahyadri YouTube News", Color.FromArgb(245, 158, 11), 2, y,
            _chkYtNews, _lblYtNewsUrl, _txtYtNewsUrl, _btnStreamYtNews, _lblYtNewsKey, _txtYtNewsKey, _btnToggleYtNewsKey);
        _rightPanel.Controls.Add(_pnlYtNews);

        _leftPanel.Resize += (s, e) => UpdateDestinationCardWidths();
        UpdateDestinationCardWidths();
    }

    private void UpdateDestinationCardWidths()
    {
        int availableWidth = Math.Max(280, _leftPanel.ClientSize.Width - 12);
        _pnlFb.Width = availableWidth;
        _pnlYt.Width = availableWidth;
        _pnlYtNews.Width = availableWidth;
    }

    private void BuildDestinationCard(Panel pnl, string name, Color accentColor, int destIndex, int y,
        CheckBox chk, Label lblUrl, TextBox txtUrl, Button btnStream, Label lblKey, TextBox txtKey, Button btnEye)
    {
        int initialWidth = Math.Max(280, _leftPanel.ClientSize.Width > 0 ? _leftPanel.ClientSize.Width - 12 : 690);
        pnl.Location = new Point(6, y);
        pnl.Size = new Size(initialWidth, 88);
        pnl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pnl.Padding = new Padding(6);

        var accentStrip = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(4, 88),
            BackColor = accentColor
        };
        pnl.Controls.Add(accentStrip);

        chk.Text = name;
        chk.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        chk.AutoSize = true;
        chk.Location = new Point(14, 5);
        chk.Checked = _config.Destinations[destIndex].Enabled;
        chk.CheckedChanged += (s, e) =>
        {
            _config.Destinations[destIndex].Enabled = chk.Checked;
            _settings.Save();
            UpdateDestinationButtons();
        };
        pnl.Controls.Add(chk);

        // URL Field - Clear visible label and wide input with start-aligned text
        lblUrl.Text = "URL:";
        lblUrl.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        lblUrl.AutoSize = true;
        lblUrl.Location = new Point(14, 29);

        txtUrl.BorderStyle = BorderStyle.FixedSingle;
        txtUrl.Font = new Font("Segoe UI", 9f);
        txtUrl.Location = new Point(54, 27);
        txtUrl.Width = Math.Max(100, pnl.ClientSize.Width - 54 - 14 - 84 - 8);
        txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtUrl.Text = _config.Destinations[destIndex].ServerUrl;
        txtUrl.Select(0, 0);
        txtUrl.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].ServerUrl = txtUrl.Text;
            _settings.Save();
        };
        pnl.Controls.Add(lblUrl);
        pnl.Controls.Add(txtUrl);

        // STREAM Button to right of URL
        btnStream.Text = "🔴 STREAM";
        btnStream.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        btnStream.BackColor = Color.FromArgb(16, 185, 129);
        btnStream.ForeColor = Color.White;
        btnStream.FlatStyle = FlatStyle.Flat;
        btnStream.FlatAppearance.BorderSize = 0;
        btnStream.Width = 84;
        btnStream.Height = 25;
        btnStream.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnStream.Location = new Point(pnl.ClientSize.Width - 14 - 84, 26);
        btnStream.Click += (s, e) => ToggleDestinationStream(destIndex);
        pnl.Controls.Add(btnStream);

        // Key Field
        lblKey.Text = "Key:";
        lblKey.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        lblKey.AutoSize = true;
        lblKey.Location = new Point(14, 57);

        txtKey.BorderStyle = BorderStyle.FixedSingle;
        txtKey.Font = new Font("Segoe UI", 9f);
        txtKey.UseSystemPasswordChar = true;
        txtKey.Location = new Point(54, 55);
        txtKey.Width = Math.Max(60, pnl.ClientSize.Width - 54 - 14 - 36 - 6);
        txtKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtKey.Text = _config.Destinations[destIndex].StreamKey;
        txtKey.Select(0, 0);
        txtKey.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].StreamKey = txtKey.Text;
            _settings.Save();
        };

        btnEye.Text = "👁";
        btnEye.Font = new Font("Segoe UI", 9f);
        btnEye.FlatStyle = FlatStyle.Flat;
        btnEye.FlatAppearance.BorderSize = 0;
        btnEye.Width = 36;
        btnEye.Height = 24;
        btnEye.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnEye.Location = new Point(pnl.ClientSize.Width - 14 - 36, 54);
        btnEye.Click += (s, e) =>
        {
            txtKey.UseSystemPasswordChar = !txtKey.UseSystemPasswordChar;
        };

        pnl.Controls.Add(lblKey);
        pnl.Controls.Add(txtKey);
        pnl.Controls.Add(btnEye);
    }

    private void InitializeLogConsole()
    {
        _logPanel.Dock = DockStyle.Bottom;
        _logPanel.Height = 120;
        _logPanel.Visible = false; // Collapsed by default

        _logHeaderPanel.Dock = DockStyle.Top;
        _logHeaderPanel.Height = 24;
        _logHeaderPanel.Padding = new Padding(6, 2, 6, 2);

        _lblLogTitle.Text = "DIAGNOSTICS & FFMPEG BROADCAST CONSOLE";
        _lblLogTitle.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _lblLogTitle.AutoSize = true;
        _lblLogTitle.Location = new Point(6, 4);

        _btnClearLog.Text = "CLEAR";
        _btnClearLog.Font = new Font("Segoe UI", 7f);
        _btnClearLog.FlatStyle = FlatStyle.Flat;
        _btnClearLog.FlatAppearance.BorderSize = 0;
        _btnClearLog.Width = 50;
        _btnClearLog.Height = 18;
        _btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnClearLog.Location = new Point(Width - 65, 3);
        _btnClearLog.Click += (s, e) => _logTextBox.Clear();

        _logHeaderPanel.Controls.Add(_lblLogTitle);
        _logHeaderPanel.Controls.Add(_btnClearLog);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.Font = new Font("Consolas", 8f);
        _logTextBox.BorderStyle = BorderStyle.None;

        _logPanel.Controls.Add(_logTextBox);
        _logPanel.Controls.Add(_logHeaderPanel);

        Controls.Add(_logPanel);
    }

    public void ApplyTheme(bool isDark)
    {
        Color bgMain = isDark ? Color.FromArgb(18, 22, 30) : Color.FromArgb(241, 245, 249);
        Color bgHeader = isDark ? Color.FromArgb(26, 32, 44) : Color.FromArgb(255, 255, 255);
        Color bgCard = isDark ? Color.FromArgb(28, 34, 46) : Color.FromArgb(255, 255, 255);
        Color bgControl = isDark ? Color.FromArgb(42, 50, 68) : Color.FromArgb(241, 245, 249);
        Color textMain = isDark ? Color.FromArgb(241, 245, 249) : Color.FromArgb(15, 23, 42);
        Color textSec = isDark ? Color.FromArgb(148, 163, 184) : Color.FromArgb(100, 116, 139);
        Color splitterBg = isDark ? Color.FromArgb(34, 40, 54) : Color.FromArgb(203, 213, 225);

        BackColor = bgMain;
        ForeColor = textMain;

        _topHeader.BackColor = bgHeader;
        _lblCard.ForeColor = textSec;
        _lblFormat.ForeColor = textSec;
        _chkShowLogs.ForeColor = textMain;
        _chkDarkMode.ForeColor = textMain;

        _deviceComboBox.BackColor = bgControl;
        _deviceComboBox.ForeColor = textMain;
        _formatComboBox.BackColor = bgControl;
        _formatComboBox.ForeColor = textMain;

        _leftPanel.BackColor = bgMain;
        _rightPanel.BackColor = bgMain;

        _videoToolBar.BackColor = isDark ? Color.FromArgb(24, 30, 42) : Color.FromArgb(226, 232, 240);
        _chkDeinterlace.ForeColor = textMain;
        _lblAudioDelay.ForeColor = textSec;
        _delayUpDown.BackColor = bgControl;
        _delayUpDown.ForeColor = textMain;
        _btnSnapshot.BackColor = bgControl;
        _btnSnapshot.ForeColor = textMain;
        _btnFullscreen.BackColor = bgControl;
        _btnFullscreen.ForeColor = textMain;

        _statsTable.BackColor = isDark ? Color.FromArgb(20, 24, 34) : Color.FromArgb(226, 232, 240);
        _lblDuration.ForeColor = textSec;
        _lblBitrate.ForeColor = textSec;
        _lblFps.ForeColor = textSec;
        _lblDropped.ForeColor = textSec;
        _lblCpu.ForeColor = textSec;
        _lblSpeed.ForeColor = textSec;

        // CPU Badge
        _cpuBadge.BackColor = isDark ? Color.FromArgb(30, 41, 59) : Color.FromArgb(226, 232, 240);
        _cpuBadge.ForeColor = isDark ? Color.FromArgb(56, 189, 248) : Color.FromArgb(2, 132, 199);

        _hwSettingsPanel.BackColor = bgCard;
        _lblHwTitle.ForeColor = textMain;
        _lblEncoderTitle.ForeColor = textSec;
        _encoderCombo.BackColor = bgControl;
        _encoderCombo.ForeColor = textMain;
        _bitrateUpDown.BackColor = bgControl;
        _bitrateUpDown.ForeColor = textMain;
        _lblKbps.ForeColor = textSec;

        // Destination Cards
        ApplyCardTheme(_pnlFb, _chkFb, _lblFbUrl, _txtFbUrl, _lblFbKey, _txtFbKey, _btnToggleFbKey, bgCard, bgControl, textMain, textSec);
        ApplyCardTheme(_pnlYt, _chkYt, _lblYtUrl, _txtYtUrl, _lblYtKey, _txtYtKey, _btnToggleYtKey, bgCard, bgControl, textMain, textSec);
        ApplyCardTheme(_pnlYtNews, _chkYtNews, _lblYtNewsUrl, _txtYtNewsUrl, _lblYtNewsKey, _txtYtNewsKey, _btnToggleYtNewsKey, bgCard, bgControl, textMain, textSec);

        // Log Console
        _logPanel.BackColor = isDark ? Color.FromArgb(12, 14, 18) : Color.FromArgb(241, 245, 249);
        _logHeaderPanel.BackColor = isDark ? Color.FromArgb(24, 28, 38) : Color.FromArgb(226, 232, 240);
        _lblLogTitle.ForeColor = textSec;
        _btnClearLog.BackColor = bgControl;
        _btnClearLog.ForeColor = textMain;
        _logTextBox.BackColor = isDark ? Color.FromArgb(10, 12, 16) : Color.FromArgb(255, 255, 255);
        _logTextBox.ForeColor = isDark ? Color.FromArgb(203, 213, 225) : Color.FromArgb(15, 23, 42);
    }

    private void ApplyCardTheme(Panel pnl, CheckBox chk, Label lUrl, TextBox tUrl, Label lKey, TextBox tKey, Button bEye,
        Color bgCard, Color bgControl, Color textMain, Color textSec)
    {
        pnl.BackColor = bgCard;
        chk.ForeColor = textMain;
        lUrl.ForeColor = textMain;
        tUrl.BackColor = bgControl;
        tUrl.ForeColor = textMain;
        lKey.ForeColor = textMain;
        tKey.BackColor = bgControl;
        tKey.ForeColor = textMain;
        bEye.BackColor = bgControl;
        bEye.ForeColor = textMain;
    }

    private void HookRunnerEvents()
    {
        _runner.OnPreviewFrame += frame =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    _standbyWatermark.Visible = false;
                    var old = _previewBox.Image;
                    _previewBox.Image = (Bitmap)frame.Clone();
                    old?.Dispose();

                    _fullscreenForm?.UpdateFrame(frame);
                }));
            }
            catch { }
        };

        _runner.OnStatsUpdated += stats =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    _lblDuration.Text = $"TIME\n{stats.Duration:hh\\:mm\\:ss}";
                    _lblBitrate.Text = $"BITRATE\n{stats.CurrentBitrateKbps:F0} kbps";
                    _lblFps.Text = $"FPS\n{stats.CurrentFps:F1}";
                    _lblDropped.Text = $"DROPS\n{stats.DroppedFrames}";
                    _lblSpeed.Text = $"SPEED\n{stats.SpeedRatio:F2}x";
                }));
            }
            catch { }
        };

        _runner.OnStatusChanged += (status, text) =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    UpdateStatusUi(status, text);
                }));
            }
            catch { }
        };

        _runner.OnLog += msg => AppendLog(msg);

        _runner.OnProcessExited += code =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    UpdateStatusUi(StreamStatus.Offline, "OFFLINE");
                    _standbyWatermark.Visible = true;
                    _standbyWatermark.Text = $"OFFLINE (Code {code})";
                    _previewBox.Image?.Dispose();
                    _previewBox.Image = null;
                }));
            }
            catch { }
        };
    }

    private void UpdateStatusUi(StreamStatus status, string text)
    {
        _statusBadge.Text = text;
        switch (status)
        {
            case StreamStatus.OnAir:
                _statusBadge.BackColor = Color.FromArgb(239, 68, 68);
                _statusBadge.ForeColor = Color.White;
                _btnStartStream.Text = "⏹ STOP";
                _btnStartStream.BackColor = Color.FromArgb(220, 38, 38);
                _btnPreview.Enabled = false;
                _deviceComboBox.Enabled = false;
                _formatComboBox.Enabled = false;
                break;

            case StreamStatus.StandbyPreview:
                _statusBadge.BackColor = Color.FromArgb(245, 158, 11);
                _statusBadge.ForeColor = Color.Black;
                _btnPreview.Text = "⏹ STOP PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(217, 119, 6);
                _btnStartStream.Enabled = true;
                _deviceComboBox.Enabled = true;
                _formatComboBox.Enabled = !FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
                break;

            case StreamStatus.Offline:
            default:
                _statusBadge.BackColor = Color.FromArgb(47, 55, 70);
                _statusBadge.ForeColor = Color.FromArgb(148, 163, 184);
                _btnStartStream.Text = "🔴 STREAM";
                _btnStartStream.BackColor = Color.FromArgb(16, 185, 129);
                _btnPreview.Text = "👁 PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
                _btnStartStream.Enabled = true;
                _btnPreview.Enabled = true;
                _deviceComboBox.Enabled = true;
                _formatComboBox.Enabled = !FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
                break;
        }

        UpdateDestinationButtons();
    }

    private void ToggleDestinationStream(int destIndex)
    {
        if (destIndex < 0 || destIndex >= _config.Destinations.Count) return;
        var dest = _config.Destinations[destIndex];

        if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.LiveStream)
        {
            if (dest.Enabled)
            {
                dest.Enabled = false;
                UpdateDestinationCheckboxes();
                _settings.Save();

                int remaining = _config.Destinations.FindAll(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl)).Count;
                if (remaining == 0)
                {
                    _runner.Stop();
                    UpdateStatusUi(StreamStatus.Offline, "OFFLINE");
                }
                else
                {
                    _runner.Stop();
                    _runner.StartLiveStream(_config);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(dest.FullUrl))
                {
                    MessageBox.Show($"Please enter a valid Stream Key for {dest.Name}.", "Stream Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                dest.Enabled = true;
                UpdateDestinationCheckboxes();
                _settings.Save();
                _runner.Stop();
                _runner.StartLiveStream(_config);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(dest.FullUrl))
            {
                MessageBox.Show($"Please enter a valid Stream Key for {dest.Name}.", "Stream Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            dest.Enabled = true;
            UpdateDestinationCheckboxes();
            _settings.Save();
            _runner.Stop();
            _runner.StartLiveStream(_config);
        }
    }

    private void UpdateDestinationCheckboxes()
    {
        if (_config.Destinations.Count > 0) _chkFb.Checked = _config.Destinations[0].Enabled;
        if (_config.Destinations.Count > 1) _chkYt.Checked = _config.Destinations[1].Enabled;
        if (_config.Destinations.Count > 2) _chkYtNews.Checked = _config.Destinations[2].Enabled;
        UpdateDestinationButtons();
    }

    private void UpdateDestinationButtons()
    {
        bool isLive = _runner.IsRunning && _runner.CurrentMode == RunnerMode.LiveStream;
        UpdateDestBtn(_btnStreamFb, 0, isLive);
        UpdateDestBtn(_btnStreamYt, 1, isLive);
        UpdateDestBtn(_btnStreamYtNews, 2, isLive);
    }

    private void UpdateDestBtn(Button btn, int index, bool isLive)
    {
        if (index >= _config.Destinations.Count) return;
        var dest = _config.Destinations[index];
        if (isLive && dest.Enabled)
        {
            btn.Text = "⏹ STOP";
            btn.BackColor = Color.FromArgb(220, 38, 38);
        }
        else
        {
            btn.Text = "🔴 STREAM";
            btn.BackColor = Color.FromArgb(16, 185, 129);
        }
    }

    private void ToggleStreaming()
    {
        if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.LiveStream)
        {
            _runner.Stop();
            UpdateStatusUi(StreamStatus.Offline, "OFFLINE");
        }
        else
        {
            int activeCount = _config.Destinations.FindAll(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl)).Count;
            if (activeCount == 0)
            {
                MessageBox.Show("Please enable at least one destination (Sahyadri Facebook, YouTube, or YouTube News) with a valid Stream Key.", "Destination Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _runner.Stop();
            _runner.StartLiveStream(_config);
        }
    }

    private void ToggleStandbyPreview()
    {
        if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.StandbyPreview)
        {
            _runner.Stop();
            UpdateStatusUi(StreamStatus.Offline, "OFFLINE");
        }
        else
        {
            _runner.Stop();
            _runner.StartStandbyPreview(_config);
        }
    }

    private void ToggleAudioListen()
    {
        if (_audioMonitor.IsMonitoring)
        {
            _audioMonitor.Stop();
            _btnListen.Text = "🎧 LISTEN";
            _btnListen.BackColor = Color.FromArgb(51, 65, 85);
        }
        else
        {
            bool started = _audioMonitor.Start(_config.DeckLinkDevice, _config.VideoStandardCode);
            if (started)
            {
                _btnListen.Text = "🔊 LISTENING";
                _btnListen.BackColor = Color.FromArgb(14, 165, 233);
            }
        }
    }

    private void TakeSnapshot()
    {
        if (_previewBox.Image == null)
        {
            MessageBox.Show("No active video frame to capture.", "Snapshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var picturesDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = Path.Combine(picturesDir, $"Sahyadri_Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            _previewBox.Image.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
            AppendLog($"[SNAPSHOT] Captured frame to: {path}");
            MessageBox.Show($"Snapshot saved successfully:\n{path}", "Snapshot Captured", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Snapshot failed: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFullscreen()
    {
        if (_fullscreenForm == null || _fullscreenForm.IsDisposed)
        {
            var title = FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice)
                ? $"{_config.DeckLinkDevice} (Loop)"
                : $"{_config.DeckLinkDevice} ({_config.VideoStandardCode})";
            _fullscreenForm = new FullscreenPreviewForm(title);
            _fullscreenForm.FormClosed += (s, e) => _fullscreenForm = null;
            _fullscreenForm.Show();
        }
        else
        {
            _fullscreenForm.BringToFront();
        }
    }

    private void LoadConfigIntoUi()
    {
        _bitrateUpDown.Value = _config.VideoBitrateKbps;
        _chkDeinterlace.Checked = _config.Deinterlace;
        _delayUpDown.Value = _config.AudioDelayMs;

        _encoderCombo.SelectedIndex = _config.VideoEncoder switch
        {
            VideoEncoderType.H264_NVENC => 0,
            VideoEncoderType.LibX264 => 1,
            VideoEncoderType.HEVC_NVENC => 2,
            _ => 0
        };
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _logTextBox.AppendText($"[{stamp}] {message}\r\n");
                if (_logTextBox.TextLength > 50000)
                {
                    _logTextBox.Text = _logTextBox.Text.Substring(10000);
                }
            }));
        }
        catch { }
    }

    private void UpdateCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        ulong idleTicks = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
        ulong kernelTicks = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
        ulong userTicks = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;

        if (_hasCpuSample)
        {
            ulong usrDiff = userTicks - _lastUserTicks;
            ulong kerDiff = kernelTicks - _lastKernelTicks;
            ulong idlDiff = idleTicks - _lastIdleTicks;

            ulong sysDiff = usrDiff + kerDiff;
            if (sysDiff > 0)
            {
                double cpuPercent = Math.Clamp((1.0 - ((double)idlDiff / sysDiff)) * 100.0, 0.0, 100.0);
                if (!IsDisposed)
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            _lblCpu.Text = $"CPU\n{cpuPercent:F0}%";
                            _cpuBadge.Text = $"CPU: {cpuPercent:F0}%";

                            if (cpuPercent > 80)
                            {
                                _cpuBadge.BackColor = Color.FromArgb(220, 38, 38);
                                _cpuBadge.ForeColor = Color.White;
                                _lblCpu.ForeColor = Color.FromArgb(239, 68, 68);
                            }
                            else if (cpuPercent > 50)
                            {
                                _cpuBadge.BackColor = Color.FromArgb(217, 119, 6);
                                _cpuBadge.ForeColor = Color.White;
                                _lblCpu.ForeColor = Color.FromArgb(245, 158, 11);
                            }
                            else
                            {
                                _cpuBadge.BackColor = _settings.DarkMode ? Color.FromArgb(30, 41, 59) : Color.FromArgb(226, 232, 240);
                                _cpuBadge.ForeColor = _settings.DarkMode ? Color.FromArgb(56, 189, 248) : Color.FromArgb(2, 132, 199);
                                _lblCpu.ForeColor = _settings.DarkMode ? Color.FromArgb(203, 213, 225) : Color.FromArgb(30, 41, 59);
                            }
                        }));
                    }
                    catch { }
                }
            }
        }

        _lastIdleTicks = idleTicks;
        _lastKernelTicks = kernelTicks;
        _lastUserTicks = userTicks;
        _hasCpuSample = true;
    }

    private void FormatStatLabel(Label lbl, string header, string initialVal)
    {
        lbl.Text = $"{header}\n{initialVal}";
        lbl.TextAlign = ContentAlignment.MiddleCenter;
        lbl.Font = new Font("Segoe UI", 7f, FontStyle.Regular);
        lbl.Dock = DockStyle.Fill;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        _cpuTimer.Stop();
        _runner.Stop();
        _audioMonitor.Stop();
        _fullscreenForm?.Dispose();

        _settings.Save();
    }
}
