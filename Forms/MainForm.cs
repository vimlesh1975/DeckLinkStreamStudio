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
    private readonly Label _cpuBadge = new();
    private readonly Button _btnPreview = new();
    private readonly Button _btnListen = new();
    private readonly CheckBox _chkShowLogs = new();
    private readonly CheckBox _chkDarkMode = new();
    private readonly DestinationStreamRunner[] _destRunners;

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
    private int _isRenderingFrame = 0;
    private readonly SemaphoreSlim _streamActionLock = new(1, 1);

    // Destination 1: Sahyadri Facebook
    private readonly Panel _pnlFb = new();
    private readonly Label _lblFbTitle = new();
    private readonly TextBox _txtFbTitle = new();
    private readonly Label _lblFbUrl = new();
    private readonly TextBox _txtFbUrl = new();
    private readonly Button _btnStreamFb = new();
    private readonly Label _lblFbKey = new();
    private readonly TextBox _txtFbKey = new();
    private readonly Button _btnToggleFbKey = new();

    // Destination 2: Sahyadri YouTube
    private readonly Panel _pnlYt = new();
    private readonly Label _lblYtTitle = new();
    private readonly TextBox _txtYtTitle = new();
    private readonly Label _lblYtUrl = new();
    private readonly TextBox _txtYtUrl = new();
    private readonly Button _btnStreamYt = new();
    private readonly Label _lblYtKey = new();
    private readonly TextBox _txtYtKey = new();
    private readonly Button _btnToggleYtKey = new();

    // Destination 3: Sahyadri YouTube News
    private readonly Panel _pnlYtNews = new();
    private readonly Label _lblYtNewsTitle = new();
    private readonly TextBox _txtYtNewsTitle = new();
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
    private int _baseFormHeight = 800;

    // CPU Timer
    private readonly System.Windows.Forms.Timer _cpuTimer = new() { Interval = 1000 };

    // Destination Cooldown Timer (e.g. 10s cooldown on Facebook to allow clean session teardown)
    private readonly int[] _destCooldown = new int[3];
    private readonly System.Windows.Forms.Timer _destCooldownTimer = new() { Interval = 1000 };

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

        // Write session-start marker to log file
        try
        {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "streaming.log"),
                $"\r\n{'='.ToString().PadRight(80, '=')}\r\n" +
                $"SESSION START: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Device: {_config.DeckLinkDevice}  Format: {_config.VideoStandardCode}\r\n" +
                $"{'='.ToString().PadRight(80, '=')}\r\n");
        }
        catch { }

        _destRunners = new DestinationStreamRunner[3]
        {
            new DestinationStreamRunner(0, _config.Destinations.Count > 0 && !string.IsNullOrWhiteSpace(_config.Destinations[0].Name) ? _config.Destinations[0].Name : "Sahyadri Facebook"),
            new DestinationStreamRunner(1, _config.Destinations.Count > 1 && !string.IsNullOrWhiteSpace(_config.Destinations[1].Name) ? _config.Destinations[1].Name : "Sahyadri YouTube"),
            new DestinationStreamRunner(2, _config.Destinations.Count > 2 && !string.IsNullOrWhiteSpace(_config.Destinations[2].Name) ? _config.Destinations[2].Name : "Sahyadri YouTube News")
        };

        for (int i = 0; i < _destRunners.Length; i++)
        {
            var r = _destRunners[i];
            r.OnStatsUpdated += (runner, stats) =>
            {
                if (IsDisposed) return;
                try { BeginInvoke(new Action(UpdateOverallStats)); } catch { }
            };
            r.OnProcessExited += (runner, code) =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        if (runner.DestinationIndex < _config.Destinations.Count)
                        {
                            var d = _config.Destinations[runner.DestinationIndex];
                            d.Enabled = false;
                            _settings.Save();
                            StartCooldown(runner.DestinationIndex, d.Name.Contains("Facebook", StringComparison.OrdinalIgnoreCase) ? 70 : 3);
                        }
                        UpdateDestinationButtons();
                        UpdateOverallStreamStatus();
                    }));
                }
                catch { }
            };
            r.OnLog += msg => AppendLog(msg);
        }

        InitializeForm();
        InitializeTopHeader();
        InitializeWorkspace();
        InitializeLogConsole();
        HookRunnerEvents();
        LoadConfigIntoUi();

        // Ensure all broadcast destinations start disabled on application launch
        foreach (var d in _config.Destinations)
        {
            d.Enabled = false;
        }
        UpdateDestinationButtons();

        // Ensure proper WinForms docking order: _leftPanel (DockStyle.Fill) must be at front
        // so _topHeader (DockStyle.Top) does not overlap or cut off the top of the video preview.
        _leftPanel.BringToFront();

        ApplyTheme(_settings.DarkMode);

        _cpuTimer.Tick += (s, e) => UpdateCpuUsage();
        _cpuTimer.Start();
        UpdateCpuUsage();

        _destCooldownTimer.Tick += (s, e) => OnDestCooldownTick();

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
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Size = new Size(720, 800);
        MinimumSize = new Size(720, 800);
        MaximumSize = new Size(720, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        Icon = SystemIcons.Application;
        _baseFormHeight = Height;
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
        _deviceComboBox.DropDownWidth = 180;
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

                if (_runner.IsRunning && GetActiveStreamCount() == 0)
                {
                    RestartStandbyPreviewAsync();
                }

                if (_audioMonitor.IsMonitoring)
                {
                    _audioMonitor.Start(_config.DeckLinkDevice, _config.VideoStandardCode);
                    AppendLog($"[STARTED] Audio Monitoring ({_config.DeckLinkDevice})");
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
                if (_runner.IsRunning && GetActiveStreamCount() == 0)
                {
                    RestartStandbyPreviewAsync();
                }
            }
        };

        bool isFileInitial = FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
        _formatComboBox.Enabled = !isFileInitial;

        // CPU Usage Badge (Big, prominent font)
        _cpuBadge.Text = "CPU: 0%";
        _cpuBadge.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        _cpuBadge.Padding = new Padding(8, 2, 8, 2);
        _cpuBadge.AutoSize = true;
        _cpuBadge.BackColor = Color.FromArgb(30, 41, 59);
        _cpuBadge.ForeColor = Color.FromArgb(56, 189, 248);
        UpdateHeaderBadgePositions();

        // Row 2: Action Buttons & Options (No master button, Preview and Listen on left)
        _btnPreview.Text = "👁 PREVIEW";
        _btnPreview.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
        _btnPreview.ForeColor = Color.White;
        _btnPreview.FlatStyle = FlatStyle.Flat;
        _btnPreview.FlatAppearance.BorderSize = 0;
        _btnPreview.Width = 135;
        _btnPreview.Height = 26;
        _btnPreview.Location = new Point(8, 36);
        _btnPreview.Click += (s, e) => ToggleStandbyPreview();

        _btnListen.Text = "🎧 LISTEN";
        _btnListen.Font = new Font("Segoe UI", 8.5f);
        _btnListen.BackColor = Color.FromArgb(51, 65, 85);
        _btnListen.ForeColor = Color.FromArgb(226, 232, 240);
        _btnListen.FlatStyle = FlatStyle.Flat;
        _btnListen.FlatAppearance.BorderSize = 0;
        _btnListen.Width = 85;
        _btnListen.Height = 26;
        _btnListen.Location = new Point(148, 36);
        _btnListen.Click += (s, e) => ToggleAudioListen();

        // Logs checkbox
        _chkShowLogs.Text = "Logs";
        _chkShowLogs.Font = new Font("Segoe UI", 8f);
        _chkShowLogs.AutoSize = true;
        _chkShowLogs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _chkShowLogs.Checked = false;
        _chkShowLogs.CheckedChanged += (s, e) =>
        {
            ToggleLogConsoleVisibility(_chkShowLogs.Checked);
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
        _topHeader.Controls.Add(_cpuBadge);
        _topHeader.Controls.Add(_btnPreview);
        _topHeader.Controls.Add(_btnListen);
        _topHeader.Controls.Add(_chkShowLogs);
        _topHeader.Controls.Add(_chkDarkMode);

        _topHeader.Resize += (s, e) => LayoutTopHeaderRightControls();
        LayoutTopHeaderRightControls();

        Controls.Add(_topHeader);
    }

    private void UpdateHeaderBadgePositions()
    {
        _cpuBadge.Location = new Point(_formatComboBox.Right + 16, 5);
    }

    private void LayoutTopHeaderRightControls()
    {
        UpdateHeaderBadgePositions();
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
        _statsTable.ColumnCount = 5;
        _statsTable.RowCount = 1;
        _statsTable.Margin = new Padding(0, 3, 0, 3);

        for (int i = 0; i < 5; i++)
            _statsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20.0f));

        FormatStatLabel(_lblDuration, "TIME", "00:00:00");
        FormatStatLabel(_lblBitrate, "BITRATE", "0 kbps");
        FormatStatLabel(_lblFps, "FPS", "0.0");
        FormatStatLabel(_lblDropped, "DROPS", "0");
        FormatStatLabel(_lblSpeed, "SPEED", "1.00x");

        _statsTable.Controls.Add(_lblDuration, 0, 0);
        _statsTable.Controls.Add(_lblBitrate, 1, 0);
        _statsTable.Controls.Add(_lblFps, 2, 0);
        _statsTable.Controls.Add(_lblDropped, 3, 0);
        _statsTable.Controls.Add(_lblSpeed, 4, 0);

        // 4. Encoder Quick Panel (Compact single-line)
        _hwSettingsPanel.Dock = DockStyle.Top;
        _hwSettingsPanel.Height = 36;
        _hwSettingsPanel.Padding = new Padding(6, 2, 6, 2);
        _hwSettingsPanel.Margin = new Padding(0, 2, 0, 0);

        _lblHwTitle.Text = "YT ENCODER:";
        _lblHwTitle.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _lblHwTitle.AutoSize = true;
        _lblHwTitle.Location = new Point(8, 10);
        _hwSettingsPanel.Controls.Add(_lblHwTitle);

        _encoderCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _encoderCombo.FlatStyle = FlatStyle.Flat;
        _encoderCombo.Font = new Font("Segoe UI", 8.5f);
        _encoderCombo.Width = 155;
        _encoderCombo.Location = new Point(94, 6);
        _encoderCombo.Items.Add("NVIDIA HEVC (H.265)");
        _encoderCombo.Items.Add("NVIDIA H.264 (NVENC)");
        _encoderCombo.Items.Add("AMD GPU (AMF)");
        _encoderCombo.Items.Add("CPU (libx264)");
        _encoderCombo.Items.Add("Auto (GPU/CPU)");
        _encoderCombo.SelectedIndex = _config.VideoEncoder switch
        {
            VideoEncoderType.HEVC_NVENC => 0,
            VideoEncoderType.H264_NVENC => 1,
            VideoEncoderType.H264_AMF => 2,
            VideoEncoderType.LibX264 => 3,
            VideoEncoderType.Auto => 4,
            _ => 3
        };
        _encoderCombo.SelectedIndexChanged += (s, e) =>
        {
            _config.VideoEncoder = _encoderCombo.SelectedIndex switch
            {
                0 => VideoEncoderType.HEVC_NVENC,
                1 => VideoEncoderType.H264_NVENC,
                2 => VideoEncoderType.H264_AMF,
                3 => VideoEncoderType.LibX264,
                4 => VideoEncoderType.Auto,
                _ => VideoEncoderType.HEVC_NVENC
            };
            _settings.Save();
            if (_runner.IsRunning && GetActiveStreamCount() == 0)
            {
                RestartStandbyPreviewAsync();
            }
        };
        _hwSettingsPanel.Controls.Add(_encoderCombo);

        _lblEncoderTitle.Text = "Bitrate:";
        _lblEncoderTitle.Font = new Font("Segoe UI", 8f);
        _lblEncoderTitle.AutoSize = true;
        _lblEncoderTitle.Location = new Point(258, 10);
        _hwSettingsPanel.Controls.Add(_lblEncoderTitle);

        _bitrateUpDown.BorderStyle = BorderStyle.FixedSingle;
        _bitrateUpDown.Font = new Font("Segoe UI", 8.5f);
        _bitrateUpDown.Minimum = 1000;
        _bitrateUpDown.Maximum = 30000;
        _bitrateUpDown.Increment = 500;
        _bitrateUpDown.Value = _config.VideoBitrateKbps;
        _bitrateUpDown.Width = 70;
        _bitrateUpDown.Location = new Point(306, 7);
        _bitrateUpDown.ValueChanged += (s, e) =>
        {
            _config.VideoBitrateKbps = (int)_bitrateUpDown.Value;
            _settings.Save();
            if (_runner.IsRunning && GetActiveStreamCount() == 0)
            {
                RestartStandbyPreviewAsync();
            }
        };
        _hwSettingsPanel.Controls.Add(_bitrateUpDown);

        _lblKbps.Text = "kbps";
        _lblKbps.Font = new Font("Segoe UI", 8f);
        _lblKbps.AutoSize = true;
        _lblKbps.Location = new Point(380, 10);
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
        BuildDestinationCard(_pnlFb, Color.FromArgb(59, 130, 246), 0, y,
            _lblFbTitle, _txtFbTitle, _lblFbUrl, _txtFbUrl, _btnStreamFb, _lblFbKey, _txtFbKey, _btnToggleFbKey);
        _rightPanel.Controls.Add(_pnlFb);
        y += 94;

        // Destination 2: Sahyadri YouTube Card
        BuildDestinationCard(_pnlYt, Color.FromArgb(239, 68, 68), 1, y,
            _lblYtTitle, _txtYtTitle, _lblYtUrl, _txtYtUrl, _btnStreamYt, _lblYtKey, _txtYtKey, _btnToggleYtKey);
        _rightPanel.Controls.Add(_pnlYt);
        y += 94;

        // Destination 3: Sahyadri YouTube News Card
        BuildDestinationCard(_pnlYtNews, Color.FromArgb(245, 158, 11), 2, y,
            _lblYtNewsTitle, _txtYtNewsTitle, _lblYtNewsUrl, _txtYtNewsUrl, _btnStreamYtNews, _lblYtNewsKey, _txtYtNewsKey, _btnToggleYtNewsKey);
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

    private void BuildDestinationCard(Panel pnl, Color accentColor, int destIndex, int y,
        Label lblTitle, TextBox txtTitle, Label lblUrl, TextBox txtUrl, Button btnStream, Label lblKey, TextBox txtKey, Button btnEye)
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

        // Stream Name Field - locked by default, double-click to unlock for editing
        lblTitle.Text = "🔒 Stream:";
        lblTitle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        lblTitle.AutoSize = true;
        var titleToolTip = new ToolTip();
        titleToolTip.SetToolTip(lblTitle, "Double-click to unlock and rename stream destination");
        titleToolTip.SetToolTip(txtTitle, "Double-click to unlock and rename stream destination");

        txtTitle.BorderStyle = BorderStyle.FixedSingle;
        txtTitle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        txtTitle.ReadOnly = true;
        txtTitle.Cursor = Cursors.Arrow;
        txtTitle.Text = _config.Destinations[destIndex].Name;
        txtTitle.Select(0, 0);
        txtTitle.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].Name = txtTitle.Text;
            if (_destRunners != null && destIndex < _destRunners.Length && _destRunners[destIndex] != null)
            {
                _destRunners[destIndex].DestinationName = txtTitle.Text;
            }
            _settings.Save();
        };

        Action unlockTitleEdit = () =>
        {
            txtTitle.ReadOnly = false;
            txtTitle.Cursor = Cursors.IBeam;
            txtTitle.BackColor = _settings.DarkMode ? Color.FromArgb(55, 65, 85) : Color.FromArgb(255, 255, 240);
            lblTitle.Text = "✏️ Stream:";
            txtTitle.Focus();
            txtTitle.SelectAll();
        };

        lblTitle.DoubleClick += (s, e) => unlockTitleEdit();
        txtTitle.DoubleClick += (s, e) => unlockTitleEdit();

        txtTitle.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
            {
                pnl.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        txtTitle.Leave += (s, e) =>
        {
            txtTitle.ReadOnly = true;
            txtTitle.Cursor = Cursors.Arrow;
            lblTitle.Text = "🔒 Stream:";
            txtTitle.BackColor = _settings.DarkMode ? Color.FromArgb(42, 50, 68) : Color.FromArgb(241, 245, 249);
        };
        pnl.Controls.Add(lblTitle);
        pnl.Controls.Add(txtTitle);

        // URL Field - locked by default, double-click to unlock for editing
        lblUrl.Text = "🔒 URL:";
        lblUrl.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        lblUrl.AutoSize = true;
        var urlToolTip = new ToolTip();
        urlToolTip.SetToolTip(lblUrl, "Double-click URL field to unlock for editing");
        urlToolTip.SetToolTip(txtUrl, "Double-click to unlock for editing");

        txtUrl.BorderStyle = BorderStyle.FixedSingle;
        txtUrl.Font = new Font("Segoe UI", 9f);
        txtUrl.ReadOnly = true;
        txtUrl.Cursor = Cursors.Arrow;
        txtUrl.Text = _config.Destinations[destIndex].ServerUrl;
        txtUrl.Select(0, 0);
        txtUrl.TextChanged += (s, e) =>
        {
            _config.Destinations[destIndex].ServerUrl = txtUrl.Text;
            _settings.Save();
        };

        Action unlockUrlEdit = () =>
        {
            txtUrl.ReadOnly = false;
            txtUrl.Cursor = Cursors.IBeam;
            txtUrl.BackColor = _settings.DarkMode ? Color.FromArgb(55, 65, 85) : Color.FromArgb(255, 255, 240);
            lblUrl.Text = "✏️ URL:";
            txtUrl.Focus();
            txtUrl.SelectAll();
        };

        lblUrl.DoubleClick += (s, e) => unlockUrlEdit();
        txtUrl.DoubleClick += (s, e) => unlockUrlEdit();

        txtUrl.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)
            {
                pnl.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        txtUrl.Leave += (s, e) =>
        {
            txtUrl.ReadOnly = true;
            txtUrl.Cursor = Cursors.Arrow;
            lblUrl.Text = "🔒 URL:";
            txtUrl.BackColor = _settings.DarkMode ? Color.FromArgb(42, 50, 68) : Color.FromArgb(241, 245, 249);
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
        btnStream.Padding = Padding.Empty;
        btnStream.Height = 25;
        btnStream.Click += (s, e) => ToggleDestinationStream(destIndex);
        pnl.Controls.Add(btnStream);

        // Key Field
        lblKey.Text = "Key:";
        lblKey.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        lblKey.AutoSize = true;

        txtKey.BorderStyle = BorderStyle.FixedSingle;
        txtKey.Font = new Font("Segoe UI", 9f);
        txtKey.UseSystemPasswordChar = true;
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
        btnEye.Padding = Padding.Empty;
        btnEye.Height = 24;
        btnEye.Click += (s, e) =>
        {
            txtKey.UseSystemPasswordChar = !txtKey.UseSystemPasswordChar;
        };

        pnl.Controls.Add(lblKey);
        pnl.Controls.Add(txtKey);
        pnl.Controls.Add(btnEye);

        // Responsive layout: 75% width for value boxes, horizontal alignment, and left margin
        const int leftMargin = 14;
        const int boxX = 82;

        Action layoutCard = () =>
        {
            int w = pnl.ClientSize.Width;
            if (w <= 0) return;

            int boxWidth = Math.Max(80, (int)(w * 0.75));
            int btnX = boxX + boxWidth + 6;
            int btnWidth = Math.Max(36, w - 6 - btnX);

            // Row 1: Stream Name
            lblTitle.Location = new Point(leftMargin, 8);
            txtTitle.Location = new Point(boxX, 5);
            txtTitle.Width = boxWidth;

            // Row 2: URL + STREAM button
            lblUrl.Location = new Point(leftMargin, 34);
            txtUrl.Location = new Point(boxX, 31);
            txtUrl.Width = boxWidth;

            btnStream.Location = new Point(btnX, 30);
            btnStream.Width = btnWidth;

            // Row 3: Key + Eye button
            lblKey.Location = new Point(leftMargin, 61);
            txtKey.Location = new Point(boxX, 58);
            txtKey.Width = boxWidth;

            btnEye.Location = new Point(btnX, 57);
            btnEye.Width = btnWidth;
        };

        pnl.Resize += (s, e) => layoutCard();
        layoutCard();
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

        _logHeaderPanel.Resize += (s, e) =>
        {
            if (_logHeaderPanel.ClientSize.Width > 0)
            {
                _btnClearLog.Location = new Point(_logHeaderPanel.ClientSize.Width - _btnClearLog.Width - 10, 3);
            }
        };

        Controls.Add(_logPanel);
    }

    private void ToggleLogConsoleVisibility(bool showLogs)
    {
        SuspendLayout();
        int currentWidth = Width;
        int logHeight = _logPanel.Height > 0 ? _logPanel.Height : 120;

        if (showLogs)
        {
            int targetHeight = _baseFormHeight + logHeight;
            MaximumSize = new Size(currentWidth, targetHeight);
            MinimumSize = new Size(currentWidth, targetHeight);
            Size = new Size(currentWidth, targetHeight);
            _logPanel.Visible = true;

            var workingArea = Screen.FromControl(this).WorkingArea;
            if (Bottom > workingArea.Bottom)
            {
                Top = Math.Max(workingArea.Top, workingArea.Bottom - Height);
            }
        }
        else
        {
            _logPanel.Visible = false;
            MinimumSize = new Size(currentWidth, _baseFormHeight);
            Size = new Size(currentWidth, _baseFormHeight);
            MaximumSize = new Size(currentWidth, _baseFormHeight);

            var workingArea = Screen.FromControl(this).WorkingArea;
            if (Top < workingArea.Top)
            {
                Top = workingArea.Top;
            }
        }
        ResumeLayout(true);
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
        ApplyCardTheme(_pnlFb, _lblFbTitle, _txtFbTitle, _lblFbUrl, _txtFbUrl, _lblFbKey, _txtFbKey, _btnToggleFbKey, bgCard, bgControl, textMain, textSec);
        ApplyCardTheme(_pnlYt, _lblYtTitle, _txtYtTitle, _lblYtUrl, _txtYtUrl, _lblYtKey, _txtYtKey, _btnToggleYtKey, bgCard, bgControl, textMain, textSec);
        ApplyCardTheme(_pnlYtNews, _lblYtNewsTitle, _txtYtNewsTitle, _lblYtNewsUrl, _txtYtNewsUrl, _lblYtNewsKey, _txtYtNewsKey, _btnToggleYtNewsKey, bgCard, bgControl, textMain, textSec);

        // Log Console
        _logPanel.BackColor = isDark ? Color.FromArgb(12, 14, 18) : Color.FromArgb(241, 245, 249);
        _logHeaderPanel.BackColor = isDark ? Color.FromArgb(24, 28, 38) : Color.FromArgb(226, 232, 240);
        _lblLogTitle.ForeColor = textSec;
        _btnClearLog.BackColor = bgControl;
        _btnClearLog.ForeColor = textMain;
        _logTextBox.BackColor = isDark ? Color.FromArgb(10, 12, 16) : Color.FromArgb(255, 255, 255);
        _logTextBox.ForeColor = isDark ? Color.FromArgb(203, 213, 225) : Color.FromArgb(15, 23, 42);
    }

    private void ApplyCardTheme(Panel pnl, Label lblTitle, TextBox txtTitle, Label lUrl, TextBox tUrl, Label lKey, TextBox tKey, Button bEye,
        Color bgCard, Color bgControl, Color textMain, Color textSec)
    {
        pnl.BackColor = bgCard;
        lblTitle.ForeColor = textSec;
        if (txtTitle.ReadOnly)
            txtTitle.BackColor = bgControl;
        txtTitle.ForeColor = textMain;

        lUrl.ForeColor = textSec; // dimmed to hint it's locked
        // Only re-apply background if still read-only (don't override active-edit colour)
        if (tUrl.ReadOnly)
            tUrl.BackColor = bgControl;
        tUrl.ForeColor = tUrl.ReadOnly ? textSec : textMain;

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
            if (IsDisposed)
            {
                frame.Dispose();
                return;
            }

            // Drop frame if UI is still rendering previous frame - prevents UI queue lag & GDI memory leak
            if (Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) != 0)
            {
                frame.Dispose();
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed) return;
                        _standbyWatermark.Visible = false;
                        var old = _previewBox.Image;
                        _previewBox.Image = frame;
                        old?.Dispose();

                        _fullscreenForm?.UpdateFrame(frame);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isRenderingFrame, 0);
                    }
                }));
            }
            catch
            {
                Interlocked.Exchange(ref _isRenderingFrame, 0);
                frame.Dispose();
            }
        };

        _runner.OnStatsUpdated += stats =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    if (GetActiveStreamCount() == 0)
                    {
                        UpdateOverallStats();
                    }
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
                    UpdateOverallStreamStatus();
                }));
            }
            catch { }
        };

        _runner.OnLog += msg => AppendLog(msg);

        _runner.OnProcessExited += (code, mode) =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    UpdateOverallStreamStatus();
                    _standbyWatermark.Visible = true;
                    _previewBox.Image?.Dispose();
                    _previewBox.Image = null;

                    if (mode == RunnerMode.LiveStream && !FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice))
                    {
                        // DeckLink live stream unexpectedly crashed/ended
                        int activeCount = _config.Destinations.Count(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl));
                        if (_settings.AutoReconnect && activeCount > 0)
                        {
                            _standbyWatermark.Text = $"STREAM RECONNECTING (Code {code}) — Retrying in 2s...";
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(2000);
                                if (!IsDisposed && _runner != null && !_runner.IsRunning)
                                {
                                    int count = _config.Destinations.Count(d => d.Enabled && !string.IsNullOrWhiteSpace(d.FullUrl));
                                    if (count > 0)
                                    {
                                        _runner.StartLiveStream(_config);
                                    }
                                }
                            });
                        }
                        else
                        {
                            _standbyWatermark.Text = $"STREAM ENDED (Code {code}) — Returning to Preview";
                            foreach (var d in _config.Destinations) d.Enabled = false;
                            _settings.Save();
                            UpdateDestinationButtons();
                            _runner.StartStandbyPreview(_config);
                            UpdateOverallStreamStatus();
                        }
                    }
                    else if (mode == RunnerMode.StandbyPreview)
                    {
                        _standbyWatermark.Text = $"PREVIEW STOPPED (Code {code})";
                    }
                    else
                    {
                        _standbyWatermark.Text = $"OFFLINE (Code {code})";
                    }
                }));
            }
            catch { }
        };
    }

    private int GetActiveStreamCount()
    {
        if (_destRunners == null) return 0;
        int count = 0;
        for (int i = 0; i < _destRunners.Length; i++)
        {
            if (_destRunners[i].IsRunning) count++;
        }
        return count;
    }

    private void UpdateOverallStreamStatus()
    {
        int activeCount = GetActiveStreamCount();
        bool isOnAir = activeCount > 0;

        if (isOnAir)
        {
            _btnPreview.Enabled = false;
            _deviceComboBox.Enabled = false;
            _formatComboBox.Enabled = false;
            _encoderCombo.Enabled = false;
            _bitrateUpDown.Enabled = false;
        }
        else
        {
            _btnPreview.Enabled = true;
            _deviceComboBox.Enabled = true;
            _formatComboBox.Enabled = !FfmpegStreamRunner.IsFileSource(_config.DeckLinkDevice);
            _encoderCombo.Enabled = true;
            _bitrateUpDown.Enabled = true;

            if (_runner.IsRunning)
            {
                _btnPreview.Text = "⏹ STOP PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(217, 119, 6);
            }
            else
            {
                _btnPreview.Text = "👁 PREVIEW";
                _btnPreview.BackColor = Color.FromArgb(37, 99, 235);
            }
        }

        UpdateHeaderBadgePositions();
        UpdateDestinationButtons();
        UpdateOverallStats();
    }

    private void UpdateOverallStats()
    {
        int activeCount = 0;
        double totalBitrate = 0;
        double maxFps = 0;
        long totalDrops = 0;
        double avgSpeed = 1.0;
        TimeSpan maxDuration = TimeSpan.Zero;

        if (_destRunners != null)
        {
            for (int i = 0; i < _destRunners.Length; i++)
            {
                var r = _destRunners[i];
                if (r.IsRunning)
                {
                    activeCount++;
                    totalBitrate += r.CurrentStats.CurrentBitrateKbps;
                    if (r.CurrentStats.CurrentFps > maxFps) maxFps = r.CurrentStats.CurrentFps;
                    totalDrops += r.CurrentStats.DroppedFrames;
                    avgSpeed = r.CurrentStats.SpeedRatio;
                    if (r.CurrentStats.Duration > maxDuration) maxDuration = r.CurrentStats.Duration;
                }
            }
        }

        if (activeCount > 0)
        {
            _lblDuration.Text = $"TIME\n{maxDuration:hh\\:mm\\:ss}";
            _lblBitrate.Text = activeCount > 1
                ? $"BITRATE ({activeCount}x)\n{totalBitrate:F0} kbps"
                : $"BITRATE\n{totalBitrate:F0} kbps";
            _lblFps.Text = $"FPS\n{maxFps:F1}";
            _lblDropped.Text = $"DROPS\n{totalDrops}";
            _lblSpeed.Text = $"SPEED\n{avgSpeed:F2}x";
        }
        else if (_runner.IsRunning)
        {
            _lblDuration.Text = $"TIME\n{_runner.CurrentStats.Duration:hh\\:mm\\:ss}";
            _lblBitrate.Text = "BITRATE\nSTANDBY";
            _lblFps.Text = $"FPS\n{_runner.CurrentStats.CurrentFps:F1}";
            _lblDropped.Text = $"DROPS\n{_runner.CurrentStats.DroppedFrames}";
            _lblSpeed.Text = $"SPEED\n{_runner.CurrentStats.SpeedRatio:F2}x";
        }
        else
        {
            _lblDuration.Text = "TIME\n00:00:00";
            _lblBitrate.Text = "BITRATE\n0 kbps";
            _lblFps.Text = "FPS\n0.0";
            _lblDropped.Text = "DROPS\n0";
            _lblSpeed.Text = "SPEED\n1.00x";
        }
    }

    private async void ToggleDestinationStream(int destIndex)
    {
        if (_destRunners == null || destIndex < 0 || destIndex >= _destRunners.Length) return;
        if (destIndex >= _config.Destinations.Count) return;
        if (_destCooldown[destIndex] > 0) return;

        var dest = _config.Destinations[destIndex];
        var runner = _destRunners[destIndex];

        if (!runner.IsRunning)
        {
            if (string.IsNullOrWhiteSpace(dest.StreamKey) || string.IsNullOrWhiteSpace(dest.FullUrl))
            {
                MessageBox.Show($"Please enter a valid Stream Key for {dest.Name}.", "Stream Key Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        // Provide immediate visual feedback on the button without blocking the UI
        var btn = GetDestButton(destIndex);
        if (btn != null)
        {
            btn.Text = runner.IsRunning ? "⏳ STOPPING..." : "⏳ STARTING...";
            btn.Enabled = false;
        }

        await _streamActionLock.WaitAsync();
        try
        {
            if (runner.IsRunning)
            {
                await Task.Run(() => runner.Stop());
                dest.Enabled = false;
                _settings.Save();
                StartCooldown(destIndex, dest.Name.Contains("Facebook", StringComparison.OrdinalIgnoreCase) ? 70 : 3);
            }
            else
            {
                // Ensure master preview and broadcast hub is running
                if (!_runner.IsRunning)
                {
                    await Task.Run(() => _runner.StartStandbyPreview(_config));
                    await Task.Delay(800);
                }

                dest.Enabled = true;
                _settings.Save();
                await Task.Run(() => runner.Start(_config, dest));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] [{dest.Name}] {ex.Message}");
        }
        finally
        {
            if (btn != null)
            {
                btn.Enabled = _destCooldown[destIndex] <= 0;
            }
            _streamActionLock.Release();
            UpdateDestinationButtons();
            UpdateOverallStreamStatus();
        }
    }

    private Button? GetDestButton(int index) => index switch
    {
        0 => _btnStreamFb,
        1 => _btnStreamYt,
        2 => _btnStreamYtNews,
        _ => null
    };

    private void StartCooldown(int destIndex, int seconds)
    {
        if (destIndex < 0 || destIndex >= _destCooldown.Length) return;
        if (seconds <= 0) return;
        _destCooldown[destIndex] = seconds;
        _destCooldownTimer.Start();
        var btn = GetDestButton(destIndex);
        if (btn != null)
        {
            UpdateDestBtn(btn, destIndex);
        }
    }

    private void OnDestCooldownTick()
    {
        if (IsDisposed) return;
        bool anyActive = false;
        for (int i = 0; i < _destCooldown.Length; i++)
        {
            if (_destCooldown[i] > 0)
            {
                _destCooldown[i]--;
                var btn = GetDestButton(i);
                if (btn != null)
                {
                    UpdateDestBtn(btn, i);
                }
                if (_destCooldown[i] > 0)
                {
                    anyActive = true;
                }
            }
        }

        if (!anyActive)
        {
            _destCooldownTimer.Stop();
        }
    }

    private void UpdateDestinationButtons()
    {
        UpdateDestBtn(_btnStreamFb, 0);
        UpdateDestBtn(_btnStreamYt, 1);
        UpdateDestBtn(_btnStreamYtNews, 2);
    }

    private void UpdateDestBtn(Button btn, int index)
    {
        if (index >= _config.Destinations.Count) return;
        if (_destRunners == null || index >= _destRunners.Length) return;

        if (_destCooldown[index] > 0)
        {
            btn.Text = $"⏳ WAIT {_destCooldown[index]}s";
            btn.BackColor = Color.FromArgb(100, 116, 139);
            btn.Enabled = false;
            return;
        }

        bool isActive = _destRunners[index].IsRunning;
        btn.Enabled = true;

        if (isActive)
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

    private async void ToggleStandbyPreview()
    {
        _btnPreview.Enabled = false;
        await _streamActionLock.WaitAsync();
        try
        {
            if (_runner.IsRunning)
            {
                if (GetActiveStreamCount() > 0)
                {
                    MessageBox.Show("Cannot stop preview while streaming is active.", "Streaming Active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                await Task.Run(() => _runner.Stop());
            }
            else
            {
                await Task.Run(() => _runner.StartStandbyPreview(_config));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] Preview: {ex.Message}");
        }
        finally
        {
            _btnPreview.Enabled = true;
            _streamActionLock.Release();
            UpdateOverallStreamStatus();
        }
    }

    private async void RestartStandbyPreviewAsync()
    {
        await _streamActionLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                _runner.Stop();
                _runner.StartStandbyPreview(_config);
            });
        }
        catch { }
        finally
        {
            _streamActionLock.Release();
            UpdateOverallStreamStatus();
        }
    }

    private void ToggleAudioListen()
    {
        if (_audioMonitor.IsMonitoring)
        {
            _audioMonitor.Stop();
            _btnListen.Text = "🎧 LISTEN";
            _btnListen.BackColor = Color.FromArgb(51, 65, 85);
            AppendLog("[STOPPED] Audio Monitoring");
        }
        else
        {
            bool started = _audioMonitor.Start(_config.DeckLinkDevice, _config.VideoStandardCode);
            if (started)
            {
                _btnListen.Text = "🔊 LISTENING";
                _btnListen.BackColor = Color.FromArgb(14, 165, 233);
                AppendLog($"[STARTED] Audio Monitoring ({_config.DeckLinkDevice})");
            }
            else
            {
                _btnListen.Text = "🎧 LISTEN";
                _btnListen.BackColor = Color.FromArgb(51, 65, 85);
                AppendLog($"[ERROR] Could not start audio monitoring for {_config.DeckLinkDevice}");
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

        if (_config.Destinations.Count > 0)
        {
            _txtFbTitle.Text = _config.Destinations[0].Name;
            _txtFbUrl.Text = _config.Destinations[0].ServerUrl;
            _txtFbKey.Text = _config.Destinations[0].StreamKey;
        }
        if (_config.Destinations.Count > 1)
        {
            _txtYtTitle.Text = _config.Destinations[1].Name;
            _txtYtUrl.Text = _config.Destinations[1].ServerUrl;
            _txtYtKey.Text = _config.Destinations[1].StreamKey;
        }
        if (_config.Destinations.Count > 2)
        {
            _txtYtNewsTitle.Text = _config.Destinations[2].Name;
            _txtYtNewsUrl.Text = _config.Destinations[2].ServerUrl;
            _txtYtNewsKey.Text = _config.Destinations[2].StreamKey;
        }

        _encoderCombo.SelectedIndex = _config.VideoEncoder switch
        {
            VideoEncoderType.HEVC_NVENC => 0,
            VideoEncoderType.H264_NVENC => 1,
            VideoEncoderType.H264_AMF => 2,
            VideoEncoderType.LibX264 => 3,
            VideoEncoderType.Auto => 4,
            _ => 0
        };
    }

    private static readonly string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "streaming.log");

    private void AppendLog(string message)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(message)) return;

        string trimmed = message.Trim();

        // User requirement: In the log there should ONLY be started, stop, error types
        bool isAllowed = trimmed.StartsWith("[STARTED", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("[STOPPED", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase)
                      || trimmed.StartsWith("[SNAPSHOT", StringComparison.OrdinalIgnoreCase);

        if (!isAllowed) return;

        // Write to file immediately (thread-safe)
        try
        {
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(_logFilePath, $"[{stamp}] {trimmed}\r\n");
        }
        catch { }

        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
                _logTextBox.AppendText($"[{stamp}] {trimmed}\r\n");
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
                            _cpuBadge.Text = $"CPU: {cpuPercent:F0}%";

                            if (cpuPercent > 80)
                            {
                                _cpuBadge.BackColor = Color.FromArgb(220, 38, 38);
                                _cpuBadge.ForeColor = Color.White;
                            }
                            else if (cpuPercent > 50)
                            {
                                _cpuBadge.BackColor = Color.FromArgb(217, 119, 6);
                                _cpuBadge.ForeColor = Color.White;
                            }
                            else
                            {
                                _cpuBadge.BackColor = _settings.DarkMode ? Color.FromArgb(30, 41, 59) : Color.FromArgb(226, 232, 240);
                                _cpuBadge.ForeColor = _settings.DarkMode ? Color.FromArgb(56, 189, 248) : Color.FromArgb(2, 132, 199);
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
        _destCooldownTimer.Stop();
        _destCooldownTimer.Dispose();
        if (_destRunners != null)
        {
            for (int i = 0; i < _destRunners.Length; i++)
            {
                try { _destRunners[i].Dispose(); } catch { }
            }
        }
        _runner.Stop();
        _audioMonitor.Stop();
        _fullscreenForm?.Dispose();

        _settings.Save();
    }
}
