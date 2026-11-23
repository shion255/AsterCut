using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// メイン画面。ウィンドウ枠、ヘッダー、メニュー、タブの骨組み、設定の読み書き、
/// 実行ログと「仕様・使い方」タブを持つ。機能の本体は部分クラスに分けてある。
/// </summary>
internal sealed partial class MainForm : Form
{
    private const string AppTitle = "AsterCut";

    /// <summary>ウィンドウタイトル用の、バージョン併記のアプリ名。</summary>
    private static string AppTitleWithVersion => $"{AppTitle} v{AppVersionText}";

    private readonly AppSettings _settings;

    private readonly TextBox _inputPathBox;
    // ffmpeg / ffprobe / mpv のパスは「オプション」画面で編集し、適用済みの文字列として保持する。
    private string _ffmpegPath = "";
    private string _ffprobePath = "";
    private string _mpvPath = "";
    private string _trimStatusText = "trim / setts: 未確認（無劣化カット対応ビルドが必要）";
    private Color _trimStatusColor = Theme.LabelFore;
    private bool _trimSupported;
    private readonly ComboBox _previewBackendCombo;

    private readonly ThemedTabControl _tabs;
    private readonly TabPage _sourceTab;
    private readonly TabPage _cutTab;
    private readonly TabPage _chapterTab;
    private readonly TabPage _outputTab;
    private readonly TabPage _logTab;
    private readonly TabPage _specTab;

    private readonly Panel _sourceInfoGroup;
    private readonly Panel _audioGroup;

    // 動画編集タブのプレビューまわり (本体は MainForm.Preview.cs)。
    private readonly Panel _mpvHostPanel;
    private readonly Label _previewPositionLabel;
    private readonly Button _previewToggleButton;
    private readonly SeekTrackBar _seekTrackBar;
    private readonly VolumeIconButton _volumeMuteButton;
    private readonly SeekTrackBar _volumeTrackBar;
    private readonly Label _volumeValueLabel;
    private readonly SeekIconButton _seekStartButton;
    private readonly SeekIconButton _frameBackButton;
    private readonly SeekIconButton _frameForwardButton;
    private System.Windows.Forms.Timer _frameStepRepeatTimer = null!;
    private int _frameStepRepeatDirection;
    private bool _frameStepRepeatFast;
    private readonly SeekIconButton _seekEndButton;
    private readonly ToolTip _volumeToolTip = new();
    private readonly ToolTip _seekToolTip = new();

    // 動画編集タブの区間編集まわり (本体は MainForm.Segments.cs)。
    private readonly TextBox _cutStartBox;
    private readonly TextBox _cutEndBox;
    private readonly Panel _segmentGroup;
    private Label _segmentTitleLabel = null!;
    private readonly Dictionary<Control, Rectangle> _previewLayoutBase = new();
    private readonly List<Control[]> _previewCenteredRows = new();
    private bool _previewLayoutReady;
    private Rectangle _mpvHostPanelHomeBounds = new(6, 15, 508, 282);
    private readonly Button _cutRemoveButton;
    private readonly ListBox _segmentList;
    private readonly Panel _segmentListFrame;
    private readonly Label _segmentTotalLabel;
    private readonly TextBox _speedBox;
    private readonly ComboBox _rotationCombo;
    private LinkLabel _rotationLabel = null!;
    private readonly ToolStripMenuItem _undoMenuItem;
    private readonly ToolStripMenuItem _redoMenuItem;
    private readonly ToolStripMenuItem _goToFrameMenuItem;
    private readonly Panel _logGroup;
    private readonly Panel _specGroup;
    private readonly TextBox _logTextBox;
    private readonly RichTextBox _specTextBox;
    private RichTextBoxWheelHook? _specWheelHook;
    private readonly ComboBox _specSectionCombo;
    private readonly TextBox _specSearchBox;
    private readonly Label _specSearchCountLabel;
    private readonly System.Collections.Generic.List<int> _specSearchMatches = new();
    private readonly System.Collections.Generic.List<int> _specSectionIndices = new();
    private int _specSearchCurrent = -1;

    private readonly PictureBox _jobAnimation;
    private readonly MemoryStream? _jobAnimationStream;
    private readonly Label _jobElapsedLabel;
    private readonly Label _jobEtaLabel;
    private readonly Label _statusLabel;
    private readonly ToolTip _statusToolTip;
    private readonly Button _cancelButton;

    // 入力動画の状態。
    private string _inputPath = "";
    private ProbeResult? _probe;
    private List<ProbeStream> _videoStreams = new();
    private List<ProbeStream> _audioStreams = new();
    private long _durationMs;
    private bool _loadingInput;

    private readonly Label _sourceFileNameValue;
    private readonly Label _sourceContainerValue;
    private readonly Label _sourceDurationValue;
    private readonly Label _sourceFileSizeValue;
    private readonly Label _sourceFrameRateValue;
    private readonly Label _sourceCodecValue;
    private readonly TextBox _sourceMetadataTextBox;
    private readonly ToolTip _sourceToolTip = new();
    private readonly CheckedListBox _audioList;

    // 入力履歴ポップアップ。
    private Button _historyButton = null!;
    private Form? _historyPopup;
    private ListBox? _historyListBox;
    private int _historyHoverIndex = -1;
    private bool _historyHoverDelete;
    private DateTime _historyToggleSuppressedUntil = DateTime.MinValue;

    // 履歴ポップアップが対象にしている履歴と入力欄 (入力動画と入力チャプターで共用)。
    private List<string> _historyEntries = null!;
    private TextBox _historyTargetBox = null!;
    private Button _historyAnchorButton = null!;
    private Action<string> _historyPicked = null!;

    // フレーム時刻テーブル (フレームインデックス) の構築状態。
    private long[]? _frameTimesUs;
    private long[]? _frameKeyTimesUs;
    private long _frameBaseUs;
    private Process? _frameIndexProcess;
    private Task<string>? _frameIndexTask;
    private Task<string>? _frameIndexErrorTask;
    private string _frameIndexInputPath = "";
    private readonly System.Windows.Forms.Timer _frameIndexTimer;

