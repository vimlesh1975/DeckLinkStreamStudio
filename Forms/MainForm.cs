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
    private readonly Label _lblPort = new();
    private readonly ComboBox _videoInputComboBox = new();
    private readonly Label _statusBadge = new();
    private readonly Button _btnStartStream = new();
    private readonly Button _btnPreview = new();
    private readonly Button _btnListen = new();
    private readonly Button _btnOpenRecordings = new();

    // Center Workspace Split
    private readonly SplitContainer _mainSplit = new();

    // Left Panel: Compact Video Canvas, HUD & Hardware controls
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
    private readonly ComboBox _encoderCombo = new();
    private readonly NumericUpDown _bitrateUpDown = new();
    private readonly CheckBox _archiveCheckBox = new();
    private readonly TextBox _archiveDirTextBox = new();
    private readonly Button _btnBrowseArchive = new();

    // Right Panel: 3 Streaming Destinations
    private readonly Panel _rightPanel = new();

    // Destination 1: Sahyadri Facebook
    private readonly CheckBox _chkFb = new();
    private readonly TextBox _txtFbUrl = new();
    private readonly TextBox _txtFbKey = new();
    private readonly Button _btnToggleFbKey = new();

    // Destination 2: Sahyadri YouTube
    private readonly CheckBox _chkYt = new();
    private readonly TextBox _txtYtUrl = new();
    private readonly TextBox _txtYtKey = new();
    private readonly Button _btnToggleYtKey = new();

    // Destination 3: Sahyadri YouTube News
    private readonly CheckBox _chkYtNews = new();
    private readonly TextBox _txtYtNewsUrl = new();
    private readonly TextBox _txtYtNewsKey = new();
    private readonly Button _btnToggleYtNewsKey = new();

    // Bottom Diagnostic Console
    private readonly Panel _logPanel = new();
    private readonly Panel _logHeaderPanel = new();
    private readonly Label _lblLogTitle = new();
    private readonly Button _btnToggleLog = new();
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

        _cpuTimer.Tick += (s, e) => UpdateCpuUsage();
        _cpuTimer.Start();

        // Automatically start standby preview on open
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
        Text = "Sahyadri DeckLink Broadcaster (x64 Release) - Multi-Destination Live Streaming";
        Size = new Size(1280, 820);
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 22, 30);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;
    }

    private void InitializeTopHeader()
    {
        _topHeader.Dock = DockStyle.Top;
        _topHeader.Height = 60;
        _topHeader.BackColor = Color.FromArgb(26, 32, 44);
        _topHeader.Padding = new Padding(12, 8, 12, 8);

        _appTitle.Text = "📡 SAHYADRI LIVE";
        _appTitle.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        _appTitle.ForeColor = Color.FromArgb(56, 189, 248);
        _appTitle.AutoSize = true;
        _appTitle.Location = new Point(10, 18);

        int x = 175;

        // 1. DeckLink Card Dropdown
        _lblCard.Text = "CARD:";
        _lblCard.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _lblCard.ForeColor = Color.FromArgb(148, 163, 184);
        _lblCard.AutoSize = true;
        _lblCard.Location = new Point(x, 21);
        x += 46;

        _deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceComboBox.BackColor = Color.FromArgb(42, 50, 68);
        _deviceComboBox.ForeColor = Color.White;
        _deviceComboBox.FlatStyle = FlatStyle.Flat;
        _deviceComboBox.Width = 165;
        _deviceComboBox.Location = new Point(x, 18);

        foreach (var d in _devices) _deviceComboBox.Items.Add(d.Name);
        int devIdx = _deviceComboBox.FindStringExact(_config.DeckLinkDevice);
        _deviceComboBox.SelectedIndex = devIdx >= 0 ? devIdx : 0;
        _deviceComboBox.SelectedIndexChanged += (s, e) =>
        {
            if (_deviceComboBox.SelectedItem != null)
            {
                _config.DeckLinkDevice = _deviceComboBox.SelectedItem.ToString()!;
                _settings.Save();
                if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.StandbyPreview)
                {
                    _runner.Stop();
                    _runner.StartStandbyPreview(_config);
                }
            }
        };
        x += 175;

        // 2. Video Standard Dropdown
        _lblFormat.Text = "STD:";
        _lblFormat.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _lblFormat.ForeColor = Color.FromArgb(148, 163, 184);
        _lblFormat.AutoSize = true;
        _lblFormat.Location = new Point(x, 21);
        x += 38;

        _formatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatComboBox.BackColor = Color.FromArgb(42, 50, 68);
        _formatComboBox.ForeColor = Color.White;
        _formatComboBox.FlatStyle = FlatStyle.Flat;
        _formatComboBox.Width = 145;
        _formatComboBox.Location = new Point(x, 18);

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
        x += 155;

        // 3. Port (SDI/HDMI)
        _lblPort.Text = "PORT:";
        _lblPort.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _lblPort.ForeColor = Color.FromArgb(148, 163, 184);
        _lblPort.AutoSize = true;
        _lblPort.Location = new Point(x, 21);
        x += 44;

        _videoInputComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _videoInputComboBox.BackColor = Color.FromArgb(42, 50, 68);
        _videoInputComboBox.ForeColor = Color.White;
        _videoInputComboBox.FlatStyle = FlatStyle.Flat;
        _videoInputComboBox.Width = 65;
        _videoInputComboBox.Location = new Point(x, 18);
        _videoInputComboBox.Items.Add("sdi");
        _videoInputComboBox.Items.Add("hdmi");
        _videoInputComboBox.SelectedIndex = _config.VideoInput.Equals("hdmi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _videoInputComboBox.SelectedIndexChanged += (s, e) =>
        {
            _config.VideoInput = _videoInputComboBox.SelectedItem?.ToString() ?? "sdi";
            _settings.Save();
            if (_runner.IsRunning && _runner.CurrentMode == RunnerMode.StandbyPreview)
            {
                _runner.Stop();
                _runner.StartStandbyPreview(_config);
            }
        };
        x += 75;

        // Status Badge
        _statusBadge.Text = "OFFLINE";
        _statusBadge.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _statusBadge.ForeColor = Color.FromArgb(148, 163, 184);
        _statusBadge.BackColor = Color.FromArgb(47, 55, 70);
        _statusBadge.Padding = new Padding(8, 4, 8, 4);
        _statusBadge.AutoSize = true;
        _statusBadge.Location = new Point(x, 18);

        // Action Buttons
        _btnStartStream.Text = "🔴 START STREAMING";
        _btnStartStream.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _btnStartStream.BackColor = Color.FromArgb(16, 185, 129);
        _btnStartStream.ForeColor = Color.White;
        _btnStartStream.FlatStyle = FlatStyle.Flat;
        _btnStartStream.FlatAppearance.BorderSize = 0;
        _btnStartStream.Width = 175;
        _btnStartStream.Height = 38;
        _btnStartStream.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnStartStream.Location = new Point(Width - 490, 11);
        _btnStartStream.Click += (s, e) => ToggleStreaming();

        _btnPreview.Text = "👁 PREVIEW";
        _btnPreview.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
        _btnPreview.ForeColor = Color.White;
        _btnPreview.FlatStyle = FlatStyle.Flat;
        _btnPreview.FlatAppearance.BorderSize = 0;
        _btnPreview.Width = 100;
        _btnPreview.Height = 38;
        _btnPreview.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnPreview.Location = new Point(Width - 305, 11);
        _btnPreview.Click += (s, e) => ToggleStandbyPreview();

        _btnListen.Text = "🎧 LISTEN";
        _btnListen.Font = new Font("Segoe UI", 8.5f);
        _btnListen.BackColor = Color.FromArgb(51, 65, 85);
        _btnListen.ForeColor = Color.FromArgb(226, 232, 240);
        _btnListen.FlatStyle = FlatStyle.Flat;
        _btnListen.FlatAppearance.BorderSize = 0;
        _btnListen.Width = 85;
        _btnListen.Height = 38;
        _btnListen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnListen.Location = new Point(Width - 195, 11);
        _btnListen.Click += (s, e) => ToggleAudioListen();

        _btnOpenRecordings.Text = "📁 ARCHIVE";
        _btnOpenRecordings.Font = new Font("Segoe UI", 8.5f);
        _btnOpenRecordings.BackColor = Color.FromArgb(42, 50, 68);
        _btnOpenRecordings.ForeColor = Color.White;
        _btnOpenRecordings.FlatStyle = FlatStyle.Flat;
        _btnOpenRecordings.FlatAppearance.BorderSize = 0;
        _btnOpenRecordings.Width = 90;
        _btnOpenRecordings.Height = 38;
        _btnOpenRecordings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnOpenRecordings.Location = new Point(Width - 100, 11);
        _btnOpenRecordings.Click += (s, e) => OpenArchiveFolder();

        _topHeader.Controls.Add(_appTitle);
        _topHeader.Controls.Add(_lblCard);
        _topHeader.Controls.Add(_deviceComboBox);
        _topHeader.Controls.Add(_lblFormat);
        _topHeader.Controls.Add(_formatComboBox);
        _topHeader.Controls.Add(_lblPort);
        _topHeader.Controls.Add(_videoInputComboBox);
        _topHeader.Controls.Add(_statusBadge);
        _topHeader.Controls.Add(_btnStartStream);
        _topHeader.Controls.Add(_btnPreview);
        _topHeader.Controls.Add(_btnListen);
        _topHeader.Controls.Add(_btnOpenRecordings);

        Controls.Add(_topHeader);
    }

    private void InitializeWorkspace()
    {
        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.BackColor = Color.FromArgb(28, 34, 46);
        _mainSplit.SplitterWidth = 6;
        _mainSplit.SplitterDistance = 490; // Compact width for video preview panel

        InitializeLeftVideoPanel();
        InitializeRightDestinationsPanel();

        Controls.Add(_mainSplit);
    }

    private void InitializeLeftVideoPanel()
    {
        var leftPanel = _mainSplit.Panel1;
        leftPanel.BackColor = Color.FromArgb(16, 20, 28);
        leftPanel.Padding = new Padding(10);
        leftPanel.AutoScroll = true;

        // 1. Compact Video Container (Height 265 for compact 16:9 look)
        _videoContainer.Dock = DockStyle.Top;
        _videoContainer.Height = 265;
        _videoContainer.BackColor = Color.Black;

        _previewBox.Dock = DockStyle.Fill;
        _previewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _previewBox.BackColor = Color.FromArgb(12, 14, 18);
        _previewBox.DoubleClick += (s, e) => OpenFullscreen();

        _standbyWatermark.Text = "STANDBY / NO SIGNAL\n(Left & Right Peak Audio Meters Ready)";
        _standbyWatermark.ForeColor = Color.FromArgb(100, 116, 139);
        _standbyWatermark.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        _standbyWatermark.TextAlign = ContentAlignment.MiddleCenter;
        _standbyWatermark.Dock = DockStyle.Fill;
        _standbyWatermark.BackColor = Color.Transparent;

        _previewBox.Controls.Add(_standbyWatermark);
        _videoContainer.Controls.Add(_previewBox);

        // 2. Toolbar under Video
        _videoToolBar.Dock = DockStyle.Top;
        _videoToolBar.Height = 32;
        _videoToolBar.BackColor = Color.FromArgb(24, 30, 42);
        _videoToolBar.Padding = new Padding(6, 3, 6, 3);

        _chkDeinterlace.Text = "YADIF";
        _chkDeinterlace.ForeColor = Color.FromArgb(203, 213, 225);
        _chkDeinterlace.Font = new Font("Segoe UI", 8.5f);
        _chkDeinterlace.AutoSize = true;
        _chkDeinterlace.Location = new Point(6, 6);
        _chkDeinterlace.Checked = _config.Deinterlace;
        _chkDeinterlace.CheckedChanged += (s, e) =>
        {
            _config.Deinterlace = _chkDeinterlace.Checked;
            _settings.Save();
        };

        _lblAudioDelay.Text = "Delay(ms):";
        _lblAudioDelay.ForeColor = Color.FromArgb(148, 163, 184);
        _lblAudioDelay.Font = new Font("Segoe UI", 8f);
        _lblAudioDelay.AutoSize = true;
        _lblAudioDelay.Location = new Point(78, 8);

        _delayUpDown.BackColor = Color.FromArgb(42, 50, 68);
        _delayUpDown.ForeColor = Color.White;
        _delayUpDown.BorderStyle = BorderStyle.FixedSingle;
        _delayUpDown.Font = new Font("Segoe UI", 8f);
        _delayUpDown.Minimum = 0;
        _delayUpDown.Maximum = 5000;
        _delayUpDown.Value = _config.AudioDelayMs;
        _delayUpDown.Increment = 50;
        _delayUpDown.Width = 60;
        _delayUpDown.Location = new Point(142, 5);
        _delayUpDown.ValueChanged += (s, e) =>
        {
            _config.AudioDelayMs = (int)_delayUpDown.Value;
            _settings.Save();
        };

        _btnSnapshot.Text = "📸 SNAP";
        _btnSnapshot.Font = new Font("Segoe UI", 7.5f);
        _btnSnapshot.BackColor = Color.FromArgb(42, 50, 68);
        _btnSnapshot.ForeColor = Color.White;
        _btnSnapshot.FlatStyle = FlatStyle.Flat;
        _btnSnapshot.FlatAppearance.BorderSize = 0;
        _btnSnapshot.Width = 65;
        _btnSnapshot.Height = 24;
        _btnSnapshot.Location = new Point(210, 4);
        _btnSnapshot.Click += (s, e) => TakeSnapshot();

        _btnFullscreen.Text = "⛶ FULL";
        _btnFullscreen.Font = new Font("Segoe UI", 7.5f);
        _btnFullscreen.BackColor = Color.FromArgb(42, 50, 68);
        _btnFullscreen.ForeColor = Color.White;
        _btnFullscreen.FlatStyle = FlatStyle.Flat;
        _btnFullscreen.FlatAppearance.BorderSize = 0;
        _btnFullscreen.Width = 65;
        _btnFullscreen.Height = 24;
        _btnFullscreen.Location = new Point(282, 4);
        _btnFullscreen.Click += (s, e) => OpenFullscreen();

        _videoToolBar.Controls.Add(_chkDeinterlace);
        _videoToolBar.Controls.Add(_lblAudioDelay);
        _videoToolBar.Controls.Add(_delayUpDown);
        _videoToolBar.Controls.Add(_btnSnapshot);
        _videoToolBar.Controls.Add(_btnFullscreen);

        // 3. Compact Live Broadcast HUD
        _statsTable.Dock = DockStyle.Top;
        _statsTable.Height = 38;
        _statsTable.ColumnCount = 6;
        _statsTable.RowCount = 1;
        _statsTable.BackColor = Color.FromArgb(20, 24, 34);
        _statsTable.Margin = new Padding(0, 4, 0, 4);

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

        // 4. Hardware & Encoder Quick Panel
        _hwSettingsPanel.Dock = DockStyle.Top;
        _hwSettingsPanel.Height = 110;
        _hwSettingsPanel.BackColor = Color.FromArgb(24, 30, 42);
        _hwSettingsPanel.Padding = new Padding(8);
        _hwSettingsPanel.Margin = new Padding(0, 6, 0, 0);

        var lblHwTitle = new Label
        {
            Text = "ENCODER & ARCHIVE SETTINGS",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(8, 6)
        };
        _hwSettingsPanel.Controls.Add(lblHwTitle);

        int hy = 30;
        _hwSettingsPanel.Controls.Add(CreateFormLabel("Encoder / Bitrate:", 8, hy));
        _encoderCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _encoderCombo.BackColor = Color.FromArgb(42, 50, 68);
        _encoderCombo.ForeColor = Color.White;
        _encoderCombo.FlatStyle = FlatStyle.Flat;
        _encoderCombo.Font = new Font("Segoe UI", 8.5f);
        _encoderCombo.Width = 145;
        _encoderCombo.Location = new Point(125, hy - 3);
        _encoderCombo.Items.Add("NVIDIA NVENC (GPU)");
        _encoderCombo.Items.Add("CPU (libx264)");
        _encoderCombo.Items.Add("HEVC NVENC (H.265)");
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

        _bitrateUpDown.BackColor = Color.FromArgb(42, 50, 68);
        _bitrateUpDown.ForeColor = Color.White;
        _bitrateUpDown.BorderStyle = BorderStyle.FixedSingle;
        _bitrateUpDown.Font = new Font("Segoe UI", 8.5f);
        _bitrateUpDown.Minimum = 1000;
        _bitrateUpDown.Maximum = 30000;
        _bitrateUpDown.Increment = 500;
        _bitrateUpDown.Value = _config.VideoBitrateKbps;
        _bitrateUpDown.Width = 70;
        _bitrateUpDown.Location = new Point(278, hy - 2);
        _bitrateUpDown.ValueChanged += (s, e) =>
        {
            _config.VideoBitrateKbps = (int)_bitrateUpDown.Value;
            _settings.Save();
        };
        var lblKbps = new Label { Text = "kbps", ForeColor = Color.FromArgb(148, 163, 184), Font = new Font("Segoe UI", 8.5f), AutoSize = true, Location = new Point(352, hy) };
        _hwSettingsPanel.Controls.Add(_bitrateUpDown);
        _hwSettingsPanel.Controls.Add(lblKbps);

        hy += 34;

        _archiveCheckBox.Text = "Record local .mp4 archive while streaming";
        _archiveCheckBox.ForeColor = Color.FromArgb(52, 211, 153);
        _archiveCheckBox.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _archiveCheckBox.AutoSize = true;
        _archiveCheckBox.Location = new Point(8, hy);
        _archiveCheckBox.Checked = _config.EnableLocalArchive;
        _archiveCheckBox.CheckedChanged += (s, e) =>
        {
            _config.EnableLocalArchive = _archiveCheckBox.Checked;
            _settings.Save();
        };
        _hwSettingsPanel.Controls.Add(_archiveCheckBox);

        leftPanel.Controls.Add(_hwSettingsPanel);
        leftPanel.Controls.Add(_statsTable);
        leftPanel.Controls.Add(_videoToolBar);
        leftPanel.Controls.Add(_videoContainer);
    }

    private void InitializeRightDestinationsPanel()
    {
        var rightPanel = _mainSplit.Panel2;
        rightPanel.BackColor = Color.FromArgb(22, 28, 38);
        rightPanel.Padding = new Padding(12);
        rightPanel.AutoScroll = true;

        int y = 8;

        var lblTitle = new Label
        {
            Text = "3 BROADCAST DESTINATIONS (SIMULTANEOUS)",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            AutoSize = true,
            Location = new Point(8, y)
        };
        rightPanel.Controls.Add(lblTitle);
        y += 30;

        // Destination 1: Sahyadri Facebook Card
        var pnlFb = CreateDestinationCard("Sahyadri Facebook", Color.FromArgb(59, 130, 246), 0, y,
            _chkFb, _txtFbUrl, _txtFbKey, _btnToggleFbKey);
        rightPanel.Controls.Add(pnlFb);
        y += 115;

        // Destination 2: Sahyadri YouTube Card
        var pnlYt = CreateDestinationCard("Sahyadri YouTube", Color.FromArgb(239, 68, 68), 1, y,
            _chkYt, _txtYtUrl, _txtYtKey, _btnToggleYtKey);
        rightPanel.Controls.Add(pnlYt);
        y += 115;

        // Destination 3: Sahyadri YouTube News Card
        var pnlYtNews = CreateDestinationCard("Sahyadri YouTube News", Color.FromArgb(245, 158, 11), 2, y,
            _chkYtNews, _txtYtNewsUrl, _txtYtNewsKey, _btnToggleYtNewsKey);
        rightPanel.Controls.Add(pnlYtNews);
    }

    private Panel CreateDestinationCard(string name, Color accentColor, int destIndex, int y,
        CheckBox chk, TextBox txtUrl, TextBox txtKey, Button btnEye)
    {
        var pnl = new Panel
        {
            Location = new Point(8, y),
            Size = new Size(680, 105),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(28, 34, 46),
            Padding = new Padding(8)
        };

        var accentStrip = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(5, 105),
            BackColor = accentColor
        };
        pnl.Controls.Add(accentStrip);

        chk.Text = name;
        chk.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        chk.ForeColor = Color.White;
        chk.AutoSize = true;
        chk.Location = new Point(14, 8);
        chk.Checked = _config.Destinations[destIndex].Enabled;
        chk.CheckedChanged += (s, e) =>
        {
            _config.Destinations[destIndex].Enabled = chk.Checked;
            _settings.Save();
        };
        pnl.Controls.Add(chk);

        // URL Field
        var lblUrl = new Label { Text = "URL:", ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true, Location = new Point(14, 38) };
        txtUrl.BackColor = Color.FromArgb(42, 50, 68);
        txtUrl.ForeColor = Color.White;
        txtUrl.BorderStyle = BorderStyle.FixedSingle;
        txtUrl.Location = new Point(56, 36);
        txtUrl.Width = 590;
        txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtUrl.Text = _config.Destinations[destIndex].ServerUrl;
        txtUrl.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].ServerUrl = txtUrl.Text;
            _settings.Save();
        };
        pnl.Controls.Add(lblUrl);
        pnl.Controls.Add(txtUrl);

        // Key Field
        var lblKey = new Label { Text = "Key:", ForeColor = Color.FromArgb(148, 163, 184), AutoSize = true, Location = new Point(14, 68) };
        txtKey.BackColor = Color.FromArgb(42, 50, 68);
        txtKey.ForeColor = Color.White;
        txtKey.BorderStyle = BorderStyle.FixedSingle;
        txtKey.UseSystemPasswordChar = true;
        txtKey.Location = new Point(56, 66);
        txtKey.Width = 545;
        txtKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtKey.Text = _config.Destinations[destIndex].StreamKey;
        txtKey.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].StreamKey = txtKey.Text;
            _settings.Save();
        };

        btnEye.Text = "👁";
        btnEye.BackColor = Color.FromArgb(51, 65, 85);
        btnEye.ForeColor = Color.White;
        btnEye.FlatStyle = FlatStyle.Flat;
        btnEye.FlatAppearance.BorderSize = 0;
        btnEye.Width = 38;
        btnEye.Height = txtKey.Height;
        btnEye.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnEye.Location = new Point(608, 66);
        btnEye.Click += (s, e) =>
        {
            txtKey.UseSystemPasswordChar = !txtKey.UseSystemPasswordChar;
        };

        pnl.Controls.Add(lblKey);
        pnl.Controls.Add(txtKey);
        pnl.Controls.Add(btnEye);

        return pnl;
    }

    private void InitializeLogConsole()
    {
        _logPanel.Dock = DockStyle.Bottom;
        _logPanel.Height = 130;
        _logPanel.BackColor = Color.FromArgb(12, 14, 18);

        _logHeaderPanel.Dock = DockStyle.Top;
        _logHeaderPanel.Height = 26;
        _logHeaderPanel.BackColor = Color.FromArgb(24, 28, 38);
        _logHeaderPanel.Padding = new Padding(8, 2, 8, 2);

        _lblLogTitle.Text = "DIAGNOSTICS & FFMPEG BROADCAST CONSOLE";
        _lblLogTitle.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        _lblLogTitle.ForeColor = Color.FromArgb(148, 163, 184);
        _lblLogTitle.AutoSize = true;
        _lblLogTitle.Location = new Point(8, 5);

        _btnClearLog.Text = "CLEAR";
        _btnClearLog.Font = new Font("Segoe UI", 7.5f);
        _btnClearLog.BackColor = Color.FromArgb(42, 50, 68);
        _btnClearLog.ForeColor = Color.White;
        _btnClearLog.FlatStyle = FlatStyle.Flat;
        _btnClearLog.FlatAppearance.BorderSize = 0;
        _btnClearLog.Width = 55;
        _btnClearLog.Height = 20;
        _btnClearLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnClearLog.Location = new Point(Width - 135, 3);
        _btnClearLog.Click += (s, e) => _logTextBox.Clear();

        _btnToggleLog.Text = "HIDE";
        _btnToggleLog.Font = new Font("Segoe UI", 7.5f);
        _btnToggleLog.BackColor = Color.FromArgb(42, 50, 68);
        _btnToggleLog.ForeColor = Color.White;
        _btnToggleLog.FlatStyle = FlatStyle.Flat;
        _btnToggleLog.FlatAppearance.BorderSize = 0;
        _btnToggleLog.Width = 55;
        _btnToggleLog.Height = 20;
        _btnToggleLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnToggleLog.Location = new Point(Width - 72, 3);
        _btnToggleLog.Click += (s, e) =>
        {
            if (_logPanel.Height > 30)
            {
                _logPanel.Height = 26;
                _btnToggleLog.Text = "SHOW";
            }
            else
            {
                _logPanel.Height = 130;
                _btnToggleLog.Text = "HIDE";
            }
        };

        _logHeaderPanel.Controls.Add(_lblLogTitle);
        _logHeaderPanel.Controls.Add(_btnClearLog);
        _logHeaderPanel.Controls.Add(_btnToggleLog);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.BackColor = Color.FromArgb(10, 12, 16);
        _logTextBox.ForeColor = Color.FromArgb(203, 213, 225);
        _logTextBox.Font = new Font("Consolas", 8f);
        _logTextBox.BorderStyle = BorderStyle.None;

        _logPanel.Controls.Add(_logTextBox);
        _logPanel.Controls.Add(_logHeaderPanel);

        Controls.Add(_logPanel);
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
                    _standbyWatermark.Text = $"OFFLINE (Exited code {code})";
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
                _btnStartStream.Text = "⏹ STOP STREAMING";
                _btnStartStream.BackColor = Color.FromArgb(220, 38, 38);
                _btnPreview.Enabled = false;
                _deviceComboBox.Enabled = false;
                _formatComboBox.Enabled = false;
                _videoInputComboBox.Enabled = false;
                break;

            case StreamStatus.StandbyPreview:
                _statusBadge.BackColor = Color.FromArgb(245, 158, 11);
                _statusBadge.ForeColor = Color.Black;
                _btnPreview.Text = "⏹ STOP PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(217, 119, 6);
                _btnStartStream.Enabled = true;
                _deviceComboBox.Enabled = true;
                _formatComboBox.Enabled = true;
                _videoInputComboBox.Enabled = true;
                break;

            case StreamStatus.Offline:
            default:
                _statusBadge.BackColor = Color.FromArgb(47, 55, 70);
                _statusBadge.ForeColor = Color.FromArgb(148, 163, 184);
                _btnStartStream.Text = "🔴 START STREAMING";
                _btnStartStream.BackColor = Color.FromArgb(16, 185, 129);
                _btnPreview.Text = "👁 PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
                _btnStartStream.Enabled = true;
                _btnPreview.Enabled = true;
                _deviceComboBox.Enabled = true;
                _formatComboBox.Enabled = true;
                _videoInputComboBox.Enabled = true;
                break;
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
            if (activeCount == 0 && !_config.EnableLocalArchive)
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
            var dir = _config.ArchiveDirectory;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
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
            _fullscreenForm = new FullscreenPreviewForm($"{_config.DeckLinkDevice} ({_config.VideoStandardCode})");
            _fullscreenForm.FormClosed += (s, e) => _fullscreenForm = null;
            _fullscreenForm.Show();
        }
        else
        {
            _fullscreenForm.BringToFront();
        }
    }

    private void OpenArchiveFolder()
    {
        var dir = _config.ArchiveDirectory;
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    private void LoadConfigIntoUi()
    {
        _bitrateUpDown.Value = _config.VideoBitrateKbps;
        _archiveCheckBox.Checked = _config.EnableLocalArchive;
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
                _lblCpu.Text = $"CPU\n{cpuPercent:F0}%";
                _lblCpu.ForeColor = cpuPercent > 80 ? Color.FromArgb(239, 68, 68) : Color.FromArgb(203, 213, 225);
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
        lbl.Font = new Font("Segoe UI", 7.5f, FontStyle.Regular);
        lbl.ForeColor = Color.FromArgb(148, 163, 184);
        lbl.Dock = DockStyle.Fill;
    }

    private Label CreateFormLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            AutoSize = true,
            Location = new Point(x, y)
        };
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