    private bool _shownOnce;
    private bool _tabStripKeyActive;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
    }

    public MainForm()
    {
        Icon = AppIcon.Value;
        _settings = AppSettings.Load();

        // フォント基準の自動拡大を止める。ピクセル単位の配置を保つためで、
        // これをしないと右端に固定したコントロール (参照・読み込みボタン、状態表示、
        // キャンセルボタン) が画面の外へずれて切れてしまう。
        AutoScaleMode = AutoScaleMode.None;

        Text = AppTitleWithVersion;
        Font = new Font("Meiryo UI", 9f);
        BackColor = Theme.PageBack;
        ForeColor = Theme.LabelFore;
        MinimumSize = new Size(1120, 900);
        Size = _settings.RememberWindowSize
            ? new Size(_settings.WindowWidth, _settings.WindowHeight)
            : new Size(1120, 1000);
        StartPosition = FormStartPosition.CenterScreen;

        // Dock するパネルは、子を入れる前に幅を明示する。幅が初期値 (200) のままだと、
        // 右端に固定した子の距離がその幅で記録され、表示時に画面の外へずれてしまう。
        // (デザイナーが生成するコードは、Dock でも必ず Size を書く)
        // 幅は復元したウィンドウ幅ではなく、設計時のクライアント幅 (1104) に固定する。
        // 子のアンカー距離はこの幅を基準に記録されるため、復元幅を基準にすると、
        // 復元時より窓を狭くしたときにその分だけ入力動画の欄が縮んで消えてしまう。
        var initialClientWidth = 1104;

        // ログ欄はタブの構築中 (プロファイルの準備など) から WriteLog で使われるため、先に作っておく。
        _logTextBox = MakeLogTextBox();

        // ---- メニューバー ----
        var menuStrip = new MenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()),
        };

        var fileMenu = new ToolStripMenuItem("ファイル") { ForeColor = Theme.MenuFore };
        var newItem = new ToolStripMenuItem("新規", null, (_, _) => ResetToInitialState())
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeys = Keys.Control | Keys.N,
        };
        var openProjectItem = new ToolStripMenuItem("編集プロジェクトを開く", null, (_, _) => _ = OpenEditProjectAsync())
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeys = Keys.Control | Keys.O,
        };
        var saveProjectItem = new ToolStripMenuItem("編集プロジェクトを保存", null, (_, _) => SaveEditProject())
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeys = Keys.Control | Keys.S,
        };
        var restoreItem = new ToolStripMenuItem("編集履歴の復元", null, (_, _) => ShowEditHistoryDialog()) { ForeColor = Theme.MenuFore };
        var closeItem = new ToolStripMenuItem("閉じる", null, (_, _) => Close()) { ForeColor = Theme.MenuFore };
        fileMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            newItem, openProjectItem, saveProjectItem, restoreItem, new ToolStripSeparator(), closeItem,
        });

        var editMenu = new ToolStripMenuItem("編集") { ForeColor = Theme.MenuFore };
        _undoMenuItem = new ToolStripMenuItem("元に戻す")
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeyDisplayString = "Ctrl+Z",
            Enabled = false,
        };
        _undoMenuItem.Click += (_, _) => UndoSegmentOperation();
        _redoMenuItem = new ToolStripMenuItem("やり直し")
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeyDisplayString = "Ctrl+Y",
            Enabled = false,
        };
        _redoMenuItem.Click += (_, _) => RedoSegmentOperation();
        _goToFrameMenuItem = new ToolStripMenuItem("指定したフレームへ移動...")
        {
            ForeColor = Theme.MenuFore,
            ShortcutKeys = Keys.Control | Keys.G,
        };
        _goToFrameMenuItem.Click += (_, _) => ShowGoToFrameDialog();
        editMenu.DropDownOpening += (_, _) => _goToFrameMenuItem.Enabled = _previewReady && TestFrameTableReady();
        editMenu.DropDownItems.AddRange(new ToolStripItem[] { _undoMenuItem, _redoMenuItem, new ToolStripSeparator(), _goToFrameMenuItem });

        var toolMenu = new ToolStripMenuItem("ツール") { ForeColor = Theme.MenuFore };
        var updateFfmpegItem = new ToolStripMenuItem("ffmpeg / ffprobe / ffplay の更新...")
        {
            ForeColor = Theme.MenuFore,
        };
        updateFfmpegItem.Click += (_, _) => ShowFfmpegUpdateDialog();
        toolMenu.DropDownItems.Add(updateFfmpegItem);
        var optionsItem = new ToolStripMenuItem("オプション...")
        {
            ForeColor = Theme.MenuFore,
        };
        optionsItem.Click += (_, _) => ShowOptionsDialog();
        toolMenu.DropDownItems.Add(new ToolStripSeparator());
        toolMenu.DropDownItems.Add(optionsItem);

        var helpMenu = new ToolStripMenuItem("ヘルプ") { ForeColor = Theme.MenuFore };
        var aboutItem = new ToolStripMenuItem("バージョン情報...") { ForeColor = Theme.MenuFore };
        aboutItem.Click += (_, _) => ShowAboutDialog();
        helpMenu.DropDownItems.Add(aboutItem);

        menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, editMenu, toolMenu, helpMenu });

        // ---- ヘッダー ----
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Size = new Size(initialClientWidth, 78),
            BackColor = Theme.PanelBack,
        };

        header.Controls.Add(Theme.MakeLabel("入力動画:", 12, 8, 75, 26));
        _inputPathBox = Theme.MakeTextBox(88, 8, 803);
        _inputPathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _inputPathBox.AllowDrop = true;
        _inputPathBox.DragEnter += (_, e) =>
        {
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        };
        _inputPathBox.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                OpenDroppedInputFile(files[0]);
            }
        };
        _inputPathBox.KeyDown += (_, e) =>
        {
            if (e.Alt && e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ShowInputHistoryPopup();
            }
        };
        header.Controls.Add(_inputPathBox);

        var historyButton = Theme.MakeButton("▼", 867, 9, 22, 24);
        historyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        historyButton.FlatAppearance.BorderSize = 0;
        historyButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 54, 72);
        historyButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(86, 74, 110);
        historyButton.BackColor = Theme.BoxBack;
        historyButton.ForeColor = Theme.MutedFore;
        historyButton.Font = new Font("Meiryo UI", 8f);
        historyButton.TabStop = false;
        var historyToolTip = new ToolTip();
        historyToolTip.SetToolTip(historyButton, "入力動画の履歴を表示 (最大 10 件)");
        historyButton.Click += (_, _) =>
        {
            // ポップアップを閉じるためにボタンが押された直後の Click は飲み込む (トグルが二重に働くのを防ぐ)。
            if (DateTime.UtcNow < _historyToggleSuppressedUntil)
            {
                _historyToggleSuppressedUntil = DateTime.MinValue;
                return;
            }
            ShowInputHistoryPopup();
        };
        _historyButton = historyButton;
        header.Controls.Add(historyButton);
        historyButton.BringToFront();

        var inputBrowseButton = Theme.MakeButton("参照...", 899, 6, 78);
        inputBrowseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        inputBrowseButton.Click += (_, _) => BrowseInputFile();
        header.Controls.Add(inputBrowseButton);
        var inputLoadButton = Theme.MakeButton("読み込み", 983, 6, 105);
        inputLoadButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        inputLoadButton.Click += (_, _) => LoadInputRequested();
        header.Controls.Add(inputLoadButton);

        header.Controls.Add(Theme.MakeLabel("プレビュー:", 12, 42, 78, 26));
        _previewBackendCombo = new ThemedComboBox
        {
            Location = new Point(90, 41),
            Size = new Size(120, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
        };
        _previewBackendCombo.DrawItem += DrawFormatComboItem;
        _previewBackendCombo.Items.Add("mpv");
        _previewBackendCombo.SelectedIndex = 0;
        header.Controls.Add(_previewBackendCombo);

        // 入力欄は、先頭文字の左に 2px の余白を付け、右端の ▼ ボタンの分も空ける。ハンドルが作り直されるたびに付け直す。
        _inputPathBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_inputPathBox, 2, 26);
        ApplyTextBoxMarginsOnce(_inputPathBox, 2, 26);

        // ---- タブ ----
        _tabs = new ThemedTabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6),
            DrawMode = TabDrawMode.OwnerDrawFixed,
            SizeMode = TabSizeMode.Fixed,
            ItemSize = new Size(126, 32),
        };
        _tabs.DrawItem += Tabs_DrawItem;

        _sourceTab = MakeTabPage("ソース");
        _sourceInfoGroup = Theme.MakeGroupPanel(10, 12, 1015, 342, "映像情報");
        // 各項目を「項目名」「値」の 2 列で、1 項目につき 1 行ずつ表示する。
        Label MakeSourceRow(string name, int y, out Label value)
        {
            var nameLabel = Theme.MakeLabel(name, 14, y, 110, 25);
            _sourceInfoGroup.Controls.Add(nameLabel);
            value = Theme.MakeLabel("—", 132, y, 860, 25);
            value.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            value.AutoEllipsis = true;
            _sourceInfoGroup.Controls.Add(value);
            return nameLabel;
        }
        MakeSourceRow("ファイル名:", 30, out _sourceFileNameValue);
        _sourceFileNameValue.Text = "未読み込み";
        _sourceFileNameValue.Font = new Font("Segoe UI", 9f);
        _sourceToolTip.SetToolTip(_sourceFileNameValue, "未読み込み");
        MakeSourceRow("ファイルサイズ:", 58, out _sourceFileSizeValue);
        MakeSourceRow("再生時間:", 86, out _sourceDurationValue);
        MakeSourceRow("コンテナ詳細:", 114, out _sourceContainerValue);
        MakeSourceRow("コーデック:", 142, out _sourceCodecValue);
        MakeSourceRow("フレームレート:", 170, out _sourceFrameRateValue);
        var metadataTitle = new LinkLabel
        {
            Text = "_ASTER_CUT (ウィンドウ表示)",
            LinkArea = new LinkArea(11, 9),
            Location = new Point(14, 198),
            Size = new Size(340, 23),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            LinkColor = Color.FromArgb(172, 158, 235),
            ActiveLinkColor = Color.FromArgb(200, 188, 245),
            LinkBehavior = LinkBehavior.HoverUnderline,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        metadataTitle.LinkClicked += (_, _) => ShowEditorMetadataWindow();
        _sourceInfoGroup.Controls.Add(metadataTitle);
        _sourceMetadataTextBox = new TextBox
        {
            Location = new Point(14, 222),
            Size = new Size(975, 106),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Text = "—",
        };
        Theme.ApplyDarkScrollBars(_sourceMetadataTextBox);
        _sourceInfoGroup.Controls.Add(_sourceMetadataTextBox);
        _sourceTab.Controls.Add(_sourceInfoGroup);

        _audioGroup = Theme.MakeGroupPanel(10, 366, 1015, 220, "保持する音声トラック");
        var audioListFrame = new Panel
        {
            Location = new Point(14, 29),
            Size = new Size(975, 125),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
        };
        _audioList = new ThemedCheckedListBox
        {
            Location = new Point(2, 1),
            Size = new Size(971, 123),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
        };
        audioListFrame.Controls.Add(_audioList);
        Theme.ApplyDarkScrollBars(_audioList);
        _audioList.ItemCheck += (sender, _) =>
        {
            if (_segments.Count == 0)
            {
                // ItemCheck のタイミングでは Checked 状態がまだ更新されていないため、更新後に出力名を再計算する。
                try { ((Control)sender!).BeginInvoke(new Action(UpdateCutOutputName)); } catch { }
            }
        };
        _audioGroup.Controls.Add(audioListFrame);
        var audioAllButton = Theme.MakeButton("すべて保持", 14, 164, 105, 30);
        audioAllButton.Click += (_, _) => { for (var i = 0; i < _audioList.Items.Count; i++) _audioList.SetItemChecked(i, true); };
        var audioNoneButton = Theme.MakeButton("すべて削除", 127, 164, 105, 30);
        audioNoneButton.Click += (_, _) => { for (var i = 0; i < _audioList.Items.Count; i++) _audioList.SetItemChecked(i, false); };
        _audioGroup.Controls.Add(audioAllButton);
        _audioGroup.Controls.Add(audioNoneButton);
        _sourceTab.Controls.Add(_audioGroup);

        _cutTab = MakeTabPage("動画編集");
        // mpv 用の映像表示領域。mpv.exe は --wid でこのパネルの子ウィンドウとして描画する。
        _mpvHostPanel = new FocusablePanel
        {
            Location = new Point(6, 15),
            Size = new Size(508, 282),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            BackColor = Color.Black,
        };
        _mpvHostPanel.Resize += (_, _) => FitMpvWindow();
        _cutTab.Controls.Add(_mpvHostPanel);
        _previewPositionLabel = Theme.MakeLabel("--:--:--.--- / --:--:--.---", 14, 299, 500, 24);
        _previewPositionLabel.TextAlign = ContentAlignment.MiddleCenter;
        _cutTab.Controls.Add(_previewPositionLabel);
        _previewToggleButton = Theme.MakeButton("再生", 14, 357, 82, 30);
        _previewToggleButton.Click += (_, _) => TogglePreviewPlayback();
        _cutTab.Controls.Add(_previewToggleButton);
        _seekStartButton = new SeekIconButton("First", 104, 357, 34, 30, "先頭へシーク", _seekToolTip);
        _seekStartButton.Click += (_, _) => MovePreviewToBoundary(false);
        _frameBackButton = new SeekIconButton("Previous", 144, 357, 34, 30, "1 フレーム戻る", _seekToolTip);
        _frameBackButton.Click += (_, _) => MovePreviewFrame(-1);
        _frameBackButton.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) StartFrameStepRepeat(-1); };
        _frameBackButton.MouseUp += (_, _) => StopFrameStepRepeat();
        _frameBackButton.MouseLeave += (_, _) => StopFrameStepRepeat();
        _frameForwardButton = new SeekIconButton("Next", 184, 357, 34, 30, "1 フレーム進む", _seekToolTip);
        _frameForwardButton.Click += (_, _) => MovePreviewFrame(1);
        _frameForwardButton.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) StartFrameStepRepeat(1); };
        _frameForwardButton.MouseUp += (_, _) => StopFrameStepRepeat();
        _frameForwardButton.MouseLeave += (_, _) => StopFrameStepRepeat();
        _seekEndButton = new SeekIconButton("Last", 224, 357, 34, 30, "末尾へシーク", _seekToolTip);
        _seekEndButton.Click += (_, _) => MovePreviewToBoundary(true);
        _cutTab.Controls.Add(_seekStartButton);
        _cutTab.Controls.Add(_frameBackButton);
        _cutTab.Controls.Add(_frameForwardButton);
        _cutTab.Controls.Add(_seekEndButton);
        var previewPopupButton = Theme.MakeButton("ポップアップ", 272, 357, 115, 30);
        previewPopupButton.Click += (_, _) => ShowPreviewPopup();
        _cutTab.Controls.Add(previewPopupButton);
        var clippingButton = Theme.MakeButton("クリッピング", 395, 357, 105, 30);
        clippingButton.Click += (_, _) => ShowClippingForm();
        _cutTab.Controls.Add(clippingButton);
        _seekTrackBar = new SeekTrackBar
        {
            Minimum = 0,
            Maximum = 10000,
            Value = 0,
            TickFrequency = 1000,
            SmallChange = 10,
            LargeChange = 100,
            AutoSize = false,
            Location = new Point(8, 319),
            Size = new Size(500, 35),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        _seekTrackBar.MouseDown += (_, _) => { if (_previewReady) { _popupPreviewSeeking = false; _previewSeeking = true; } };
        _seekTrackBar.MouseUp += (_, _) =>
        {
            if (_previewSeeking)
            {
                _previewSeeking = false;
                SetPreviewPositionFromTrackBar(_seekTrackBar);
            }
        };
        _seekTrackBar.Scroll += (_, _) => SetPreviewPositionFromTrackBar(_seekTrackBar);
        _seekTrackBar.KeyDown += (_, e) => HandleSeekTrackBarKeyDown(e);
        _seekTrackBar.KeyUp += (_, e) => HandleSeekTrackBarKeyUp(_seekTrackBar, e);
        _seekTrackBar.MouseWheel += (_, e) => HandleSeekTrackBarMouseWheel(e);
        _cutTab.Controls.Add(_seekTrackBar);
        _volumeMuteButton = new VolumeIconButton(14, 396, 35, 30);
        _volumeMuteButton.Click += (_, _) => TogglePreviewMute();
        _cutTab.Controls.Add(_volumeMuteButton);
        _volumeTrackBar = new SeekTrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 25,
            SmallChange = 1,
            LargeChange = 5,
            AutoSize = false,
            Location = new Point(55, 394),
            Size = new Size(260, 34),
        };
        _volumeTrackBar.Scroll += (_, _) => SetPreviewVolumeFromTrackBar(_volumeTrackBar);
        _volumeTrackBar.KeyDown += (_, e) => HandleVolumeTrackBarKeyDown(_volumeTrackBar, e);
        _volumeTrackBar.MouseUp += (_, _) => SaveSettingsFromUi();
        _volumeTrackBar.KeyUp += (_, _) => SaveSettingsFromUi();
        _cutTab.Controls.Add(_volumeTrackBar);
        _volumeValueLabel = Theme.MakeLabel("80%", 323, 399, 50, 24);
        _cutTab.Controls.Add(_volumeValueLabel);
        // 音量の初期値は設定から取る。
        _volumeSliderValue = _settings.VolumeSliderValue;
        _volumeBeforeMute = _settings.Volume;
        _previewMuted = _settings.PreviewMuted;
        _volumeTrackBar.Value = Math.Clamp(_volumeSliderValue, 0, 100);
        _volumeValueLabel.Text = $"{_volumeTrackBar.Value}%";
        UpdatePreviewMuteButtons();

        // 回転と変更後の速度 (音量バーの下、同じ行に横並び)。
        var speedLabel = Theme.MakeLabel("変更後の速度:", 14, 468, 110, 24);
        _cutTab.Controls.Add(speedLabel);
        _speedBox = Theme.MakeTextBox(128, 466, 66, 28);
        // 数字と小数点だけの入力欄のため IME は無効にする (全角の数字は数値として読み取れないため)。
        _speedBox.ImeMode = ImeMode.Disable;
        _speedBox.Text = "1.00";
        _speedBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_speedBox, 2, 0);
        _speedBox.MouseWheel += (_, e) => SpeedBoxMouseWheel(e);
        _speedBox.KeyDown += (_, e) => SpeedBoxKeyDown(e);
        _speedBox.Leave += (_, _) => CommitSpeedBoxText();
        _cutTab.Controls.Add(_speedBox);
        var rotationLabel = _rotationLabel = new LinkLabel
        {
            Text = "回転 (プレビュー):",
            Location = new Point(14, 434),
            Size = new Size(135, 24),
            Font = Font,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            LinkColor = Color.FromArgb(172, 158, 235),
            ActiveLinkColor = Color.FromArgb(200, 188, 245),
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        rotationLabel.Links.Add(new LinkLabel.Link(3, 7));
        rotationLabel.LinkClicked += (_, _) => ShowRotationPreviewPopup();
        _cutTab.Controls.Add(rotationLabel);
        _rotationCombo = new ThemedComboBox
        {
            Size = new Size(158, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
        };
        _rotationCombo.DrawItem += DrawFormatComboItem;
        _rotationCombo.Items.AddRange(new[] { "なし", "右90°（時計回り）", "180°", "左90°（反時計回り）" });
        _rotationCombo.SelectedIndex = 0;
        _rotationCombo.SelectedIndexChanged += (_, _) =>
        {
            UpdateCutOutputName();
            // 回転プレビューを開いている間は、選択の変更をすぐに反映する。
            if (_previewPopupKind == "rotation" && _previewPopupForm is { IsDisposed: false }) SetPreviewRotation();
        };
        // 回転コンボの左位置は、速度の行の間隔に合わせて文字幅から決める。
        var measureSize = new Size(10000, 100);
        var speedTextWidth = TextRenderer.MeasureText(speedLabel.Text, speedLabel.Font, measureSize, TextFormatFlags.NoPadding).Width;
        var rotationTextWidth = TextRenderer.MeasureText(rotationLabel.Text, rotationLabel.Font, measureSize, TextFormatFlags.NoPadding).Width;
        var speedLabelGap = Math.Max(2, _speedBox.Left - (speedLabel.Left + speedTextWidth));
        _rotationCombo.Location = new Point(rotationLabel.Left + rotationTextWidth + speedLabelGap, 432);
        rotationLabel.Width = Math.Max(40, _rotationCombo.Left - rotationLabel.Left - 2);
        _cutTab.Controls.Add(_rotationCombo);
        // 変更後の速度は、回転の右隣に置く。
        speedLabel.Location = new Point(_rotationCombo.Right + 16, 434);
        _speedBox.Location = new Point(speedLabel.Left + speedTextWidth + speedLabelGap, 432);
        // 再生時間は、プレビュー列の中央にそろえる
        // (実際の配置は UpdatePreviewColumnLayout が行う)。
        _previewCenteredRows.Add(new Control[] { _previewPositionLabel });
        var speedTabToolTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300 };
        var speedHintText = string.Join(Environment.NewLine,
            "再生速度を設定します。",
            "マウスホイールと ↑ / ↓ キーで 0.05 ずつ増減でき、直接入力もできます (0.05 ～ 100.00)。",
            "保持する音声がある速度変更は再エンコードします。",
            "音声なしは（1 区間・クリッピングなしの場合）ストリームコピー方式です。",
            "保持する音声は「ソース」タブで選択します。");
        // 速度のツールチップは、自動表示だとボックスに被ってしまうため、
        // ボックスのすぐ下に固定して出す (ラベルに重ねたときも同じ位置)。
        EventHandler showSpeedHint = (_, _) => speedTabToolTip.Show(speedHintText, _speedBox, 0, _speedBox.Height + 6);
        _speedBox.MouseEnter += showSpeedHint;
        speedLabel.MouseEnter += showSpeedHint;
        _speedBox.MouseLeave += (_, _) => speedTabToolTip.Hide(_speedBox);
        speedLabel.MouseLeave += (_, _) => speedTabToolTip.Hide(_speedBox);
        var rotationHintText = string.Join(Environment.NewLine,
            "再生時の向きだけを変えます。",
            "回転のために映像・音声を再エンコードすることはありません。",
            "回転メタデータ（表示マトリクス）だけを書き換えます（FFmpeg 6.0 以降）。",
            "入力に回転情報がある場合は加算します。",
            "ファイル名に「_rot90cw / _rot180 / _rot90ccw」を付けます。",
            "プレーヤーによっては回転メタデータが無視されることがあります。",
            "「(プレビュー)」をクリックすると、回転後の向きをポップアップで確認できます（表示のみ）。");
        speedTabToolTip.SetToolTip(_rotationCombo, rotationHintText);
        speedTabToolTip.SetToolTip(rotationLabel, rotationHintText);

        // 開始・終了と区間の操作ボタン。
        var cutStartLabel = Theme.MakeLabel("開始:", 520, 16, 48, 24);
        cutStartLabel.Font = Font;
        cutStartLabel.Width = cutStartLabel.PreferredWidth;
        _cutTab.Controls.Add(cutStartLabel);
        var cutStartBoxX = 520 + cutStartLabel.PreferredWidth + 1;
        _cutStartBox = Theme.MakeTextBox(cutStartBoxX, 15, 145);
        _cutStartBox.Text = "00:00:00.000";
        _cutStartBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_cutStartBox, 2, 0);
        _cutStartBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            try { SeekPreviewToMs(ConvertTimeToMs(_cutStartBox.Text)); }
            catch { /* 時刻を解釈できない場合は何もしない */ }
        };
        _cutTab.Controls.Add(_cutStartBox);
        var setCurrentStartButton = Theme.MakeButton("現在位置を開始に設定", cutStartBoxX + 155, 14, 170, 30);
        setCurrentStartButton.Click += (_, _) =>
        {
            if (_previewReady)
            {
                var ms = GetPreviewCurrentPositionMs();
                _cutStartBox.Text = TimeText.FormatClockFromMs(ms);
                WriteLog($"開始位置を設定: {TimeText.FormatClockFromMs(ms)} ({GetFrameInfoText(ms)})");
            }
            else
            {
                MessageBox.Show("プレビューが準備できていません。", AppTitle);
            }
        };
        _cutTab.Controls.Add(setCurrentStartButton);
        // 「この位置にシーク」の幅は、文字の実測幅に余白を加えて決める。
        var seekButtonWidth = TextRenderer.MeasureText("この位置にシーク", Font, new Size(10000, 100), TextFormatFlags.NoPadding).Width + 28;
        var seekToStartButton = Theme.MakeButton("この位置にシーク", cutStartBoxX + 333, 14, seekButtonWidth, 30);
        seekToStartButton.Click += (_, _) =>
        {
            try { SeekPreviewToMs(ConvertTimeToMs(_cutStartBox.Text)); }
            catch { /* 時刻を解釈できない場合は何もしない */ }
        };
        _cutTab.Controls.Add(seekToStartButton);
        var cutEndLabel = Theme.MakeLabel("終了:", 520, 56, 48, 24);
        cutEndLabel.Font = Font;
        cutEndLabel.Width = cutEndLabel.PreferredWidth;
        _cutTab.Controls.Add(cutEndLabel);
        var cutEndBoxX = 520 + cutEndLabel.PreferredWidth + 1;
        _cutEndBox = Theme.MakeTextBox(cutEndBoxX, 55, 145);
        _cutEndBox.Text = "00:00:00.000";
        _cutEndBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_cutEndBox, 2, 0);
        _cutEndBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            try { SeekPreviewToMs(ConvertTimeToMs(_cutEndBox.Text)); }
            catch { /* 時刻を解釈できない場合は何もしない */ }
        };
        _cutTab.Controls.Add(_cutEndBox);
        var setCurrentEndButton = Theme.MakeButton("現在位置を終了に設定", cutEndBoxX + 155, 54, 170, 30);
        setCurrentEndButton.Click += (_, _) =>
        {
            if (_previewReady)
            {
                var ms = GetPreviewCurrentPositionMs();
                _cutEndBox.Text = TimeText.FormatClockFromMs(ms);
                WriteLog($"終了位置を設定: {TimeText.FormatClockFromMs(ms)} ({GetFrameInfoText(ms)})");
            }
            else
            {
                MessageBox.Show("プレビューが準備できていません。", AppTitle);
            }
        };
        _cutTab.Controls.Add(setCurrentEndButton);
        var seekToEndButton = Theme.MakeButton("この位置にシーク", cutEndBoxX + 333, 54, seekButtonWidth, 30);
        seekToEndButton.Click += (_, _) =>
        {
            try { SeekPreviewToMs(ConvertTimeToMs(_cutEndBox.Text)); }
            catch { /* 時刻を解釈できない場合は何もしない */ }
        };
        _cutTab.Controls.Add(seekToEndButton);
        var cutAddButton = Theme.MakeButton("区間を追加", 520, 98, 104, 32);
        cutAddButton.Click += (_, _) => AddCutSegment();
        _cutTab.Controls.Add(cutAddButton);
        var clipAddButton = Theme.MakeButton("クリッピングして追加", 630, 98, 140, 32);
        clipAddButton.Click += (_, _) => AddCutSegment(true);
        _cutTab.Controls.Add(clipAddButton);
        _cutRemoveButton = Theme.MakeButton("選択を削除", 776, 98, 105, 32);
        _cutRemoveButton.Click += (_, _) => RemoveSelectedSegment();
        _cutTab.Controls.Add(_cutRemoveButton);
        var cutUpButton = new ArrowButton
        {
            Location = new Point(887, 98),
            Size = new Size(42, 32),
            DirectionUp = true,
        };
        cutUpButton.Click += (_, _) => MoveCutSegment(-1);
        _cutTab.Controls.Add(cutUpButton);
        var cutDownButton = new ArrowButton
        {
            Location = new Point(935, 98),
            Size = new Size(42, 32),
            DirectionUp = false,
        };
        cutDownButton.Click += (_, _) => MoveCutSegment(1);
        _cutTab.Controls.Add(cutDownButton);
        var cutSplitAllButton = Theme.MakeButton("全区間分割", 983, 98, 105, 32);
        cutSplitAllButton.Click += (_, _) => SplitAllCutSegments();
        _cutTab.Controls.Add(cutSplitAllButton);
        var splitAllToolTip = new ToolTip();
        splitAllToolTip.SetToolTip(cutSplitAllButton, "すべての区間を分割し、開始時刻順に並べ替えます。");

        // 区間一覧のグループ。
        var segmentGroup = _segmentGroup = new Panel
        {
            Location = new Point(520, 143),
            Size = new Size(510, 525),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.PageBack,
        };
        _cutTab.Controls.Add(segmentGroup);
        var segmentTitle = _segmentTitleLabel = Theme.MakeLabel("区間一覧: くっついている区間は結合されます", 12, 2, 480, 23);
        segmentTitle.Font = new Font(Font, FontStyle.Bold);
        segmentTitle.BackColor = Theme.PageBack;
        // 見出しラベルの幅は文の幅までにする (背景色が不透明なため、広いままだと右のファイル数ラベルを覆い隠してしまう)。
        segmentTitle.Width = segmentTitle.PreferredWidth;
        segmentGroup.Controls.Add(segmentTitle);
        var totalLeft = 12 + segmentTitle.PreferredWidth + 16;
        _segmentTotalLabel = Theme.MakeLabel("分割ファイル数: 0　結合ファイル数: 0", totalLeft, 2, Math.Max(60, segmentGroup.ClientSize.Width - totalLeft - 12), 23);
        _segmentTotalLabel.BackColor = Theme.PageBack;
        segmentGroup.Controls.Add(_segmentTotalLabel);
        _segmentTotalLabel.BringToFront();
        // 枠は自前で描く (標準の枠は、スクロールバーを暗くするテーマの都合で、マウスを重ねると青く変わってしまうため)。
        _segmentListFrame = new Panel
        {
            Location = new Point(12, 28),
            Size = new Size(484, 483),
            BackColor = Theme.BoxBack,
        };
        _segmentListFrame.Paint += (_, e) =>
        {
            // 枠の角は、区間カードの角丸 (半径6) と同心の角丸にする。
            var previousSmoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = CreateRoundedRectPath(new Rectangle(0, 0, _segmentListFrame.Width - 1, _segmentListFrame.Height - 1), SegmentListFrameCornerRadius);
            using var pen = new Pen(Color.FromArgb(88, 80, 110));
            e.Graphics.DrawPath(pen, path);
            e.Graphics.SmoothingMode = previousSmoothing;
        };
        segmentGroup.Controls.Add(_segmentListFrame);
        _segmentList = new ListBox
        {
            Location = new Point(1, 1),
            Size = new Size(482, 481),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            HorizontalScrollbar = true,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Font = Font,
            DrawMode = DrawMode.OwnerDrawVariable,
            SelectionMode = SelectionMode.MultiExtended,
            AllowDrop = true,
        };
        _segmentList.SizeChanged += (_, _) => UpdateSegmentListRegion();
        UpdateSegmentListRegion();
        Theme.ApplyDarkScrollBars(_segmentList);
        _segmentList.ItemHeight = Math.Max(26, _segmentList.Font.Height + 10);
        _segmentList.DrawItem += (_, e) => DrawSegmentListItem(e);
        _segmentList.MeasureItem += SegmentListMeasureItem;
        _segmentList.MouseDown += (sender, e) => SegmentListMouseDownForDrag(e);
        _segmentList.MouseUp += (_, _) => UpdateSegmentStableSelection();
        _segmentList.MouseMove += (sender, e) => SegmentListMouseMoveForDrag(e);
        _segmentList.DragOver += (sender, e) => SegmentListDragOver(e);
        _segmentList.DragDrop += (sender, e) => SegmentListDragDrop(e);
        _segmentList.DragLeave += (_, _) => ClearSegmentDropFeedback();
        _segmentList.MouseDoubleClick += (sender, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var hit = _segmentList.IndexFromPoint(e.Location);
            if (hit >= 0)
            {
                _segmentList.SelectedIndex = hit;
                ApplySegmentToCutBoxes(hit);
            }
        };
        var segmentContextMenu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()),
        };
        var overwriteItem = new ToolStripMenuItem("上書き") { ForeColor = Theme.MenuFore };
        overwriteItem.Click += (_, _) => OverwriteCutSegment(_segmentContextIndex);
        segmentContextMenu.Items.Add(overwriteItem);
        var popupPlayItem = new ToolStripMenuItem("この区間をポップアップ再生") { ForeColor = Theme.MenuFore };
        popupPlayItem.Click += (_, _) => ShowSegmentPreviewPopup(_segmentContextIndex);
        segmentContextMenu.Items.Add(popupPlayItem);
        _segmentList.MouseDown += (sender, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _segmentList.IndexFromPoint(e.Location);
            if (hit >= 0)
            {
                // 選択中の行を右クリックしたときは、複数選択を保ったままメニューを出す。
                var alreadySelected = false;
                foreach (int selected in _segmentList.SelectedIndices)
                {
                    if (selected == hit) { alreadySelected = true; break; }
                }
                if (!alreadySelected) _segmentList.SelectedIndex = hit;
                _segmentContextIndex = hit;
                segmentContextMenu.Show(_segmentList, e.Location);
            }
        };
        _segmentList.KeyDown += (_, e) =>
        {
            if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && !e.Control && !e.Alt && !e.Shift)
            {
                // ↑↓ は選択の移動ではなく、区間そのものの並べ替えにする (行の ↑↓ ボタンと同じ)。
                if (_segmentList.SelectedIndex >= 0)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    MoveCutSegment(e.KeyCode == Keys.Up ? -1 : 1);
                }
                return;
            }
            if (e.KeyCode == Keys.Delete && !e.Control && !e.Alt)
            {
                if (_segmentList.SelectedIndex >= 0)
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    // 削除の処理 (取り消し用の控え、ログ) は「選択を削除」ボタンと共通にする。
                    _cutRemoveButton.PerformClick();
                }
                return;
            }
            if (!e.Control || e.Alt || e.Shift) return;
            if (e.KeyCode == Keys.Z && _segmentUndoStack.Count > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                UndoSegmentOperation();
            }
            else if (e.KeyCode == Keys.Y && _segmentRedoStack.Count > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RedoSegmentOperation();
            }
            else if (e.KeyCode == Keys.A && _segments.Count > 0)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                var all = new int[_segments.Count];
                for (var i = 0; i < all.Length; i++) all[i] = i;
                SelectSegmentIndices(all);
            }
        };
        _segmentListFrame.Controls.Add(_segmentList);

        _chapterTab = MakeTabPage("チャプター");
        BuildChapterTab();

        _outputTab = MakeTabPage("動画出力");
        BuildOutputTab();

        _logTab = MakeTabPage("実行ログ");
        _logGroup = Theme.MakeGroupPanel(10, 44, 1015, 675, "実行ログ");
        var logCopyButton = Theme.MakeButton("ログをコピー", 12, 9, 120, 28);
        logCopyButton.Click += (_, _) => CopyLog();
        var logSaveButton = Theme.MakeButton("ログを保存...", 140, 9, 120, 28);
        logSaveButton.Click += (_, _) => SaveLog();
        var logClearButton = Theme.MakeButton("ログを消去", 268, 9, 110, 28);
        logClearButton.Click += (_, _) => { _logTextBox.Clear(); WriteLog("実行ログを消去しました。"); };
        _logTab.Controls.Add(logCopyButton);
        _logTab.Controls.Add(logSaveButton);
        _logTab.Controls.Add(logClearButton);
        _logGroup.Controls.Add(_logTextBox);
        _logTab.Controls.Add(_logGroup);

        _specTab = MakeTabPage("仕様・使い方");
        _specGroup = Theme.MakeGroupPanel(10, 12, 1015, 675, "仕様・使い方");
        _specTextBox = new RichTextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            DetectUrls = false,
            HideSelection = false,
            BackColor = Color.FromArgb(23, 22, 29),
            ForeColor = Color.FromArgb(203, 198, 216),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Location = new Point(12, 29),
            Size = new Size(989, 638),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _specTextBox.Text = LoadSpecText();
        ApplySpecTextFonts();
        _specGroup.Controls.Add(_specTextBox);
        Theme.ApplyDarkScrollBars(_specTextBox);
        // ホイールスクロールの遅れをなくすため、標準のホイール処理を直接スクロールへ置き換える。
        _specWheelHook = new RichTextBoxWheelHook(_specTextBox);

        // 仕様・使い方の項目ジャンプとキーワード検索 (ページ内検索)。見出しの隣に、枠の高さに収まる大きさで置く。
        _specSectionCombo = new ThemedComboBox
        {
            Location = new Point(124, 2),
            Size = new Size(150, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 21,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
        };
        _specSectionCombo.DrawItem += DrawSpecSectionComboItem;
        _specGroup.Controls.Add(_specSectionCombo);
        BuildSpecSectionList();
        _specSectionCombo.SelectedIndexChanged += (_, _) => JumpToSpecSection();
        _specSearchBox = Theme.MakeTextBox(282, 1, 190, 23);
        _specGroup.Controls.Add(_specSearchBox);
        var specSearchPrevButton = new ArrowButton
        {
            Location = new Point(478, 3),
            Size = new Size(24, 22),
            DirectionUp = true,
        };
        specSearchPrevButton.Click += (_, _) => MoveSpecSearchMatch(-1);
        _specGroup.Controls.Add(specSearchPrevButton);
        var specSearchNextButton = new ArrowButton
        {
            Location = new Point(506, 3),
            Size = new Size(24, 22),
            DirectionUp = false,
        };
        specSearchNextButton.Click += (_, _) => MoveSpecSearchMatch(1);
        _specGroup.Controls.Add(specSearchNextButton);
        _specSearchCountLabel = Theme.MakeLabel("", 536, 1, 96, 23);
        _specGroup.Controls.Add(_specSearchCountLabel);
        // 見出しラベルの枠 (幅 480) と重なるため、検索まわりを前面へ出す。
        _specSectionCombo.BringToFront();
        _specSearchBox.BringToFront();
        _specSearchCountLabel.BringToFront();
        specSearchPrevButton.BringToFront();
        specSearchNextButton.BringToFront();
        var specSearchToolTip = new ToolTip();
        specSearchToolTip.SetToolTip(_specSectionCombo, "選択した項目の先頭へ移動します");
        specSearchToolTip.SetToolTip(_specSearchBox, "キーワードを入力すると、仕様・使い方の中を検索します (Enter で次の一致、Shift+Enter で前の一致)");
        specSearchToolTip.SetToolTip(specSearchPrevButton, "前の一致へ");
        specSearchToolTip.SetToolTip(specSearchNextButton, "次の一致へ");
        _specSearchBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_specSearchBox, 2, 0);
        if (_specSearchBox.IsHandleCreated) ApplyTextBoxMarginsOnce(_specSearchBox, 2, 0);
        _specSearchBox.TextChanged += (_, _) => RefreshSpecSearch();
        _specSearchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                MoveSpecSearchMatch(e.Shift ? -1 : 1);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _specSearchBox.Clear();
            }
        };
        _specTab.Controls.Add(_specGroup);

        _tabs.TabPages.AddRange(new[] { _sourceTab, _cutTab, _chapterTab, _outputTab, _logTab, _specTab });
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateEditSummary();
            UpdateTabPageLayouts();
            if (_tabs.SelectedTab == _chapterTab) MovePreviewControls(true);
            else if (_tabs.SelectedTab == _cutTab) MovePreviewControls(false);
            if (_tabs.SelectedTab == _logTab) ScrollLogToEnd();
        };
        _tabs.KeyDown += (_, e) =>
        {
            // タブ一覧がアクティブなときの ↑ は最初のタブ、↓ は最後のタブへの移動にする
            // (フォーム側のキー判定でも同じ移動をするが、届き方で反応が変わらないよう両方で受ける)。
            // プレビュー操作中は ↑↓ が音量なので除く。
            if (_previewKeyboardActive || !_tabStripKeyActive || e.KeyCode is not (Keys.Up or Keys.Down)) return;
            _tabs.SelectedIndex = e.KeyCode == Keys.Up ? 0 : _tabs.TabCount - 1;
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        _tabs.MouseWheel += (_, e) =>
        {
            // タブの見出しの上でのホイール操作で、隣のタブへ移動する。
            var point = _tabs.PointToClient(Control.MousePosition);
            var headerBottom = 0;
            for (var ti = 0; ti < _tabs.TabCount; ti++)
            {
                var tabRect = _tabs.GetTabRect(ti);
                if (tabRect.Bottom > headerBottom) headerBottom = tabRect.Bottom;
            }
            if (point.Y > headerBottom) return;
            var nextIndex = _tabs.SelectedIndex + (e.Delta < 0 ? 1 : -1);
            if (nextIndex >= 0 && nextIndex < _tabs.TabCount)
            {
                if (e is HandledMouseEventArgs handled) handled.Handled = true;
                _tabs.SelectedIndex = nextIndex;
            }
        };
        // 矢印キーでタブを移動できるのは、タブ一覧がアクティブな間だけにする。
        // 見出しのクリックと、キー操作でタブ一覧へフォーカスが移ったときにアクティブになり、
        // それ以外の場所のクリックで非アクティブになる。
        Application.AddMessageFilter(new TabStripFocusFilter(this));
        _tabs.GotFocus += (_, _) => _tabStripKeyActive = true;
        _tabs.LostFocus += (_, _) => _tabStripKeyActive = false;
        foreach (var page in new[] { _sourceTab, _cutTab, _chapterTab, _outputTab, _logTab, _specTab })
        {
            page.SizeChanged += (_, _) => UpdateTabPageLayouts();
        }
        _tabs.SizeChanged += (_, _) => UpdateTabPageLayouts();

        // ---- 下部バー ----
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Size = new Size(initialClientWidth, 48),
            BackColor = Theme.PanelBack,
        };
        _jobAnimationStream = LoadJobAnimationStream();
        _jobAnimation = new PictureBox
        {
            Location = new Point(10, 4),
            Size = new Size(73, 40),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.PanelBack,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Visible = false,
        };
        if (_jobAnimationStream != null) _jobAnimation.Image = Image.FromStream(_jobAnimationStream);
        bottom.Controls.Add(_jobAnimation);
        // 経過時間と終了予測は、離して色も分けて並べる (終了予測は明るい紫苑色)。
        _jobElapsedLabel = Theme.MakeLabel("", 91, 10, 170, 26);
        _jobElapsedLabel.Font = new Font(_jobElapsedLabel.Font.FontFamily, 10.5f, FontStyle.Bold);
        _jobElapsedLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        bottom.Controls.Add(_jobElapsedLabel);
        _jobEtaLabel = Theme.MakeLabel("", 285, 10, 400, 26);
        _jobEtaLabel.Font = new Font(_jobEtaLabel.Font.FontFamily, 10.5f, FontStyle.Bold);
        _jobEtaLabel.ForeColor = Color.FromArgb(150, 128, 205);
        _jobEtaLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        bottom.Controls.Add(_jobEtaLabel);
        _statusToolTip = new ToolTip();
        _statusLabel = Theme.MakeLabel("待機中", 585, 11, 390, 24);
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        bottom.Controls.Add(_statusLabel);
        _cancelButton = Theme.MakeButton("キャンセル", 985, 8, 105, 30);
        _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelCurrentJob();
        bottom.Controls.Add(_cancelButton);
        // ラベルは背面色で不透明に描かれるため、待機中にステータス欄の左端と重なる経過時間・終了予測のラベルに隠れないよう、ステータス欄を最前面に置く。
        bottom.Controls.SetChildIndex(_statusLabel, 0);
        bottom.Resize += (_, _) => LayoutBottomBar();
        LayoutBottomBar();

        Controls.Add(_tabs);
        Controls.Add(bottom);
        Controls.Add(header);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
        // Dock したコントロールは z オーダーの逆順にレイアウトされる。
        Controls.SetChildIndex(_tabs, 0);
        Controls.SetChildIndex(bottom, 1);
        Controls.SetChildIndex(header, 2);
        Controls.SetChildIndex(menuStrip, 3);

        _frameIndexTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _frameIndexTimer.Tick += (_, _) => CompleteFrameIndexBuild();

        _mpvPumpTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _mpvPumpTimer.Tick += (_, _) =>
        {
            // タイマー内の例外は未処理例外ダイアログになるため、ここで受け止めてログに残す (同じ内容は 1 回だけ)。
            try
            {
                ReadMpvMessages();
                ProcessMpvPendingEvents();
            }
            catch (Exception ex)
            {
                if (ex.Message != _mpvPumpLastError)
                {
                    _mpvPumpLastError = ex.Message;
                    WriteLog($"mpv メッセージ処理エラー: {ex.Message}");
                }
            }
        };
        _previewTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _previewTimer.Tick += (_, _) => { UpdatePreviewLandingSnap(); UpdatePreviewPositionLabel(); };
        _previewTimer.Start();

        _previewDragTimer = new System.Windows.Forms.Timer { Interval = 20 };
        _previewDragTimer.Tick += (_, _) =>
        {
            UpdatePreviewPointerDrag();
            UpdatePopupPointerDrag();
            UpdateSegmentPopupPointerDrag();
            UpdateClipPointerDrag();
        };
        _previewDragTimer.Start();

        // スペースキーは、動画編集 / チャプタータブでは再生 / 一時停止に使う。
        KeyPreview = true;
        KeyDown += HandlePreviewSpaceKeyDown;
        KeyUp += HandlePreviewSpaceKeyUp;

        // ラベルと入力欄の間隔を、「画質 (crf):」の行にそろえる。
        InitializeLabelBoxGaps();

        // プレビュー列のレイアウトは、全コントロールの配置が終わったここから有効にする
        // (基準位置は最初のレイアウト時に記録する)。
        _previewLayoutReady = true;

        FormClosing += (_, e) =>
        {
            if (_jobRunning)
            {
                var result = MessageBox.Show("処理中です。終了して FFmpeg を停止しますか？", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes) { e.Cancel = true; return; }
                if (_currentProcess != null) { try { _currentProcess.Kill(); } catch { } }
                if (_cleanupFolder.Length > 0 && Directory.Exists(_cleanupFolder))
                {
                    try { Directory.Delete(_cleanupFolder, true); } catch { }
                }
            }
            CloseInputHistoryPopup();
            _previewTimer.Stop();
            _previewDragTimer.Stop();
            _mpvPumpTimer.Stop();
            StopFrameIndexBuild();
            if (_previewPopupForm is { IsDisposed: false }) { try { _previewPopupForm.Close(); } catch { } }
            CloseSegmentPreviewPopup();
            CloseClippingForm();
            StopMpvPlayer();
            SaveSettingsFromUi();
        };
        Shown += async (_, _) =>
        {
            if (_shownOnce) return;
            _shownOnce = true;
            ApplyResolvedPaths();
            EnsurePathBoxMargins();
            UpdateTabPageLayouts();
            // 生成時に割り当てたフォントは、コントロールの実体化の過程で外れてしまうため、表示された時点で割り当て直す。
            ApplySpecTextFonts();
            WriteStartupLogs();
            await CheckTrimSupportAsync();
            // 起動時にファイルが渡されたら (関連付けやドラッグなど) 、そのまま読み込む。
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1]))
            {
                _inputPathBox.Text = args[1];
                await LoadInputFileAsync(args[1]);
            }
        };
    }

    // ------------------------------------------------------------------
    // 基本動作
    // ------------------------------------------------------------------

    private TabPage MakeTabPage(string text)
    {
        return new TabPage(text) { BackColor = Theme.PageBack };
    }

    private Label MakePlaceholderLabel(string text)
    {
        var label = Theme.MakeLabel(text, 14, 14, 700, 26);
        label.ForeColor = Theme.MutedFore;
        return label;
    }

    private TextBox MakeLogTextBox()
    {
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = Color.FromArgb(23, 22, 29),
            ForeColor = Color.FromArgb(203, 198, 216),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Location = new Point(12, 29),
            Size = new Size(989, 638),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Theme.ApplyDarkScrollBars(box);
        return box;
    }

    private static string LoadSpecText()
    {
        try
        {
            var assembly = typeof(MainForm).Assembly;
            using var stream = assembly.GetManifestResourceStream("AsterCut.Spec.txt");
            if (stream == null) return "仕様テキストを読み込めませんでした。";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // テキストボックスは CRLF でないと改行として扱わないため、改行コードをそろえる。
            return reader.ReadToEnd().Trim().Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        catch (Exception ex)
        {
            return "仕様テキストを読み込めませんでした。\r\n" + ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // 仕様・使い方の項目ジャンプとキーワード検索 (ページ内検索)
    // ------------------------------------------------------------------

    /// <summary>本文から 【…】 の大きな項目を拾い、選択ボックスへ並べる。先頭はダミーの「項目を選択」。</summary>
    private void BuildSpecSectionList()
    {
        _specSectionIndices.Clear();
        _specSectionCombo.Items.Clear();
        _specSectionCombo.Items.Add("項目を選択");
        _specSectionIndices.Add(-1);
        var text = _specTextBox.Text;
        var index = 0;
        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = text.Length;
            var line = text.Substring(index, lineEnd - index).Trim();
            if (line.StartsWith("【") && line.Contains('】'))
            {
                _specSectionCombo.Items.Add(line);
                _specSectionIndices.Add(index);
            }
            index = lineEnd + 1;
        }
        _specSectionCombo.SelectedIndex = 0;
    }

    /// <summary>項目ジャンプの選択ボックスの項目を、項目の枠の上下中央に描く。</summary>
    private static void DrawSpecSectionComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var isEditPart = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        // 背景は OS の選択色を使わず、テーマの色で塗る。フォーカスの点線は描かない。
        var backColor = !isEditPart && isSelected ? Color.FromArgb(86, 74, 110) : combo.BackColor;
        using (var brush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }
        if (e.Index >= 0)
        {
            var text = combo.Items[e.Index]?.ToString() ?? "";
            var textSize = TextRenderer.MeasureText(text, combo.Font);
            var y = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - textSize.Height) / 2);
            var bounds = new Rectangle(e.Bounds.Left + 2, y, e.Bounds.Width - 2, textSize.Height);
            TextRenderer.DrawText(e.Graphics, text, combo.Font, bounds, e.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>選択された項目の先頭が表示欄の一番上に来るようスクロールする。</summary>
    private void JumpToSpecSection()
    {
        var selected = _specSectionCombo.SelectedIndex;
        if (selected < 0 || selected >= _specSectionIndices.Count) return;
        var target = _specSectionIndices[selected];
        if (target < 0) return;
        _specTextBox.Select(0, 0);
        _specTextBox.ScrollToCaret();
        var line = _specTextBox.GetLineFromCharIndex(target);
        SendMessage(_specTextBox.Handle, 0x00B6, IntPtr.Zero, (IntPtr)line); // EM_LINESCROLL
        _specTextBox.Select(target, 0);
    }

    private static readonly Color SpecSearchMatchBack = Color.FromArgb(96, 82, 8);
    private static readonly Color SpecSearchCurrentBack = Color.FromArgb(194, 108, 0);

    private static bool IsSpecJapaneseChar(char c)
    {
        return (c >= 'ぁ' && c <= 'ヿ')       // ひらがな・カタカナ
            || (c >= '一' && c <= '鿿')       // 漢字
            || (c >= '！' && c <= 'ﾟ')       // 全角英数・記号、半角カタカナ
            || (c >= '　' && c <= '〿');      // CJK の記号 (、。・「」など)
    }

    /// <summary>
    /// 設定にあるフォント名を、インストール済みのフォント名へ解決する。無ければ既定のフォント名を返す。
    /// フォント名は英語名・日本語名のどちらで書かれていても照合する。
    /// </summary>
    private static string GetSpecFontName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var target = name.Trim();
        try
        {
            using var collection = new System.Drawing.Text.InstalledFontCollection();
            foreach (var family in collection.Families)
            {
                if (SpecFontNameMatches(family, target)) return family.Name;
            }
            return fallback;
        }
        catch
        {
            return name;
        }
    }

    private static bool SpecFontNameMatches(System.Drawing.FontFamily family, string target)
    {
        if (string.Equals(family.Name.Trim(), target, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var lcid in new[] { 0x0409, 0x0411 })
        {
            try
            {
                if (string.Equals(family.GetName(lcid).Trim(), target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // その言語の名前を持たないフォントでは次を試す。
            }
        }
        return false;
    }

    /// <summary>
    /// RichTextBox はフォントの当て方を自動で決めてしまい、指定したフォントのまま表示されないことがあるため、
    /// 英数字は Consolas、日本語はオプションで選ばれたフォントと、文字種ごとに明示的に割り当てる。
    /// </summary>
    private void ApplySpecTextFonts()
    {
        var text = _specTextBox.Text;
        using var latinFont = new Font("Consolas", 9f);
        using var japaneseFont = new Font(GetSpecFontName(_settings.SpecJapaneseFontName, "Yu Gothic"), 9f);

        // 割り当てのたびに選択位置へ表示が追従してスクロールしてしまうため、
        // いま見えている先頭の文字を覚えておき、終わったら表示位置をそこへ戻す。
        var restoreView = _specTextBox.IsHandleCreated;
        var topChar = restoreView ? _specTextBox.GetCharIndexFromPosition(new Point(2, 2)) : 0;
        SetSpecTextRedraw(false);
        try
        {
            var i = 0;
            while (i < text.Length)
            {
                var japanese = IsSpecJapaneseChar(text[i]);
                var j = i + 1;
                while (j < text.Length && IsSpecJapaneseChar(text[j]) == japanese) j++;
                _specTextBox.Select(i, j - i);
                _specTextBox.SelectionFont = japanese ? japaneseFont : latinFont;
                i = j;
            }
            _specTextBox.Select(0, 0);
            if (restoreView)
            {
                _specTextBox.ScrollToCaret();
                var topLine = _specTextBox.GetLineFromCharIndex(topChar);
                if (topLine > 0) SendMessage(_specTextBox.Handle, 0x00B6, IntPtr.Zero, (IntPtr)topLine); // EM_LINESCROLL
                _specTextBox.Select(topChar, 0);
            }
        }
        finally
        {
            SetSpecTextRedraw(true);
        }
    }

    private void SetSpecTextRedraw(bool enable)
    {
        if (!_specTextBox.IsHandleCreated) return;
        SendMessage(_specTextBox.Handle, 0x000B, (IntPtr)(enable ? 1 : 0), IntPtr.Zero);
        if (enable) _specTextBox.Invalidate();
    }

    /// <summary>キーワードが変わるたびに、一致箇所をすべてハイライトし直して最初の一致へ移動する。</summary>
    private void RefreshSpecSearch()
    {
        _specSearchMatches.Clear();
        _specSearchCurrent = -1;
        SetSpecTextRedraw(false);
        try
        {
            // 前回のハイライトを消す (フォントの割り当てはそのまま残る)。
            _specTextBox.SelectAll();
            _specTextBox.SelectionBackColor = _specTextBox.BackColor;
            _specTextBox.SelectionColor = _specTextBox.ForeColor;
            _specTextBox.Select(0, 0);

            var keyword = _specSearchBox.Text;
            if (keyword.Length > 0)
            {
                var text = _specTextBox.Text;
                var index = 0;
                while (index < text.Length)
                {
                    var found = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    _specSearchMatches.Add(found);
                    index = found + 1;
                }
                foreach (var match in _specSearchMatches)
                {
                    _specTextBox.Select(match, keyword.Length);
                    _specTextBox.SelectionBackColor = SpecSearchMatchBack;
                }
                if (_specSearchMatches.Count > 0)
                {
                    _specSearchCurrent = 0;
                    ApplySpecSearchCurrent();
                }
            }
        }
        finally
        {
            SetSpecTextRedraw(true);
        }
        UpdateSpecSearchCountLabel();
        if (_specSearchCurrent >= 0) _specTextBox.ScrollToCaret();
    }

    private void ApplySpecSearchCurrent()
    {
        if (_specSearchCurrent < 0 || _specSearchCurrent >= _specSearchMatches.Count) return;
        var start = _specSearchMatches[_specSearchCurrent];
        _specTextBox.Select(start, _specSearchBox.Text.Length);
        _specTextBox.SelectionBackColor = SpecSearchCurrentBack;
        _specTextBox.Select(start, _specSearchBox.Text.Length);
    }

    private void MoveSpecSearchMatch(int direction)
    {
        if (_specSearchMatches.Count == 0) return;
        var previous = _specSearchCurrent;
        _specSearchCurrent = (_specSearchCurrent + direction + _specSearchMatches.Count) % _specSearchMatches.Count;
        SetSpecTextRedraw(false);
        try
        {
            if (previous >= 0 && previous != _specSearchCurrent)
            {
                _specTextBox.Select(_specSearchMatches[previous], _specSearchBox.Text.Length);
                _specTextBox.SelectionBackColor = SpecSearchMatchBack;
            }
            ApplySpecSearchCurrent();
        }
        finally
        {
            SetSpecTextRedraw(true);
        }
        _specTextBox.ScrollToCaret();
        UpdateSpecSearchCountLabel();
    }

    private void UpdateSpecSearchCountLabel()
    {
        if (_specSearchBox.Text.Length == 0)
        {
            _specSearchCountLabel.Text = "";
        }
        else if (_specSearchMatches.Count == 0)
        {
            _specSearchCountLabel.Text = "見つかりません";
        }
        else
        {
            _specSearchCountLabel.Text = $"{_specSearchCurrent + 1} / {_specSearchMatches.Count}";
        }
    }

    private void Tabs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _tabs.TabPages.Count) return;
        var isSelected = e.Index == _tabs.SelectedIndex;
        var fill = isSelected ? Theme.Accent : Color.FromArgb(46, 43, 56);
        var fore = isSelected ? Color.White : Color.FromArgb(192, 186, 204);
        using (var brush = new SolidBrush(fill))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }
        TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index]?.Text ?? "", _tabs.Font, e.Bounds, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (isSelected)
        {
            using var pen = new Pen(Color.FromArgb(150, 128, 205), 2f);
            e.Graphics.DrawLine(pen, e.Bounds.Left + 5, e.Bounds.Bottom - 2, e.Bounds.Right - 5, e.Bounds.Bottom - 2);
        }
    }

    private void UpdateTabPageLayouts()
    {
        const int edge = 10;
        if (_sourceTab.ClientSize.Width >= 400)
        {
            _sourceInfoGroup.SetBounds(edge, 12, _sourceTab.ClientSize.Width - edge * 2, 342);
            _audioGroup.SetBounds(edge, 366, _sourceTab.ClientSize.Width - edge * 2, 220);
        }
        if (_logTab.ClientSize.Width >= 400 && _logTab.ClientSize.Height >= 300)
        {
            _logGroup.SetBounds(edge, 44, _logTab.ClientSize.Width - edge * 2, Math.Max(100, _logTab.ClientSize.Height - 44 - edge));
        }
        if (_specTab.ClientSize.Width >= 400 && _specTab.ClientSize.Height >= 300)
        {
            _specGroup.SetBounds(edge, 12, _specTab.ClientSize.Width - edge * 2, Math.Max(100, _specTab.ClientSize.Height - 12 - edge));
        }
        if (_outputTab.ClientSize.Width >= 400)
        {
            _outputPathGroup.SetBounds(edge, 12, _outputTab.ClientSize.Width - edge * 2, 219);
            _editSummaryGroup.SetBounds(edge, 239, _outputTab.ClientSize.Width - edge * 2, 146);
            _encodeSettingsGroup.SetBounds(edge, 393, _outputTab.ClientSize.Width - edge * 2, 218);
        }
        if (_cutTab.ClientSize.Width >= 700 && _cutTab.ClientSize.Height >= 300)
        {
            // 区間一覧のグループは、ページの右端・下端まで広げてウィンドウサイズに追従させる。
            var segmentGroupX = 520 + GetPreviewColumnExtra().Width;
            _segmentGroup.SetBounds(segmentGroupX, 143,
                Math.Max(300, Math.Min(MaxSegmentGroupWidth, _cutTab.ClientSize.Width - segmentGroupX - edge)),
                Math.Max(120, _cutTab.ClientSize.Height - 143 - edge));
            UpdateSegmentListBounds();
        }
        if (_chapterTab.ClientSize.Width >= 700 && _chapterTab.ClientSize.Height >= 300)
        {
            var chapterGroupX = 520 + GetPreviewColumnExtra().Width;
            _chapterGroup.SetBounds(chapterGroupX, 145,
                Math.Max(300, Math.Min(MaxSegmentGroupWidth, _chapterTab.ClientSize.Width - chapterGroupX - edge)),
                Math.Max(120, _chapterTab.ClientSize.Height - 145 - edge));
            UpdateChapterListBounds();
        }
        UpdatePreviewColumnLayout();
    }

    // 区間一覧 (グループ) の幅の上限。これ以上は一覧に回さず、増分はすべてプレビューへ回す。
    private const int MaxSegmentGroupWidth = 740;

    /// <summary>ウィンドウの最小サイズからの増分を、プレビューへ割り当てる量に換算する (横は7割、縦はすべて。一覧が最大幅に達した分は横もすべてプレビューへ回す)。</summary>
    private (int Width, int Height) GetPreviewColumnExtra()
    {
        var growth = Math.Max(0, Width - MinimumSize.Width);
        var width = (int)Math.Floor(growth * 0.7);
        var height = Math.Max(0, Height - MinimumSize.Height);
        if (_cutTab != null && _cutTab.ClientSize.Width > 0)
        {
            // 区間一覧 (グループ) の幅が上限を超える分は、プレビューへ回す。
            width = Math.Max(width, _cutTab.ClientSize.Width - 520 - 10 - MaxSegmentGroupWidth);
        }
        return (width, height);
    }

    // 区間一覧の枠の角の半径。カードの角 (半径6) と同心になるよう、カードまでの距離 (3) を足す。
    private const int SegmentListFrameCornerRadius = 9;

    // 一覧の角も、枠の角丸の内側に沿う角丸で切り取る (一覧の四角い角が枠線に重なって
    // 角丸を切って見せないようにするため)。半径は、枠の角と同心になるよう
    // 一覧の位置 (1, 1) の分だけ小さい。
    private const int SegmentListCornerRadius = 8;

    private void UpdateSegmentListRegion()
    {
        if (_segmentList.Width <= 0 || _segmentList.Height <= 0) return;
        using var path = CreateRoundedRectPath(new Rectangle(0, 0, _segmentList.Width - 1, _segmentList.Height - 1), SegmentListCornerRadius);
        var oldRegion = _segmentList.Region;
        _segmentList.Region = new Region(path);
        oldRegion?.Dispose();
    }

    private void UpdateSegmentListBounds()
    {
        _segmentListFrame.SetBounds(12, 28,
            Math.Max(100, _segmentGroup.ClientSize.Width - 24),
            Math.Max(60, _segmentGroup.ClientSize.Height - 28 - 12));
        // ファイル数ラベルは、見出しの文の直後からパネル右端 (余白 12px) までを使う。
        var totalLeft = 12 + _segmentTitleLabel.PreferredWidth + 16;
        _segmentTotalLabel.SetBounds(totalLeft, 2, Math.Max(60, _segmentGroup.ClientSize.Width - totalLeft - 12), 23);
    }

    /// <summary>
    /// プレビューとその周辺・右側のコントロールを、ウィンドウサイズに応じて配置する。
    /// 基準位置は初回に記録し、以降は基準位置に増分を加えて配置する。
    /// </summary>
    private void UpdatePreviewColumnLayout()
    {
        if (!_previewLayoutReady) return;
        if (_previewLayoutBase.Count == 0)
        {
            foreach (var page in new[] { _cutTab, _chapterTab })
            {
                foreach (Control control in page.Controls)
                {
                    _previewLayoutBase.TryAdd(control, control.Bounds);
                }
            }
        }
        var extra = GetPreviewColumnExtra();
        // 中央揃えの行は、行全体の横幅がプレビュー列の中央に来るよう、基準位置からのずらし量を求める。
        var centeredDelta = new Dictionary<Control, int>();
        if (_previewLayoutBase.TryGetValue(_mpvHostPanel, out var columnBase))
        {
            var columnWidth = columnBase.Width + extra.Width;
            foreach (var row in _previewCenteredRows)
            {
                var rowLeft = int.MaxValue;
                var rowRight = int.MinValue;
                foreach (var control in row)
                {
                    if (!_previewLayoutBase.TryGetValue(control, out var b)) continue;
                    rowLeft = Math.Min(rowLeft, b.Left);
                    rowRight = Math.Max(rowRight, b.Right);
                }
                if (rowRight == int.MinValue) continue;
                var delta = columnBase.Left + (columnWidth - (rowRight - rowLeft)) / 2 - rowLeft;
                foreach (var control in row) centeredDelta[control] = delta;
            }
        }
        foreach (var page in new[] { _cutTab, _chapterTab })
        foreach (Control control in page.Controls)
        {
            if (!_previewLayoutBase.TryGetValue(control, out var baseBounds)) continue;
            if (control == _segmentGroup || control == _chapterGroup) continue;
            if (control == _mpvHostPanel)
            {
                control.SetBounds(baseBounds.Left, baseBounds.Top, baseBounds.Width + extra.Width, baseBounds.Height + extra.Height);
            }
            else if (baseBounds.Left >= 520)
            {
                if (control.Left != baseBounds.Left + extra.Width) control.Left = baseBounds.Left + extra.Width;
            }
            else
            {
                var newTop = baseBounds.Top > 297 ? baseBounds.Top + extra.Height : baseBounds.Top;
                var newWidth = control == _seekTrackBar
                    ? baseBounds.Width + extra.Width
                    : baseBounds.Width;
                var newLeft = baseBounds.Left + (centeredDelta.TryGetValue(control, out var delta) ? delta : 0);
                if (control.Left != newLeft || control.Top != newTop || control.Width != newWidth)
                {
                    control.SetBounds(newLeft, newTop, newWidth, baseBounds.Height);
                }
            }
        }
        if (_previewLayoutBase.TryGetValue(_mpvHostPanel, out var panelBase))
        {
            _mpvHostPanelHomeBounds = new Rectangle(panelBase.Left, panelBase.Top, panelBase.Width + extra.Width, panelBase.Height + extra.Height);
        }
    }

    /// <summary>映像パネルを、ポップアップから戻すときの位置・大きさ (現在のレイアウトどおり) にする。</summary>
    private void RestoreMpvHostPanelHome()
    {
        _mpvHostPanel.Location = _mpvHostPanelHomeBounds.Location;
        _mpvHostPanel.Size = _mpvHostPanelHomeBounds.Size;
    }

    // ------------------------------------------------------------------
    // ログと状態
    // ------------------------------------------------------------------

    public void WriteLog(string message)
    {
        if (_logTextBox.IsDisposed) return;
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        _logTextBox.AppendText($"[{stamp}] {message}\r\n");
        AppendOutputLogPopupLine($"[{stamp}] {message}");
        var lines = _logTextBox.Lines;
        if (lines.Length > 1500)
        {
            _logTextBox.Lines = lines[(lines.Length - 1000)..];
        }
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (_logTextBox.IsDisposed) return;
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.SelectionLength = 0;
        _logTextBox.ScrollToCaret();
    }

    // 下部バーの配置: 経過時間と終了予測は実測幅で左に置き、ステータス欄は残り幅いっぱいに広げる (プログレスバーがなくなった分、メッセージを長く表示できる)。出力中だけは終了予測の右までを空け、待機中はアニメーションの右から使える。
    private void LayoutBottomBar()
    {
        _cancelButton.Location = new Point(Math.Max(0, _cancelButton.Parent!.ClientSize.Width - 14 - _cancelButton.Width), _cancelButton.Top);
        var elapsedWidth = TextRenderer.MeasureText("経過 0:00:00", _jobElapsedLabel.Font).Width + 8;
        _jobElapsedLabel.Size = new Size(elapsedWidth, 26);
        var etaWidth = TextRenderer.MeasureText("終了予測 あと約 0:00:00 (00:00 終了)", _jobEtaLabel.Font).Width + 16;
        _jobEtaLabel.Location = new Point(_jobElapsedLabel.Right + 24, 10);
        _jobEtaLabel.Size = new Size(etaWidth, 26);
        var minStatusWidth = TextRenderer.MeasureText("プレビュー準備完了", _statusLabel.Font).Width + 10;
        var leftLimit = _jobRunning ? _jobEtaLabel.Right + 12 : _jobAnimation.Right + 12;
        var statusWidth = Math.Max(minStatusWidth, _cancelButton.Left - 8 - leftLimit);
        _statusLabel.Location = new Point(_cancelButton.Left - 8 - statusWidth, 11);
        _statusLabel.Size = new Size(statusWidth, 24);
    }

    // 出力中アニメーション (紫苑カット) の GIF を埋め込みリソースから読み込む。アニメーション表示には元のストリームが必要なため、呼び出し側で保持する。
    private static MemoryStream? LoadJobAnimationStream()
    {
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AsterCut.aster_cut.gif");
        if (stream == null) return null;
        var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }

    public void SetStatus(string message)
    {
        _statusLabel.Text = message;
        _statusToolTip.SetToolTip(_statusLabel, message);
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(_logTextBox.Text);
            SetStatus("ログをコピーしました");
        }
        catch { /* クリップボードが使えない場合は何もしない */ }
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            FileName = "AsterCut-log.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, _logTextBox.Text, new UTF8Encoding(true));
            SetStatus("ログを保存しました");
        }
        catch (Exception ex)
        {
            WriteLog($"ログ保存エラー: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // ヘッダーまわり
    // ------------------------------------------------------------------

    private void ApplyResolvedPaths()
    {
        _ffmpegPath = ExecutableFinder.Find("ffmpeg.exe", _settings.FFmpegPath);
        _ffprobePath = ExecutableFinder.Find("ffprobe.exe", _settings.FFprobePath);
        // ffmpeg と ffprobe は通常同じフォルダーに置かれるため、片方が見つかれば隣も探す。
        if (_ffprobePath.Length == 0 && _ffmpegPath.Length > 0)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(_ffmpegPath) ?? "", "ffprobe.exe");
            if (File.Exists(sibling)) _ffprobePath = sibling;
        }
        if (_ffmpegPath.Length == 0 && _ffprobePath.Length > 0)
        {
            var sibling = Path.Combine(Path.GetDirectoryName(_ffprobePath) ?? "", "ffmpeg.exe");
            if (File.Exists(sibling)) _ffmpegPath = sibling;
        }
        _mpvPath = ExecutableFinder.Find("mpv.exe", _settings.MpvPath);
    }

    private void WriteStartupLogs()
    {
        WriteLog("起動完了。出力は「動画出力」タブ、診断情報は「実行ログ」タブ、仕様・操作方法は「仕様・使い方」タブで確認できます。");
        if (_ffmpegPath.Length == 0)
        {
            WriteLog("ffmpeg.exe が見つかりません。「ツール」メニューの「オプション」で、既存の FFmpeg bin フォルダー内の実行ファイルを指定してください。");
        }
        if (_ffprobePath.Length == 0)
        {
            WriteLog("ffprobe.exe が見つかりません。「ツール」メニューの「オプション」で、既存の FFmpeg bin フォルダー内の実行ファイルを指定してください。");
        }
        if (_mpvPath.Length == 0)
        {
            WriteLog("mpv.exe が見つかりません。プレビューには mpv.exe が必要です。「ツール」メニューの「オプション」でパスを設定してください。");
        }
    }

    // ドロップされたファイルを開く (入力欄とプレビュー画面で共通)。
    private void OpenDroppedInputFile(string file)
    {
        // プロジェクトファイルは、動画ではなく編集プロジェクトとして開く。
        if (string.Equals(Path.GetExtension(file), ".acproj", StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenEditProjectFileAsync(file);
            return;
        }
        _inputPathBox.Text = file;
        _ = LoadInputFileAsync(file);
    }

    private void BrowseInputFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "動画ファイル|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.wmv;*.flv;*.acproj|すべてのファイル (*.*)|*.*",
            Title = "入力動画を選択",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // プロジェクトファイルは、動画ではなく編集プロジェクトとして開く。
            if (string.Equals(Path.GetExtension(dialog.FileName), ".acproj", StringComparison.OrdinalIgnoreCase))
            {
                _ = OpenEditProjectFileAsync(dialog.FileName);
                return;
            }
            _inputPathBox.Text = dialog.FileName;
            _ = LoadInputFileAsync(dialog.FileName);
        }
    }

    private bool BrowseExeFile(TextBox target, string fileName)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = fileName,
            Title = $"{fileName} を選択",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        target.Text = dialog.FileName;
        return true;
    }

    // ------------------------------------------------------------------
    // 入力履歴ポップアップ (ホバーで右端に × が出て、その場で削除できる)
    // ------------------------------------------------------------------

    private static Rectangle GetHistoryDeleteRect(Rectangle itemBounds)
    {
        return new Rectangle(itemBounds.Right - 28, itemBounds.Y, 26, itemBounds.Height);
    }

    private void CloseInputHistoryPopup()
    {
        var popup = _historyPopup;
        _historyPopup = null;
        _historyListBox = null;
        _historyHoverIndex = -1;
        _historyHoverDelete = false;
        if (popup is { IsDisposed: false })
        {
            try { popup.Close(); } catch { /* 閉じ済みなら何もしない */ }
        }
    }

    private void RefreshInputHistoryList()
    {
        var list = _historyListBox;
        if (list == null || list.IsDisposed) return;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            if (_historyEntries.Count == 0)
            {
                list.Items.Add("（履歴はありません）");
            }
            else
            {
                foreach (var path in _historyEntries) list.Items.Add(path);
            }
        }
        finally
        {
            list.EndUpdate();
        }
        _historyHoverIndex = -1;
        _historyHoverDelete = false;
        if (_historyPopup is { IsDisposed: false })
        {
            _historyPopup.Height = Math.Max(1, list.Items.Count) * list.ItemHeight + 2;
        }
        list.Invalidate();
    }

    private void RemoveInputHistory(string path)
    {
        var removed = _historyEntries.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) SaveSettingsFromUi();
    }

    private void ShowInputHistoryPopup()
    {
        _historyEntries = _settings.InputHistory;
        _historyTargetBox = _inputPathBox;
        _historyAnchorButton = _historyButton;
        _historyPicked = path => { _inputPathBox.Text = path; _ = LoadInputFileAsync(path); };
        ShowHistoryPopupCore();
    }

    private void ShowChapterHistoryPopup()
    {
        _historyEntries = _settings.ChapterInputHistory;
        _historyTargetBox = _chapterPathBox;
        _historyAnchorButton = _chapterHistoryButton;
        _historyPicked = path => { _chapterPathBox.Text = path; LoadChapterFile(path); };
        ShowHistoryPopupCore();
    }

    private void ShowHistoryPopupCore()
    {
        // すでに開いている場合は閉じる (▼ボタンのトグル動作)。
        if (_historyPopup is { IsDisposed: false })
        {
            CloseInputHistoryPopup();
            return;
        }

        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        _historyPopup = popup;

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Font = Font,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            HorizontalScrollbar = false,
        };
        list.ItemHeight = Math.Max(24, list.Font.Height + 7);
        _historyListBox = list;
        popup.Controls.Add(list);

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var isPlaceholder = _historyEntries.Count == 0;
            var isHover = e.Index == _historyHoverIndex;
            var background = isHover && !isPlaceholder ? Color.FromArgb(58, 54, 72) : Theme.BoxBack;
            using (var brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            var textColor = isPlaceholder ? Theme.MutedFore : Theme.BoxFore;
            // ホバー中は右端の「×」の分だけ文字列の幅を狭め、パスが「×」の下に隠れないようにする。
            var textWidth = Math.Max(10, e.Bounds.Width - (isHover && !isPlaceholder ? 40 : 12));
            var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, textWidth, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, (string)list.Items[e.Index], list.Font, textRect, textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis);
            if (isHover && !isPlaceholder)
            {
                var deleteRect = GetHistoryDeleteRect(e.Bounds);
                if (_historyHoverDelete)
                {
                    using var brush = new SolidBrush(Color.FromArgb(178, 54, 44));
                    e.Graphics.FillRectangle(brush, deleteRect);
                }
                var deleteColor = _historyHoverDelete ? Color.White : Theme.MutedFore;
                TextRenderer.DrawText(e.Graphics, "×", list.Font, deleteRect, deleteColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine);
            }
        };
        list.MouseMove += (_, e) =>
        {
            var index = list.IndexFromPoint(e.Location);
            var overDelete = false;
            if (index >= 0 && _historyEntries.Count > 0)
            {
                try { overDelete = GetHistoryDeleteRect(list.GetItemRectangle(index)).Contains(e.Location); }
                catch { /* 範囲外なら false */ }
            }
            if (index != _historyHoverIndex || overDelete != _historyHoverDelete)
            {
                var previousIndex = _historyHoverIndex;
                _historyHoverIndex = index;
                _historyHoverDelete = overDelete;
                try { if (previousIndex >= 0) list.Invalidate(list.GetItemRectangle(previousIndex)); } catch { }
                try { if (index >= 0) list.Invalidate(list.GetItemRectangle(index)); } catch { }
            }
            list.Cursor = overDelete ? Cursors.Hand : Cursors.Default;
        };
        list.MouseLeave += (_, _) =>
        {
            var previousIndex = _historyHoverIndex;
            _historyHoverIndex = -1;
            _historyHoverDelete = false;
            try { if (previousIndex >= 0) list.Invalidate(list.GetItemRectangle(previousIndex)); } catch { }
        };
        list.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_historyEntries.Count == 0) return;
            var index = list.IndexFromPoint(e.Location);
            if (index < 0 || index >= list.Items.Count) return;
            var selectedPath = (string)list.Items[index];
            var overDelete = false;
            try { overDelete = GetHistoryDeleteRect(list.GetItemRectangle(index)).Contains(e.Location); }
            catch { /* 範囲外なら false */ }
            if (overDelete)
            {
                // 履歴だけを削除し、ポップアップは開いたままにする。動画ファイル自体は削除しない。
                RemoveInputHistory(selectedPath);
                RefreshInputHistoryList();
                return;
            }
            CloseInputHistoryPopup();
            _historyPicked(selectedPath);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseInputHistoryPopup();
            }
            else if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0 && _historyEntries.Count > 0)
            {
                e.Handled = true;
                var selectedPath = (string)list.Items[list.SelectedIndex];
                CloseInputHistoryPopup();
                _historyPicked(selectedPath);
            }
            else if (e.KeyCode == Keys.Delete && list.SelectedIndex >= 0 && _historyEntries.Count > 0)
            {
                e.Handled = true;
                RemoveInputHistory((string)list.Items[list.SelectedIndex]);
                RefreshInputHistoryList();
            }
        };
        popup.Deactivate += (_, _) =>
        {
            // ▼ボタンを押して閉じた場合は、直後に発生するボタンの Click で開き直さないよう印を付ける。
            try
            {
                if (!_historyAnchorButton.IsDisposed)
                {
                    var cursorInButton = _historyAnchorButton.PointToClient(Control.MousePosition);
                    if (_historyAnchorButton.ClientRectangle.Contains(cursorInButton))
                    {
                        _historyToggleSuppressedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    }
                }
            }
            catch { /* 判定できなくても閉じる */ }
            CloseInputHistoryPopup();
        };
        popup.FormClosed += (_, _) =>
        {
            if (_historyPopup == popup)
            {
                _historyPopup = null;
                _historyListBox = null;
                _historyHoverIndex = -1;
                _historyHoverDelete = false;
            }
        };

        popup.Width = Math.Max(240, _historyTargetBox.Width);
        RefreshInputHistoryList();
        var belowLeft = _historyTargetBox.PointToScreen(new Point(0, _historyTargetBox.Height + 2));
        var aboveLeft = _historyTargetBox.PointToScreen(new Point(0, -popup.Height - 2));
        var workingArea = Screen.FromControl(_historyTargetBox).WorkingArea;
        var popupX = belowLeft.X;
        if (popupX + popup.Width > workingArea.Right) popupX = workingArea.Right - popup.Width;
        if (popupX < workingArea.Left) popupX = workingArea.Left;
        var popupY = belowLeft.Y;
        if (popupY + popup.Height > workingArea.Bottom) popupY = aboveLeft.Y;
        if (popupY < workingArea.Top) popupY = workingArea.Top;
        popup.Location = new Point(popupX, popupY);
        popup.Show(this);
        list.Focus();
    }

    private void LoadInputRequested()
    {
        var path = _inputPathBox.Text.Trim();
        if (path.Length == 0)
        {
            SetStatus("入力動画を選択してください。");
            return;
        }
        _ = LoadInputFileAsync(path);
    }

    /// <summary>ffprobe のパス解決: 欄のパス → ffmpeg の隣 → エラー。</summary>
    private string GetFfprobePath()
    {
        var p = Environment.ExpandEnvironmentVariables(_ffprobePath.Trim().Trim('"'));
        if (p.Length > 0 && File.Exists(p)) return Path.GetFullPath(p);
        var ffmpeg = Environment.ExpandEnvironmentVariables(_ffmpegPath.Trim().Trim('"'));
        if (ffmpeg.Length > 0 && File.Exists(ffmpeg))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (File.Exists(sibling)) return sibling;
        }
        throw new InvalidOperationException("ffprobe.exe が見つかりません。「ツール」メニューの「オプション」でパスを設定してください。");
    }

    private async Task LoadInputFileAsync(string path)
    {
        if (_loadingInput) return;
        CloseSegmentPreviewPopup();
        CloseClippingForm();
        path = path.Trim().Trim('"');
        if (!File.Exists(path))
        {
            MessageBox.Show("入力ファイルが見つかりません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _loadingInput = true;
        try
        {
            SetStatus("情報を読み込み中...");
            StopFrameIndexBuild();
            _frameTimesUs = null;
            _frameKeyTimesUs = null;
            _inputPath = Path.GetFullPath(path);
            _inputPathBox.Text = _inputPath;
            _probe = null;
            _videoStreams = new List<ProbeStream>();
            _audioStreams = new List<ProbeStream>();
            _durationMs = 0;
            UpdateAudioList();
            UpdateSourceInfoPanel();
            UpdateDataBroadcastCheckBoxStyle();

            var ffprobe = GetFfprobePath();
            var probe = await Probe.InvokeAsync(ffprobe, _inputPath);
            _probe = probe;
            _videoStreams = probe.Streams.FindAll(s => s.CodecType == "video" && s.AttachedPic != 1);
            _audioStreams = probe.Streams.FindAll(s => s.CodecType == "audio");
            if (_videoStreams.Count == 0)
            {
                throw new InvalidOperationException("動画ストリームが見つかりません。動画ファイルを選択してください。");
            }
            var duration = Probe.ParseSeconds(probe.FormatDuration);
            if (duration <= 0 && _videoStreams.Count > 0) duration = Probe.ParseSeconds(_videoStreams[0].Duration);
            _durationMs = Math.Max(0L, (long)Math.Round(duration * 1000));

            UpdateAudioList();
            UpdateSourceInfoPanel();
            UpdateDataBroadcastCheckBoxStyle();
            ClearSegmentsForNewInput();
            _cutEndBox.Text = _durationMs > 0 ? TimeText.FormatClockFromMs(_durationMs) : "00:00:00.000";
            SetPreviewSource(_inputPath);
            SetOutputDefaults(_inputPath);
            _outputRunButton.Enabled = true;
            _encodeRunButton.Enabled = true;

            var fileInfo = new FileInfo(_inputPath);
            WriteLog($"入力を読み込み: {_inputPath}");
            WriteLog($"入力ファイルサイズ: {fileInfo.Length:N0} bytes; 更新日時: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            WriteLog($"コンテナ: {probe.FormatName}; ffprobe形式時間: {TimeText.FormatClockFromMs(_durationMs)}");
            WriteLog($"動画 {_videoStreams.Count} ストリーム / 音声 {_audioStreams.Count} ストリーム");
            foreach (var stream in probe.Streams)
            {
                WriteLog($"stream index={stream.Index}; type={stream.CodecType}; codec={stream.CodecName}; time_base={stream.TimeBase}; start_time={stream.StartTime}; duration={stream.Duration}; profile={stream.Profile}");
            }
            AddInputHistory(_inputPath);
            InitializeChaptersForInput(_inputPath);
            SetStatus("入力ファイル準備完了");
            UpdateFormTitle();
            StartFrameIndexBuild();
            // 読み込み直後はシークバーにフォーカスを移す (入力欄に残ったままだと、スペースキーで再生できないため)。
            _seekTrackBar.Focus();
        }
        catch (Exception ex)
        {
            UpdateSourceInfoPanel();
            SetStatus("読み込みエラー");
            WriteLog($"読み込みエラー: {ex.Message}");
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loadingInput = false;
        }
        _ = CheckTrimSupportAsync();
    }

    private void UpdateFormTitle()
    {
        Text = _inputPath.Length > 0 ? $"{AppTitleWithVersion} - {Path.GetFileName(_inputPath)}" : AppTitleWithVersion;
    }

    private void AddHistoryEntry(List<string> entries, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath;
        try { fullPath = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return; }
        if (fullPath.Length == 0) return;
        entries.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        entries.Insert(0, fullPath);
        while (entries.Count > 10) entries.RemoveAt(entries.Count - 1);
        SaveSettingsFromUi();
    }

    private void AddInputHistory(string path) => AddHistoryEntry(_settings.InputHistory, path);

    private void AddChapterInputHistory(string path) => AddHistoryEntry(_settings.ChapterInputHistory, path);

    private void UpdateAudioList()
    {
        _audioList.Items.Clear();
        var n = 0;
        foreach (var stream in _audioStreams)
        {
            var codec = stream.CodecName.Length > 0 ? stream.CodecName : "unknown codec";
            var channels = stream.Channels > 0 ? $" {stream.Channels}ch" : "";
            var lang = stream.Tag("language").Length > 0 ? $" / {stream.Tag("language")}" : "";
            var title = stream.Tag("title").Length > 0 ? $" / {stream.Tag("title")}" : "";
            _audioList.Items.Add($"Audio {n + 1}  |  {codec}{channels}{lang}{title}");
            _audioList.SetItemChecked(_audioList.Items.Count - 1, true);
            n++;
        }
        if (_audioStreams.Count == 0)
        {
            _audioList.Items.Add("(音声トラックなし)");
        }
    }

    private void UpdateSourceInfoPanel()
    {
        if (_inputPath.Length == 0 || _probe == null)
        {
            _sourceFileNameValue.Text = "未読み込み";
            _sourceToolTip.SetToolTip(_sourceFileNameValue, "未読み込み");
            _sourceContainerValue.Text = "—";
            _sourceDurationValue.Text = "—";
            _sourceFileSizeValue.Text = "—";
            _sourceFrameRateValue.Text = "—";
            _sourceCodecValue.Text = "—";
            _sourceMetadataTextBox.Text = "—";
            return;
        }

        var fileName = Path.GetFileName(_inputPath);
        _sourceFileNameValue.Text = fileName;
        _sourceToolTip.SetToolTip(_sourceFileNameValue, fileName);

        var containerName = _probe.FormatLongName;
        var containerId = _probe.FormatName;
        _sourceContainerValue.Text =
            containerName.Length > 0 && containerId.Length > 0 ? $"{containerName} ({containerId})" :
            containerName.Length > 0 ? containerName :
            containerId.Length > 0 ? containerId : "不明";

        _sourceDurationValue.Text = _durationMs > 0 ? TimeText.FormatClockFromMs(_durationMs) : "不明";
        if (_durationMs > 0 && TestFrameTableReady()) _sourceDurationValue.Text += $" ({_frameTimesUs!.Length}f)";
        try
        {
            var info = new FileInfo(_inputPath);
            _sourceFileSizeValue.Text = $"{(double)info.Length / 1048576:N2} MiB ({info.Length:N0} bytes)";
        }
        catch
        {
            _sourceFileSizeValue.Text = "取得できません";
        }

        var video = _videoStreams.Count > 0 ? _videoStreams[0] : null;
        var fps = 0.0;
        if (video != null)
        {
            fps = Probe.ConvertFrameRateToDouble(video.AvgFrameRate);
            if (fps <= 0) fps = Probe.ConvertFrameRateToDouble(video.RFrameRate);
        }
        if (fps <= 0 && _frameTimesUs is { Length: > 1 })
        {
            var spanUs = (double)_frameTimesUs[^1] - _frameTimesUs[0];
            if (spanUs > 0) fps = (_frameTimesUs.Length - 1) * 1000000.0 / spanUs;
        }
        var fpsText = fps > 0 ? $"{fps:N3} fps" : "フレームレート不明";
        string vfrStatus;
        if (_frameTimesUs is { Length: > 1 })
        {
            var stats = FrameIndex.IntervalStats(_frameTimesUs);
            vfrStatus = stats[1] - stats[0] > 1000 ? "VFR" : "CFR";
        }
        else if (_frameIndexTask == null && _frameIndexProcess == null)
        {
            vfrStatus = "判定不可";
        }
        else
        {
            vfrStatus = "VFR 判定中";
        }
        _sourceFrameRateValue.Text = $"{fpsText} / {vfrStatus}";

        if (video != null)
        {
            _sourceCodecValue.Text =
                video.CodecLongName.Length > 0 && video.CodecName.Length > 0 ? $"{video.CodecLongName} ({video.CodecName})" :
                video.CodecLongName.Length > 0 ? video.CodecLongName :
                video.CodecName.Length > 0 ? video.CodecName : "不明";
        }
        else
        {
            _sourceCodecValue.Text = "不明";
        }
        _sourceMetadataTextBox.Text = GetSourceEditorMetadataText();
    }

    private string GetSourceEditorMetadataText()
    {
        if (_probe == null) return "なし";
        if (_probe.FormatTags.TryGetValue("_ASTER_CUT", out var formatValue) && formatValue.Trim().Length > 0)
        {
            return formatValue;
        }
        foreach (var stream in _probe.Streams)
        {
            if (stream.Tags.TryGetValue("_ASTER_CUT", out var value) && value.Trim().Length > 0)
            {
                return value;
            }
        }
        return "なし";
    }

    private void ShowEditorMetadataWindow()
    {
        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = "_ASTER_CUT",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(650, 600),
            MinimumSize = new Size(400, 300),
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        var clientWidth = dialog.ClientSize.Width;
        var clientHeight = dialog.ClientSize.Height;
        var textBox = new TextBox
        {
            Location = new Point(12, 12),
            Size = new Size(clientWidth - 24, clientHeight - 68),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Text = _sourceMetadataTextBox.Text,
        };
        Theme.ApplyDarkScrollBars(textBox);
        dialog.Controls.Add(textBox);
        var copyButton = Theme.MakeButton("コピー", clientWidth - 260, clientHeight - 44, 120, 32);
        copyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(textBox.Text);
                SetStatus("メタデータをコピーしました");
            }
            catch { /* クリップボードが使えない場合は何もしない */ }
        };
        dialog.Controls.Add(copyButton);
        var closeButton = Theme.MakeButton("閉じる", clientWidth - 132, clientHeight - 44, 120, 32);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(closeButton);
        dialog.CancelButton = closeButton;
        // テキストボックスが初期フォーカスを受けると全文が選択されてしまうため、閉じるボタンに置く。
        dialog.ActiveControl = closeButton;
        Theme.ApplyDarkTitleBar(dialog);
        dialog.ShowDialog(this);
    }

    // ------------------------------------------------------------------
    // フレーム時刻テーブル (フレームインデックス)
    // ------------------------------------------------------------------

    private void StopFrameIndexBuild()
    {
        _frameIndexTimer.Stop();
        var process = _frameIndexProcess;
        _frameIndexProcess = null;
        _frameIndexTask = null;
        _frameIndexErrorTask = null;
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(); } catch { /* 終了済みなら何もしない */ }
            try { process.Dispose(); } catch { /* 同上 */ }
        }
    }

    private void StartFrameIndexBuild()
    {
        StopFrameIndexBuild();
        _frameTimesUs = null;
        _frameKeyTimesUs = null;
        if (_videoStreams.Count == 0) return;
        try
        {
            var ffprobe = GetFfprobePath();
            _frameBaseUs = (long)Math.Round(Probe.ParseSeconds(_probe?.FormatStartTime ?? "") * 1000000.0);
            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                WorkingDirectory = Path.GetDirectoryName(ffprobe) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[]
            {
                "-v", "error",
                "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
                "-show_entries", "packet=pts_time,dts_time,flags",
                "-of", "csv=p=0",
                _inputPath,
            })
            {
                startInfo.ArgumentList.Add(arg);
            }
            var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("ffprobe プロセスを起動できませんでした。");
            _frameIndexProcess = process;
            _frameIndexInputPath = _inputPath;
            _frameIndexTask = process.StandardOutput.ReadToEndAsync();
            _frameIndexErrorTask = process.StandardError.ReadToEndAsync();
            _frameIndexTimer.Start();
            UpdateSourceInfoPanel();
            WriteLog("フレーム時刻の解析を開始しました。完了までは fps から推定した位置で 1 フレーム移動します。");
        }
        catch (Exception ex)
        {
            UpdateSourceInfoPanel();
            WriteLog($"フレーム時刻の解析を開始できませんでした: {ex.Message}");
        }
    }

    private void CompleteFrameIndexBuild()
    {
        if (_frameIndexTask == null || _frameIndexErrorTask == null)
        {
            _frameIndexTimer.Stop();
            return;
        }
        if (!_frameIndexTask.IsCompleted || !_frameIndexErrorTask.IsCompleted) return;
        _frameIndexTimer.Stop();
        var outTask = _frameIndexTask;
        var process = _frameIndexProcess;
        var sourcePath = _frameIndexInputPath;
        _frameIndexTask = null;
        _frameIndexErrorTask = null;
        _frameIndexProcess = null;
        var exitCode = -1;
        var text = "";
        try { process?.WaitForExit(5000); exitCode = process?.ExitCode ?? -1; } catch { /* 取得できなければ -1 */ }
        try { text = outTask.Result; } catch { /* 空として扱う */ }
        try { process?.Dispose(); } catch { /* 同上 */ }
        if (sourcePath != _inputPath) return;
        if (exitCode != 0 || string.IsNullOrWhiteSpace(text))
        {
            UpdateSourceInfoPanel();
            WriteLog($"フレーム時刻の解析に失敗しました（終了コード {exitCode}）。fps から推定した位置で 1 フレーム移動します。");
            return;
        }
        try
        {
            var table = FrameIndex.ParsePacketTimes(text, _frameBaseUs);
            if (table.Length < 2)
            {
                UpdateSourceInfoPanel();
                WriteLog("フレーム時刻を取得できませんでした（有効なタイムスタンプが 2 個未満）。fps からの推定を使用します。");
                return;
            }
            _frameTimesUs = table;
            _frameKeyTimesUs = FrameIndex.ParseKeyframeTimes(text, _frameBaseUs);
            UpdateSourceInfoPanel();
            _segmentList?.Invalidate();
            UpdateMpvTimelineOffset();
            var stats = FrameIndex.IntervalStats(table);
            var minMs = stats[0] / 1000.0;
            var maxMs = stats[1] / 1000.0;
            WriteLog($"フレーム時刻テーブルを作成: {table.Length} フレーム / 先頭={TimeText.FormatClockFromMs((long)Math.Floor(table[0] / 1000.0))} / 最終={TimeText.FormatClockFromMs((long)Math.Floor(table[^1] / 1000.0))} / 間隔 最小={minMs:0.###} ms 最大={maxMs:0.###} ms / 基準(format.start_time)={_frameBaseUs / 1000.0:0.###} ms");
            if (minMs > 0 && maxMs > minMs * 1.5)
            {
                WriteLog("可変フレームレート、またはタイムスタンプのばらつきがあります。1 フレーム移動と開始・終了時刻は実際のフレーム時刻に合わせて動作します。");
            }
            WriteLog("これ以降、1 フレーム移動と「現在位置を開始／終了に設定」は実際のフレーム時刻に合わせます。");
        }
        catch (Exception ex)
        {
            UpdateSourceInfoPanel();
            WriteLog($"フレーム時刻テーブルの作成に失敗しました: {ex.Message}");
        }
    }

    private bool IsEditingInProgress()
    {
        if (_inputPath.Length == 0) return false;
        if (_segments.Count > 0) return true;
        if (Math.Abs(GetSelectedSpeed() - 1.0) >= 0.000001) return true;
        if (GetRotationIndex() != 0) return true;
        if (GetCurrentClipValues().HasClipping) return true;
        if (_audioStreams.Count > 0)
        {
            for (var i = 0; i < _audioStreams.Count; i++)
            {
                if (i < _audioList.Items.Count && !_audioList.GetItemChecked(i)) return true;
            }
        }
        if (_cutStartBox.Text.Trim() != "00:00:00.000") return true;
        if (OutputMode != 0) return true;
        if (_outputModeUserSelected) return true;
        if ((_profileCombo?.SelectedItem?.ToString() ?? "投稿用") != "投稿用") return true;
        if (GetSelectedVideoFormat() != "AVC") return true;
        if (GetSelectedBitDepth() != 8) return true;
        if (GetSelectedAudioFormat() != "AAC") return true;
        if (GetSelectedContainer() != "MP4") return true;
        if (_videoCrfBox.Text.Trim() != "18.0") return true;
        if (GetAudioBitrateKbpsOrDefault() != 320) return true;
        if (_appendTimestampCheckBox.Checked) return true;
        if (!_appendSuffixCheckBox.Checked) return true;
        if (!_keepMetadataCheckBox.Checked) return true;
        if (!_writeEditorMetadataCheckBox.Checked) return true;
        if (_outputDirIsCustom) return true;
        return false;
    }

    private void ResetToInitialState()
    {
        if (_jobRunning)
        {
            MessageBox.Show("処理中は「新規」を実行できません。処理が終わるか、キャンセルしてから実行してください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (IsEditingInProgress())
        {
            var answer = MessageBox.Show(
                "編集中の内容を破棄して、起動時の状態に戻しますか？\r\n\r\n出力していない編集内容は履歴に保存されず、元に戻せません。",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
        }
        CloseInputHistoryPopup();
        ResetPreviewState();
        StopFrameIndexBuild();
        _frameTimesUs = null;
        _frameKeyTimesUs = null;
        _frameIndexInputPath = "";
        _inputPath = "";
        _inputPathBox.Text = "";
        _probe = null;
        _videoStreams = new List<ProbeStream>();
        _audioStreams = new List<ProbeStream>();
        _durationMs = 0;
        UpdateDataBroadcastCheckBoxStyle();
        _audioList.Items.Clear();
        CloseSegmentPreviewPopup();
        CloseClippingForm();
        ClearSegmentsForNewInput();
        ClearChapters();
        _chapterStartBox.Text = "00:00:00.000";
        _chapterPathBox.Text = "";
        _chapterOutputPathBox.Text = "";
        _clipCurrent = new Segment();
        _cutStartBox.Text = "00:00:00.000";
        _cutEndBox.Text = "00:00:00.000";
        SetSpeedValue(1.0);
        _rotationCombo.SelectedIndex = 0;
        UpdateSegmentTotalLabel();
        UpdateSourceInfoPanel();
        // 出力設定
        _outputModeUserSelected = false;
        RefreshProfileCombo("投稿用");
        ApplySelectedProfile();
        _appendTimestampCheckBox.Checked = false;
        _appendSuffixCheckBox.Checked = true;
        _keepMetadataCheckBox.Checked = true;
        _writeEditorMetadataCheckBox.Checked = true;
        _outputDirIsCustom = false;
        _defaultOutputPath = "";
        _outputTimestampSuffix = "";
        SetOutputDirText("");
        UpdateOutputPathMode();
        UpdateEditSummary();
        UpdateSegmentTotalLabel();
        UpdateFormTitle();
        _outputRunButton.Enabled = false;
        _encodeRunButton.Enabled = false;
        _pendingEditHistoryEntry = null;
        _tabs.SelectedTab = _sourceTab;
        SetStatus("待機中");
        WriteLog("「新規」を実行: 入力動画と編集操作を起動時の状態に戻しました。");
    }

    private async Task CheckTrimSupportAsync()
    {
        var exe = _ffmpegPath.Trim();
        var supported = false;
        if (exe.Length > 0 && File.Exists(exe))
        {
            try
            {
                var output = await Task.Run(() =>
                {
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-hide_banner -bsfs",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    process.Start();
                    var text = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } }
                    return text;
                });
                supported = Regex.IsMatch(output, @"(?m)^\s*trim\s*$") && Regex.IsMatch(output, @"(?m)^\s*setts\s*$");
            }
            catch
            {
                supported = false;
            }
        }
        if (IsDisposed) return;
        _trimSupported = supported;
        if (supported)
        {
            _trimStatusText = "trim / setts フィルター: 対応";
            _trimStatusColor = Color.FromArgb(103, 209, 157);
        }
        else
        {
            _trimStatusText = "trim / setts: 未対応 / パス未設定";
            _trimStatusColor = Color.FromArgb(255, 177, 95);
        }
    }

    // ------------------------------------------------------------------
    // 設定
    // ------------------------------------------------------------------

    private void SaveSettingsFromUi()
    {
        // 欄が空のときに、保存済みのパスを空で上書きしないための保護。
        var ffmpegPath = _ffmpegPath.Trim().Trim('"');
        if (ffmpegPath.Length > 0 || _settings.FFmpegPath.Length == 0) _settings.FFmpegPath = ffmpegPath;
        var ffprobePath = _ffprobePath.Trim().Trim('"');
        if (ffprobePath.Length > 0 || _settings.FFprobePath.Length == 0) _settings.FFprobePath = ffprobePath;
        var mpvPath = _mpvPath.Trim().Trim('"');
        if (mpvPath.Length > 0 || _settings.MpvPath.Length == 0) _settings.MpvPath = mpvPath;
        _settings.Volume = Math.Clamp(_volumeBeforeMute, 1, 100);
        _settings.VolumeSliderValue = _volumeTrackBar != null ? _volumeTrackBar.Value : Math.Clamp(_volumeSliderValue, 0, 100);
        _settings.PreviewMuted = _previewMuted;
        _settings.PreviewBackend = "mpv";
        var profileName = _profileCombo?.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(profileName)) _settings.OutputProfileName = profileName;
        if (_settings.RememberWindowSize)
        {
            var bounds = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _settings.WindowWidth = Math.Max(1120, bounds.Width);
                _settings.WindowHeight = Math.Max(900, bounds.Height);
            }
        }
        if (!_settings.Save())
        {
            WriteLog("設定保存エラー");
        }
    }

    // ------------------------------------------------------------------
    // ラベルと入力欄の間隔の統一
    // ------------------------------------------------------------------

    private static Label? FindLabelByText(Control? parent, string text)
    {
        if (parent == null) return null;
        foreach (Control control in parent.Controls)
        {
            if (control is Label label && label.Text == text) return label;
        }
        return null;
    }

    private static void AlignLabelAndBox(Label? label, Control? box, int gap, bool keepRightEdge)
    {
        if (label == null || box == null) return;
        var textWidth = label.PreferredWidth;
        var right = box.Left + box.Width;
        box.Left = label.Left + textWidth + gap;
        if (keepRightEdge) box.Width = Math.Max(10, right - box.Left);
        // ラベルの枠が入力欄に重ならないよう幅は入力欄の手前までに詰めるが、
        // 文字が折り返す幅 (PreferredWidth) よりは狭くしない。
        label.Width = Math.Max(textWidth, box.Left - label.Left - 2);
        // 文字が入力欄の縦の真ん中に来るよう、ラベルの位置を合わせる。
        label.Top = box.Top + (box.Height - label.Height) / 2;
    }

    /// <summary>基準の間隔: 「画質 (crf):」のラベルの文字の末尾から入力欄までの距離。</summary>
    private int GetLabelBoxGap()
    {
        var crfLabel = FindLabelByText(_outputTab, "画質 (crf):");
        if (crfLabel == null) return 6;
        return Math.Max(2, _videoCrfBox.Left - crfLabel.Left - crfLabel.PreferredWidth);
    }

    /// <summary>ラベルの文字の末尾 (コロン) から入力欄までの間隔を、「画質 (crf):」の行にそろえる。</summary>
    private void InitializeLabelBoxGaps()
    {
        if (_videoCrfBox == null) return;
        var outputTab = _videoCrfBox.Parent;
        var crfLabel = FindLabelByText(outputTab, "画質 (crf):");
        if (crfLabel == null) return;
        var crfTextWidth = crfLabel.PreferredWidth;
        var gap = Math.Max(2, _videoCrfBox.Left - crfLabel.Left - crfTextWidth);
        // 基準行自身も文字幅と間隔が合うよう並べ直す (ラベルが欄に重なって枠線を隠さないため)。
        // 音質のラベルと欄は、並びを保ったまま同じ分だけずらす。
        var crfShift = (crfLabel.Left + crfTextWidth + gap) - _videoCrfBox.Left;
        if (crfShift != 0)
        {
            _videoCrfBox.Left += crfShift;
            var audioLabelPre = FindLabelByText(outputTab, "音質 (bit rate):");
            if (audioLabelPre != null) audioLabelPre.Left += crfShift;
            if (_audioBitrateCombo != null) _audioBitrateCombo.Left += crfShift;
        }
        crfLabel.Width = crfTextWidth;
        crfLabel.Top = _videoCrfBox.Top + (_videoCrfBox.Height - crfLabel.Height) / 2;
        var header = _inputPathBox.Parent;
        // 入力動画の欄は、右端のボタン類との兼ね合いで元の位置・長さのままにする (対象外)。
        // プレビューの欄は、左端を入力動画の欄とまったく同じ位置にする (幅は変えない)。
        var previewLabel = FindLabelByText(header, "プレビュー:");
        _previewBackendCombo.Left = _inputPathBox.Left;
        if (previewLabel != null)
        {
            var previewTextWidth = previewLabel.PreferredWidth;
            previewLabel.Width = Math.Max(previewTextWidth, _previewBackendCombo.Left - previewLabel.Left - 2);
            previewLabel.Top = _previewBackendCombo.Top + (_previewBackendCombo.Height - previewLabel.Height) / 2;
        }
        var cutTab = _cutStartBox.Parent;
        AlignLabelAndBox(FindLabelByText(cutTab, "開始:"), _cutStartBox, gap, true);
        AlignLabelAndBox(FindLabelByText(cutTab, "終了:"), _cutEndBox, gap, true);
        var speedLabel = FindLabelByText(cutTab, "変更後の速度:");
        AlignLabelAndBox(speedLabel, _speedBox, gap, false);
        // 速度の欄を動かした分、回転の欄も同じ計算で合わせ直す。
        if (_rotationLabel != null && _rotationCombo != null && speedLabel != null)
        {
            var speedTextWidth = speedLabel.PreferredWidth;
            var rotationTextWidth = _rotationLabel.PreferredWidth;
            var speedLabelGap = Math.Max(2, _speedBox.Left - (speedLabel.Left + speedTextWidth));
            _rotationCombo.Left = _rotationLabel.Left + rotationTextWidth + speedLabelGap;
            _rotationLabel.Width = Math.Max(rotationTextWidth, _rotationCombo.Left - _rotationLabel.Left - 2);
            _rotationLabel.Top = _rotationCombo.Top + (_rotationCombo.Height - _rotationLabel.Height) / 2;
        }
        AlignLabelAndBox(FindLabelByText(outputTab, "音質 (bit rate):"), _audioBitrateCombo, gap, false);
        AlignLabelAndBox(FindLabelByText(_outputDirBox.Parent, "フォルダーパス:"), _outputDirBox, gap, false);
    }

    // ------------------------------------------------------------------
    // テキストボックスの余白 (EM_SETMARGINS)
    // ------------------------------------------------------------------

    // 適用済みのハンドルを覚えておき、同じハンドルへ二重に足さない。
    private readonly Dictionary<Control, IntPtr> _marginAppliedHandles = new();

    /// <summary>現在の余白へ指定量を足す。</summary>
    private void ApplyTextBoxMarginsOnce(TextBox textBox, int leftAdd, int rightAdd)
    {
        const int emSetMargins = 0x00D3;
        const int emGetMargins = 0x00D4;
        if (!textBox.IsHandleCreated) return;
        if (_marginAppliedHandles.TryGetValue(textBox, out var applied) && applied == textBox.Handle) return;
        _marginAppliedHandles[textBox] = textBox.Handle;
        var margins = SendMessage(textBox.Handle, emGetMargins, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var left = (margins & 0xFFFF) + leftAdd;
        var right = ((margins >> 16) & 0xFFFF) + rightAdd;
        var flags = (leftAdd != 0 ? 0x0001 : 0) | (rightAdd != 0 ? 0x0002 : 0);
        var packed = (left & 0xFFFF) | ((right & 0xFFFF) << 16);
        SendMessage(textBox.Handle, emSetMargins, (IntPtr)flags, (IntPtr)packed);
    }

    /// <summary>表示が落ち着いた時点で、パス欄の余白を付け直す (ハンドル作り直しへの保険)。</summary>
    private void EnsurePathBoxMargins()
    {
        ApplyTextBoxMarginsOnce(_inputPathBox, 2, 26);
        ApplyTextBoxMarginsOnce(_outputDirBox, 2, 0);
        ApplyTextBoxMarginsOnce(_cutStartBox, 2, 0);
        ApplyTextBoxMarginsOnce(_cutEndBox, 2, 0);
        ApplyTextBoxMarginsOnce(_chapterStartBox, 2, 0);
    }

    /// <summary>
    /// クリックされた場所から、タブ一覧がアクティブかどうかを記録するフィルター。
    /// 見出しのクリックだけをアクティブとし、それ以外の場所 (ページの余白を含む) の
    /// クリックは非アクティブにする。実際のフォーカス先は環境で動きが違うため、
    /// フォーカスを外しに行くのではなく、この記録だけで矢印キーの効きを決める。
    /// ホイールでのタブ移動 (見出しの上) には影響しない。
    /// </summary>
    private sealed class TabStripFocusFilter : IMessageFilter
    {
        private const int WmLButtonDown = 0x0201;
        private readonly MainForm _form;

        public TabStripFocusFilter(MainForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmLButtonDown || _form.IsDisposed) return false;
            _form._tabStripKeyActive = Control.FromHandle(m.HWnd) == _form._tabs;
            return false;
        }
    }
}
