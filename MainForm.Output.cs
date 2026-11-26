using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// 動画出力タブ (プロファイル・再エンコード設定・出力先・編集内容) と出力名の計算。
/// 出力先はフォルダーのみを指定し、ファイル名は入力名と編集内容から自動で決まる。
/// </summary>
internal sealed partial class MainForm
{
    private ComboBox _profileCombo = null!;
    private Label _profileDirtyLabel = null!;
    private Button _addProfileButton = null!;
    private Button _saveProfileButton = null!;
    private ComboBox _videoFormatCombo = null!;
    private TextBox _videoCrfBox = null!;
    private ComboBox _bitDepthCombo = null!;
    private ComboBox _audioFormatCombo = null!;
    private ComboBox _audioBitrateCombo = null!;
    private ComboBox _containerCombo = null!;
    private Panel _outputPathGroup = null!;
    private TextBox _outputDirBox = null!;
    private Label _outputFixedNameLabel = null!;
    private Button _outputFolderButton = null!;
    private CheckBox _appendTimestampCheckBox = null!;
    private CheckBox _appendSuffixCheckBox = null!;
    private CheckBox _keepMetadataCheckBox = null!;
    private CheckBox _writeEditorMetadataCheckBox = null!;
    private Panel _editSummaryGroup = null!;
    private Label _editSummaryLabel = null!;
    private Button _outputRunButton = null!;
    private Panel _encodeSettingsGroup = null!;
    private Button _encodeRunButton = null!;
    private Button _showCommandLinesButton = null!;
    private readonly ToolTip _outputToolTip = new();

    private bool _outputDirIsCustom;
    private bool _outputModeUserSelected;
    private bool _updatingOutputDir;
    private string _defaultOutputPath = "";
    private string _outputTimestampSuffix = "";

    // 出力モードの値 (編集状態の保存と共通): 0 = カット以外の編集、1 = 抽出・結合、2 = 抽出・分割。
    // 区間がなければ 0、区間が 1 グループなら 1、それ以外は 2 として、区間とグループ分けから求める。
    private int OutputMode
    {
        get
        {
            if (_segments.Count == 0) return 0;
            return BuildSegmentGroups().Count == 1 ? 1 : 2;
        }
    }

    private static ComboBox MakeFormatCombo(int x, int y, int width)
    {
        // 高さは 28 に固定する (通常描画ではフォントに合わせて本体が大きくなり、横のボタンと揃わない)。
        // 自前描画で項目の高さを本体いっぱいにし、枠とボタンは FlatStyle でテーマに馴染ませる。
        var combo = new ThemedComboBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
        };
        combo.DrawItem += DrawFormatComboItem;
        return combo;
    }

    /// <summary>コンボの表示テキストを、テキストボックスと同じく先頭に 2px の余白を足して描く。</summary>
    private static void DrawFormatComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var isEditPart = (e.State & DrawItemState.ComboBoxEdit) != 0;
        // 表示部分は ThemedComboBox の自前描画で描くため、ここでは描かない。
        // 描く経路が 1 つだけになり、文字の位置が固定される。
        if (isEditPart && sender is ThemedComboBox) return;
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        // 背景は OS の選択色を使わず、テーマの色で塗る。編集部分はフォーカス時でも背景色のまま、
        // リストの選択行はテーマの選択色にする。
        var backColor = !isEditPart && isSelected ? Color.FromArgb(86, 74, 110) : combo.BackColor;
        using (var brush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }
        if (e.Index >= 0)
        {
            var text = combo.Items[e.Index]?.ToString() ?? "";
            // そのまま描くと文字がわずかに下寄りに見えるため、項目の高さ (24 が基準) に応じた
            // 1ピクセル分だけ上へずらして描く。
            var shift = Math.Max(1, (int)Math.Round(e.Bounds.Height / 24.0));
            var bounds = new Rectangle(e.Bounds.Left + 2, e.Bounds.Top - shift, e.Bounds.Width - 2, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, combo.Font, bounds, e.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
        // フォーカスの点線は描かない (選択行の色だけで示す)。
    }

    private void BuildOutputTab()
    {
        // ---- エンコード設定 (プロファイルと再エンコード設定) ----
        _encodeSettingsGroup = Theme.MakeGroupPanel(10, 367, 1015, 229, "エンコード設定");
        _outputTab.Controls.Add(_encodeSettingsGroup);
        // ラベルの枠がコンボに重なって文字を隠さないよう、幅は画面のフォントで測った文字の実幅に合わせる。
        var profileLabel = Theme.MakeLabel("プロファイル:", 16, 33, 110, 28);
        profileLabel.Width = TextRenderer.MeasureText(profileLabel.Text, Font).Width;
        _encodeSettingsGroup.Controls.Add(profileLabel);
        _profileCombo = MakeFormatCombo(112, 33, 220);
        _encodeSettingsGroup.Controls.Add(_profileCombo);
        _addProfileButton = Theme.MakeButton("プロファイルを追加", 340, 33, 150, 30);
        _encodeSettingsGroup.Controls.Add(_addProfileButton);
        _saveProfileButton = Theme.MakeButton("現在のプロファイルに保存", 498, 33, 210, 30);
        _encodeSettingsGroup.Controls.Add(_saveProfileButton);
        // グループの題字ラベル (幅 480) が行の上端に重なって枠を隠すため、手前に出す。
        _profileCombo.BringToFront();
        _addProfileButton.BringToFront();
        _saveProfileButton.BringToFront();
        // 選択中のプロファイルから設定が変更されたことを示す印。プロファイル欄の右上に重ねて出す。
        // 背景は題字ラベルの帯に馴染むよう、ページの背景色にしておく。
        _profileDirtyLabel = new Label
        {
            Text = "*",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(150, 128, 205),
            BackColor = Theme.BoxBack,
            Location = new Point(117, 34),
            Visible = false,
        };
        _encodeSettingsGroup.Controls.Add(_profileDirtyLabel);
        _profileDirtyLabel.BringToFront();
        _outputToolTip.SetToolTip(_profileDirtyLabel, "選択中のプロファイルから設定が変更されています。\r\n「現在のプロファイルに保存」を押すと、変更が保存されて印が消えます。");
        _outputToolTip.SetToolTip(_profileCombo, "再エンコード設定のプロファイル。\r\nprofiles フォルダーに1件1ファイルで保存されます。\r\n右クリックで並び順の変更、名前の変更、削除ができます (名前の変更と削除は組み込みのプロファイルは除く)。");
        _outputToolTip.SetToolTip(_addProfileButton, "現在の設定値で新しいプロファイルを作成します。\r\n名前は、右クリックで変更できます。");
        _outputToolTip.SetToolTip(_saveProfileButton, "現在の設定値を、選択中のプロファイルへ上書き保存します。");

        _encodeSettingsGroup.Controls.Add(Theme.MakeLabel("映像フォーマット:", 16, 67, 130, 28));
        _videoFormatCombo = MakeFormatCombo(148, 67, 110);
        foreach (var format in VideoFormatNames) _videoFormatCombo.Items.Add(format);
        _encodeSettingsGroup.Controls.Add(_videoFormatCombo);
        var crfLabel = Theme.MakeLabel("画質 (crf):", 274, 67, 82, 28);
        _encodeSettingsGroup.Controls.Add(crfLabel);
        _videoCrfBox = Theme.MakeTextBox(357, 67, 70, 28);
        _videoCrfBox.ImeMode = ImeMode.Disable;
        _videoCrfBox.Text = "23.0";
        _videoCrfBox.TextAlign = HorizontalAlignment.Center;
        _videoCrfBox.MouseWheel += (_, e) => CrfBoxMouseWheel(e);
        _videoCrfBox.KeyDown += (_, e) => CrfBoxKeyDown(e);
        _videoCrfBox.Leave += (_, _) => CommitCrfBoxText();
        _encodeSettingsGroup.Controls.Add(_videoCrfBox);
        var bitDepthLabel = Theme.MakeLabel("ビット数:", 449, 67, 80, 28);
        bitDepthLabel.Width = TextRenderer.MeasureText(bitDepthLabel.Text, Font).Width;
        _encodeSettingsGroup.Controls.Add(bitDepthLabel);
        _bitDepthCombo = MakeFormatCombo(522, 67, 90);
        _encodeSettingsGroup.Controls.Add(_bitDepthCombo);
        // 画質のツールチップは、自動表示だとボックスに被ってしまうため、
        // 再生速度の欄と同じくボックスのすぐ下に固定して出す (ラベルに重ねたときも同じ位置)。
        var crfToolTip = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300 };
        var crfHintText = string.Join(Environment.NewLine,
            "映像を再エンコードするときの CRF。AVC / HEVC は 0～51 (小数可)、AV1 は 0～63 (整数のみ)。",
            "マウスホイールと ↑↓ キーでも変更できます (AVC / HEVC は 0.1 刻み、AV1 は 1 刻み)。");
        EventHandler showCrfHint = (_, _) => crfToolTip.Show(crfHintText, _videoCrfBox, 0, _videoCrfBox.Height + 6);
        _videoCrfBox.MouseEnter += showCrfHint;
        crfLabel.MouseEnter += showCrfHint;
        _videoCrfBox.MouseLeave += (_, _) => crfToolTip.Hide(_videoCrfBox);
        crfLabel.MouseLeave += (_, _) => crfToolTip.Hide(_videoCrfBox);

        _encodeSettingsGroup.Controls.Add(Theme.MakeLabel("音声フォーマット:", 16, 101, 130, 28));
        _audioFormatCombo = MakeFormatCombo(148, 101, 110);
        foreach (var format in AudioFormatNames) _audioFormatCombo.Items.Add(format);
        _encodeSettingsGroup.Controls.Add(_audioFormatCombo);
        _encodeSettingsGroup.Controls.Add(Theme.MakeLabel("音質:", 274, 101, 116, 28));
        _audioBitrateCombo = MakeFormatCombo(357, 101, 105);
        _encodeSettingsGroup.Controls.Add(_audioBitrateCombo);

        _encodeSettingsGroup.Controls.Add(Theme.MakeLabel("コンテナ:", 16, 135, 130, 28));
        _containerCombo = MakeFormatCombo(148, 135, 110);
        _encodeSettingsGroup.Controls.Add(_containerCombo);
        _outputToolTip.SetToolTip(_containerCombo, "出力ファイルのコンテナ。\r\nWebM は映像 AV1 + 音声 Opus の組み合わせのときだけ選べます。");

        _encodeRunButton = Theme.MakeButton("エンコードして出力", 12, 179, 160, 40);
        _encodeRunButton.Enabled = false;
        _encodeRunButton.BackColor = Theme.Accent;
        _encodeSettingsGroup.Controls.Add(_encodeRunButton);

        _showCommandLinesButton = Theme.MakeButton("コマンドラインを表示", 22, 604, 160, 34);
        _showCommandLinesButton.Click += (_, _) => ShowCommandLinesPopup();
        _outputTab.Controls.Add(_showCommandLinesButton);

        // ---- 出力先 (フォルダーのみ。ファイル名は自動) ----
        _outputPathGroup = Theme.MakeGroupPanel(10, 12, 1015, 193, "出力先");
        _outputTab.Controls.Add(_outputPathGroup);
        _outputPathGroup.Controls.Add(Theme.MakeLabel("フォルダーパス:", 12, 31, 100, 26));
        _outputDirBox = Theme.MakeTextBox(98, 31, 540);
        _outputDirBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _outputPathGroup.Controls.Add(_outputDirBox);
        _outputFixedNameLabel = new Label
        {
            Location = new Point(639, 31),
            Size = new Size(254, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(46, 40, 62),
            ForeColor = Color.FromArgb(198, 182, 235),
            Cursor = Cursors.Hand,
        };
        _outputPathGroup.Controls.Add(_outputFixedNameLabel);
        _outputFolderButton = Theme.MakeButton("フォルダー...", 896, 30, 107, 30);
        _outputFolderButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _outputPathGroup.Controls.Add(_outputFolderButton);

        CheckBox MakeCheckBox(string text, int y, int width, bool checkedState)
        {
            return new CheckBox
            {
                Text = text,
                Location = new Point(12, y),
                Size = new Size(width, 28),
                ForeColor = Theme.LabelFore,
                BackColor = Color.Transparent,
                Checked = checkedState,
            };
        }
        _appendTimestampCheckBox = MakeCheckBox("出力ファイル名の末尾に現在時刻を追加（例: _20261021102603）", 72, 760, false);
        _outputPathGroup.Controls.Add(_appendTimestampCheckBox);
        _appendSuffixCheckBox = MakeCheckBox("出力ファイル名の末尾にサフィックスを付与する", 98, 760, true);
        _outputPathGroup.Controls.Add(_appendSuffixCheckBox);
        _keepMetadataCheckBox = MakeCheckBox("メタデータを残す（タイトル・作成日時・言語などのタグ）", 124, 980, true);
        _outputPathGroup.Controls.Add(_keepMetadataCheckBox);
        _writeEditorMetadataCheckBox = MakeCheckBox("メタデータを書き込む（編集内容・編集日時・区間・解像度・エンコード設定など。MKV / MP4 / MOV / M4V のみ）", 150, 980, true);
        _outputPathGroup.Controls.Add(_writeEditorMetadataCheckBox);

        _editSummaryGroup = Theme.MakeGroupPanel(10, 213, 1015, 146, "編集内容");
        _outputTab.Controls.Add(_editSummaryGroup);
        _editSummaryLabel = Theme.MakeLabel("入力動画を読み込んでください。", 12, 24, 991, 56);
        _editSummaryGroup.Controls.Add(_editSummaryLabel);
        _outputRunButton = Theme.MakeButton("動画を出力", 12, 98, 160, 40);
        _outputRunButton.Enabled = false;
        _outputRunButton.BackColor = Theme.Accent;
        _editSummaryGroup.Controls.Add(_outputRunButton);
        var runNoteLabel = Theme.MakeLabel("※ 可能な限り無劣化で出力します。エンコードが必要な場合は、下の「エンコード設定」の内容でエンコードします。", 184, 98, 820, 40);
        runNoteLabel.TextAlign = ContentAlignment.MiddleLeft;
        _editSummaryGroup.Controls.Add(runNoteLabel);

        // ---- イベント ----
        _outputDirBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_outputDirBox, 2, 0);
        if (_outputDirBox.IsHandleCreated) ApplyTextBoxMarginsOnce(_outputDirBox, 2, 0);
        _profileCombo.SelectedIndexChanged += (_, _) => OnProfileSelectionChanged();
        _addProfileButton.Click += (_, _) => AddNewProfile();
        _saveProfileButton.Click += (_, _) => SaveCurrentProfile();
        InitializeProfileMenu();
        _videoFormatCombo.SelectedIndexChanged += (_, _) => OnVideoFormatChanged();
        _audioFormatCombo.SelectedIndexChanged += (_, _) => OnAudioFormatChanged();
        _containerCombo.SelectedIndexChanged += (_, _) => OnContainerChanged();
        _bitDepthCombo.SelectedIndexChanged += (_, _) => UpdateProfileDirtyMark();
        _audioBitrateCombo.SelectedIndexChanged += (_, _) => UpdateProfileDirtyMark();
        _appendSuffixCheckBox.CheckedChanged += (_, _) => UpdateCutOutputName();
        _outputFolderButton.Click += (_, _) => BrowseOutputDirectory();
        _outputFixedNameLabel.Click += (_, _) => ShowOutputFileNames();
        _outputDirBox.TextChanged += (_, _) => { if (!_updatingOutputDir) _outputDirIsCustom = true; };
        _outputDirBox.Leave += (_, _) =>
        {
            var typedDir = _outputDirBox.Text.Trim().Trim('"');
            if (typedDir.Length == 0)
            {
                _outputDirIsCustom = false;
                UpdateOutputNameView();
            }
            else
            {
                SetOutputDirText(FormatDirectoryForDisplay(typedDir));
            }
        };
        _outputRunButton.Click += (_, _) => StartSelectedOutput(false);
        _encodeRunButton.Click += (_, _) => StartSelectedOutput(true);

        InitializeOutputProfiles();
    }

    // ------------------------------------------------------------------
    // サフィックスと自動出力名
    // ------------------------------------------------------------------

    private static string GetRotationFileSuffix(int index) => index switch
    {
        1 => "_rot90cw",
        2 => "_rot180",
        3 => "_rot90ccw",
        _ => "",
    };

    private string GetSpeedRotationFileSuffix()
    {
        var suffix = "";
        var speed = GetSelectedSpeed();
        if (Math.Abs(speed - 1.0) >= 0.000001) suffix += "_x" + FormatSpeedForFileName(speed);
        suffix += GetRotationFileSuffix(GetRotationIndex());
        return suffix;
    }

    private string GetAudioTrackFileSuffix()
    {
        if (_audioStreams.Count == 0) return "";
        var selectedNumbers = new System.Collections.Generic.List<int>();
        for (var i = 0; i < _audioStreams.Count; i++)
        {
            if (i < _audioList.Items.Count && _audioList.GetItemChecked(i)) selectedNumbers.Add(i + 1);
        }
        if (selectedNumbers.Count >= _audioStreams.Count) return "";
        if (selectedNumbers.Count == 0) return "_noaudio";
        return "_audio" + string.Join("-", selectedNumbers);
    }

    private string GetCurrentOutputSuffix()
    {
        if (_appendSuffixCheckBox != null && !_appendSuffixCheckBox.Checked) return "";
        if (_segments.Count == 0)
        {
            return GetSpeedRotationFileSuffix() + GetAudioTrackFileSuffix();
        }
        return GetCutRangeSuffix();
    }

    private string GetCutRangeSuffix()
    {
        var speedRotationSuffix = GetSpeedRotationFileSuffix();
        if (_segments.Count == 0) return speedRotationSuffix + GetAudioTrackFileSuffix();
        var parts = new System.Collections.Generic.List<string>();
        foreach (var group in BuildSegmentGroups())
        {
            parts.Add(GetGroupCutNamePart(group));
        }
        return "_" + string.Join("_", parts) + speedRotationSuffix + GetAudioTrackFileSuffix();
    }

    private static string FormatFileClock(long ms)
    {
        return TimeText.FormatClockFromMs(ms).Replace(':', '-');
    }

    /// <summary>
    /// グループのカット内容を表す名前の部品。
    /// 1 区間は「cut_[開始 - 終了]」(クリッピングは「clip_[開始 - 終了]」)、
    /// 結合グループは「cut(区間数)_[カット前の再生時間 → カット後の再生時間]」。
    /// カット前は入力動画全体の再生時間、カット後はグループ内の区間の合計時間。
    /// 結合グループにクリッピングが混在する場合は「cut(通常カットの区間数)+clip(クリッピングの区間数)」とし、クリッピングのみなら「clip(区間数)」とする。
    /// </summary>
    private string GetGroupCutNamePart(System.Collections.Generic.List<Segment> group)
    {
        if (group.Count == 1)
        {
            var segment = group[0];
            var singleOperation = segment.HasClipping ? "clip" : "cut";
            return $"{singleOperation}_[{FormatFileClock(segment.StartMs)} - {FormatFileClock(segment.EndMs)}]";
        }
        var clipCount = 0;
        foreach (var segment in group) if (segment.HasClipping) clipCount++;
        var cutCount = group.Count - clipCount;
        string operation;
        if (clipCount == 0) operation = $"cut{group.Count}";
        else if (cutCount == 0) operation = $"clip{group.Count}";
        else operation = $"cut{cutCount}+clip{clipCount}";
        var beforeMs = _durationMs > 0 ? _durationMs : group[^1].EndMs;
        long afterMs = 0;
        foreach (var segment in group) afterMs += segment.EndMs - segment.StartMs;
        return $"{operation}_[{FormatFileClock(beforeMs)} to {FormatFileClock(afterMs)}]";
    }

    /// <summary>入力ファイルのコンテナの拡張子 (ffprobe の format_name から判定し、判別できない場合は入力ファイルの拡張子を使う)。</summary>
    private string GetSourceContainerExtension()
    {
        if (_probe == null || string.IsNullOrWhiteSpace(_inputPath)) return GetContainerExtension();
        var formatName = _probe.FormatName ?? "";
        var inputExt = Path.GetExtension(_inputPath).ToLowerInvariant();
        // M2TS (BDAV) は通常の TS と判別する。Blu-ray の LPCM 音声 (pcm_bluray) は通常の TS では
        // 保持できず、データストリームとして書き出されて再生側で認識されなくなるため。
        if (formatName.Contains("mpegts")) return inputExt is ".m2ts" or ".mts" ? inputExt : ".ts";
        if (formatName.Contains("matroska")) return inputExt == ".webm" ? ".webm" : ".mkv";
        if (formatName.Contains("mp4") || formatName.Contains("mov"))
        {
            return inputExt is ".mp4" or ".mov" or ".m4v" ? inputExt : ".mp4";
        }
        // その他のコンテナは、入力ファイルの拡張子をそのまま使う。
        return inputExt.Length > 1 ? inputExt : GetContainerExtension();
    }

    /// <summary>全体編集がストリームコピーになるかどうか。速度変更と音声保持が重なると再エンコードになる。「エンコードして出力」では常に再エンコードになる。</summary>
    private bool WholeOutputIsStreamCopy(bool forceEncode = false)
    {
        if (forceEncode) return false;
        var speedChanged = Math.Abs(GetSelectedSpeed() - 1.0) >= 0.000001;
        return !(speedChanged && GetSelectedAudioStreams().Count > 0);
    }

    /// <summary>グループの出力がストリームコピーになるかどうか (1 区間・クリッピングなし・再エンコード条件に当たらない場合)。「エンコードして出力」では常に再エンコードになる。</summary>
    private bool GroupIsStreamCopy(System.Collections.Generic.List<Segment> group, bool forceEncode = false)
    {
        if (forceEncode) return false;
        if (group.Count != 1 || group[0].HasClipping) return false;
        var speedChanged = Math.Abs(GetSelectedSpeed() - 1.0) >= 0.000001;
        return !(speedChanged && GetSelectedAudioStreams().Count > 0);
    }

    /// <summary>全体編集の出力拡張子。ストリームコピーは、プロファイルのコンテナによらず入力と同じコンテナで出力する。</summary>
    private bool IsTsSourceContainer() => string.Equals(GetSourceContainerExtension(), ".ts", StringComparison.OrdinalIgnoreCase);

    private string GetWholeOutputExtension(bool forceEncode = false) => WholeOutputIsStreamCopy(forceEncode) ? GetSourceContainerExtension() : GetContainerExtension();

    /// <summary>グループの出力拡張子。ストリームコピーは、プロファイルのコンテナによらず入力と同じコンテナで出力する。</summary>
    private string GetGroupOutputExtension(System.Collections.Generic.List<Segment> group, bool forceEncode = false) => GroupIsStreamCopy(group, forceEncode) ? GetSourceContainerExtension() : GetContainerExtension();

    /// <summary>全体編集の出力ファイル名 (入力名 + サフィックス + 出力方法で決まる拡張子)。</summary>
    private string GetWholeOutputFileName(string timestampSuffix = "", bool forceEncode = false)
    {
        if (string.IsNullOrWhiteSpace(_inputPath)) return "";
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFullPath(_inputPath));
        return baseName + GetCurrentOutputSuffix() + timestampSuffix + GetWholeOutputExtension(forceEncode);
    }

    /// <summary>全体編集の出力パス。フォルダーは出力先欄、ファイル名は自動生成、時刻サフィックスはチェックに応じて付ける。</summary>
    private string GetWholeOutputPath(bool forceEncode = false)
    {
        var stamp = "";
        if (_appendTimestampCheckBox != null && _appendTimestampCheckBox.Checked)
        {
            stamp = "_" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        }
        _outputTimestampSuffix = stamp;
        return Path.Combine(GetOutputDirectory(), GetWholeOutputFileName(stamp, forceEncode));
    }

    private void UpdateCutOutputName()
    {
        UpdateOutputNameView();
    }

    private void SetOutputDefaults(string inputFilePath)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            throw new InvalidOperationException("出力先の初期設定に使う入力ファイルパスが空です。動画を再選択してください。");
        }
        _inputPath = Path.GetFullPath(inputFilePath.Trim().Trim('"'));
        _outputDirIsCustom = false;
        if (_durationMs > 0) _cutEndBox.Text = TimeText.FormatClockFromMs(_durationMs);
        UpdateOutputNameView();
        UpdateEditSummary();
        UpdateSegmentTotalLabel();
    }

    // ------------------------------------------------------------------
    // 出力先フォルダーとファイル名表示
    // ------------------------------------------------------------------

    private string GetSplitFixedFileName()
    {
        if (string.IsNullOrWhiteSpace(_inputPath)) return "";
        var full = Path.GetFullPath(_inputPath);
        var name = Path.GetFileNameWithoutExtension(full);
        // 区間出力の基準名も、出力方法で決まる拡張子を使う (代表として先頭グループの拡張子)。
        var ext = _segments.Count > 0 ? GetGroupOutputExtension(BuildSegmentGroups()[0]) : GetWholeOutputExtension();
        return name + ext;
    }

    private static string FormatDirectoryForDisplay(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        if (directory.EndsWith('\\') || directory.EndsWith('/')) return directory;
        return directory + "\\";
    }

    /// <summary>出力グループ 1 件分のファイル名。1 区間なら区間単位の名前、結合グループなら区間数と前後の再生時間で表す名前。</summary>
    private string GetGroupOutputFileName(System.Collections.Generic.List<Segment> group, string timestampSuffix = "", bool forceEncode = false)
    {
        if (group.Count == 1) return GetSplitOutputFileName(group[0], timestampSuffix, forceEncode);
        if (string.IsNullOrWhiteSpace(_inputPath)) return "";
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFullPath(_inputPath));
        var ext = GetGroupOutputExtension(group, forceEncode);
        // カット表記もサフィックスの一部とし、付与オプションがオフなら付けない。
        var suffix = _appendSuffixCheckBox != null && !_appendSuffixCheckBox.Checked
            ? ""
            : "_" + GetGroupCutNamePart(group) + GetSpeedRotationFileSuffix() + GetAudioTrackFileSuffix();
        return $"{baseName}{suffix}{timestampSuffix}{ext}";
    }

    /// <summary>出力グループのファイル名の一覧。同じ範囲のグループが複数あると名前が同じになるため、2 件目以降には連番を付けて区別する (同じ区間の繰り返しも出力できるようにするため)。</summary>
    private System.Collections.Generic.List<string> GetGroupOutputFileNames(string timestampSuffix = "", bool forceEncode = false)
    {
        var names = new System.Collections.Generic.List<string>();
        var usedNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var group in BuildSegmentGroups())
        {
            var name = GetGroupOutputFileName(group, timestampSuffix, forceEncode);
            if (!usedNames.Add(name))
            {
                var baseName = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                var number = 2;
                string candidate;
                do
                {
                    candidate = $"{baseName}_{number}{ext}";
                    number++;
                }
                while (!usedNames.Add(candidate));
                name = candidate;
            }
            names.Add(name);
        }
        return names;
    }

    private string GetSplitOutputFileName(Segment segment, string timestampSuffix = "", bool forceEncode = false)
    {
        if (string.IsNullOrWhiteSpace(_inputPath)) return "";
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFullPath(_inputPath));
        var ext = GetGroupOutputExtension(new System.Collections.Generic.List<Segment> { segment }, forceEncode);
        // カット表記もサフィックスの一部とし、付与オプションがオフなら付けない。
        var suffix = _appendSuffixCheckBox != null && !_appendSuffixCheckBox.Checked
            ? ""
            : "_" + GetGroupCutNamePart(new System.Collections.Generic.List<Segment> { segment })
              + GetSpeedRotationFileSuffix() + GetAudioTrackFileSuffix();
        return $"{baseName}{suffix}{timestampSuffix}{ext}";
    }

    private void SetOutputDirText(string text)
    {
        _updatingOutputDir = true;
        try { _outputDirBox.Text = text; }
        finally { _updatingOutputDir = false; }
    }

    private void UpdateSplitPathLayout()
    {
        // フォルダー欄 (編集可) とファイル名の枠を、出力先行の範囲内に並べる。
        var width = 160;
        try
        {
            var measured = TextRenderer.MeasureText(_outputFixedNameLabel.Text, _outputFixedNameLabel.Font).Width;
            width = (int)Math.Max(120, Math.Min(380, measured + 22));
        }
        catch { }
        var right = _outputFolderButton.Left - 7;
        var top = _outputDirBox.Top;
        var height = _outputDirBox.Height;
        _outputFixedNameLabel.SetBounds(right - width, top, width, height);
        _outputDirBox.SetBounds(_outputDirBox.Left, top, right - width - _outputDirBox.Left, height);
    }

    /// <summary>出力先フォルダーの初期値。オプションで固定が有効なら固定フォルダー、そうでなければ入力と同じフォルダー。</summary>
    private string GetDefaultOutputDirectory()
    {
        if (_settings.FixedOutputDirectoryEnabled && !string.IsNullOrWhiteSpace(_settings.FixedOutputDirectory))
        {
            return _settings.FixedOutputDirectory.Trim().Trim('"');
        }
        if (!string.IsNullOrWhiteSpace(_inputPath))
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(_inputPath)) ?? ""; } catch { }
        }
        return "";
    }

    /// <summary>出力先フォルダー (未指定なら入力と同じフォルダー) とファイル名表示を最新にする。</summary>
    private void UpdateOutputNameView()
    {
        if (_outputDirBox == null || _outputFixedNameLabel == null) return;
        _outputFixedNameLabel.Text = _segments.Count > 0 ? GetSplitFixedFileName() : GetWholeOutputFileName();
        if (!_outputDirIsCustom)
        {
            SetOutputDirText(FormatDirectoryForDisplay(GetDefaultOutputDirectory()));
        }
        _outputToolTip.SetToolTip(_outputFixedNameLabel, _segments.Count > 0
            ? "出力時のファイル名は自動で決まります。\r\n1区間の出力には「_cut_[開始 - 終了]」、結合グループには「_cut(区間数)_[カット前の再生時間 to カット後の再生時間]」が付きます (クリッピングの区間は「clip」として数え、混在するグループは「_cut(数)+clip(数)_」になります)。\r\n速度・回転を指定した場合は、その後ろに「_x0.50」「_rot90cw」などが付きます。\r\nクリックすると、出力するファイル名を全て確認できます。"
            : "出力時のファイル名は自動で決まります。\r\n拡張子はコンテナで決まります。\r\n速度・回転・音声トラックを変更した場合は「_x0.50」「_rot90cw」などのサフィックスが付きます。\r\nクリックすると、出力するファイル名を確認できます。");
        UpdateSplitPathLayout();
    }

    /// <summary>
    /// 表示用の組み立てに限り、無劣化の範囲調整の探査を出力ごとに並行して先行させる。
    /// 探査結果は通常の組み立てと同じキャッシュに入るため、組み立て結果は変わらない。
    /// </summary>
    private void PrewarmLosslessProbeCaches()
    {
        try
        {
            if (Math.Abs(GetSelectedSpeed() - 1.0) >= 0.000001) return;
            var ranges = new List<(long StartMs, long EndMs)>();
            if (_segments.Count == 0)
            {
                if (_durationMs > 0) ranges.Add((0, _durationMs));
            }
            else
            {
                // 単独区間のグループだけが無劣化の範囲調整を使う (結合グループは再エンコードになる)。
                var i = 0;
                while (i < _segments.Count)
                {
                    var j = i;
                    while (j + 1 < _segments.Count && _segments[j + 1].JoinWithPrevious) j++;
                    if (j == i) ranges.Add((_segments[i].StartMs, _segments[i].EndMs));
                    i = j + 1;
                }
            }
            if (ranges.Count < 2) return;
            Parallel.ForEach(ranges.Distinct(), range =>
            {
                try { AdjustLosslessVideoRangeMs(range.StartMs, range.EndMs); } catch { }
            });
        }
        catch { }
    }

    /// <summary>現在の設定で実行される出力の全ステップを、実行せずに組み立ててコマンドライン文字列にする。</summary>
    private (List<(string Description, string Command)> Items, string Error) CaptureCommandLines(bool forceEncode, bool writeAsterCut)
    {
        _previewingCommands = true;
        _previewAsterCut = writeAsterCut;
        _capturedCommandSteps = null;
        _capturedCommandError = "";
        try
        {
            if (!forceEncode) PrewarmLosslessProbeCaches();
            StartSelectedOutput(forceEncode);
        }
        finally
        {
            _previewingCommands = false;
        }
        if (_capturedCommandSteps == null) return (new List<(string Description, string Command)>(), _capturedCommandError);
        var ffmpeg = GetFFmpegPath();
        var items = _capturedCommandSteps
            .Select(step => (step.Description, QuoteProcessArgument(ffmpeg) + " " + ConvertToProcessArgumentLine(step.Arguments)))
            .ToList();
        return (items, "");
    }

    /// <summary>「コマンドラインを表示」: 出力前の全コマンドを 1 行ずつポップアップに表示する。無劣化とエンコードを切り替えられ、項目の選択で各出力へ移動できる。</summary>
    private void ShowCommandLinesPopup()
    {
        if (_jobRunning) return;
        if (_probe == null)
        {
            MessageBox.Show("先に入力動画を読み込んでください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        (List<(string Description, string Command)> Items, string Error)? encode = null;
        (List<(string Description, string Command)> Items, string Error)? lossless = null;
        var previousStatus = _statusLabel.Text;
        _statusLabel.Text = "コマンドラインを作成しています...";
        Cursor.Current = Cursors.WaitCursor;
        Update();
        try
        {
            lossless = CaptureCommandLines(false, false);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
            _statusLabel.Text = previousStatus;
        }

        using var popup = new Form
        {
            Icon = AppIcon.Value,
            Text = "コマンドライン",
            Size = new Size(940, 550),
            MinimumSize = new Size(830, 300),
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.CenterParent,
        };
        // パネルの幅を先に与えておく (右寄せのアンカーの基準にするため)。
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44, Width = 924, BackColor = Theme.PageBack };
        var losslessRadio = new RadioButton
        {
            Text = "無劣化",
            Location = new Point(12, 11),
            Size = new Size(90, 24),
            ForeColor = Theme.LabelFore,
            Checked = true,
        };
        var encodeRadio = new RadioButton
        {
            Text = "エンコード",
            Location = new Point(110, 11),
            Size = new Size(110, 24),
            ForeColor = Theme.LabelFore,
        };
        topPanel.Controls.Add(losslessRadio);
        topPanel.Controls.Add(encodeRadio);
        var jumpCombo = new ThemedComboBox
        {
            Location = new Point(240, 9),
            Size = new Size(430, 25),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 21,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
        };
        jumpCombo.DrawItem += DrawSpecSectionComboItem;
        topPanel.Controls.Add(jumpCombo);
        // 独自タグのチェックは常にオフで開き、状態は記憶しない。
        var asterCutCheck = new CheckBox
        {
            Text = "_ASTER_CUT",
            Location = new Point(792, 11),
            Size = new Size(124, 24),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        topPanel.Controls.Add(asterCutCheck);
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = Color.FromArgb(23, 22, 29),
            ForeColor = Color.FromArgb(203, 198, 216),
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f),
            Dock = DockStyle.Fill,
        };
        Theme.ApplyDarkScrollBars(box);
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 48, Width = 924, BackColor = Theme.PageBack };
        var copyButton = Theme.MakeButton("コピー", 690, 8, 100, 32);
        copyButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var closeButton = Theme.MakeButton("閉じる", 800, 8, 100, 32);
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.Click += (_, _) => popup.Close();
        copyButton.Click += (_, _) =>
        {
            var current = losslessRadio.Checked ? lossless : encode;
            if (current == null || current.Value.Items.Count == 0) return;
            // 出力が複数のときは見出しも含めて表示どおりにコピーし、1 つのときはコマンドだけをコピーする。
            var text = current.Value.Items.Count >= 2
                ? box.Text
                : string.Join("\r\n", current.Value.Items.Select(item => item.Command));
            try { Clipboard.SetText(text); } catch { }
        };
        bottomPanel.Controls.Add(copyButton);
        bottomPanel.Controls.Add(closeButton);
        popup.Controls.Add(box);
        popup.Controls.Add(bottomPanel);
        popup.Controls.Add(topPanel);
        var jumpIndices = new List<int>();
        void RefreshText()
        {
            if (popup.IsDisposed) return;
            if (losslessRadio.Checked ? lossless == null : encode == null)
            {
                box.Text = "作成中...";
                box.Update();
                Cursor.Current = Cursors.WaitCursor;
                try
                {
                    if (losslessRadio.Checked) lossless = CaptureCommandLines(false, asterCutCheck.Checked);
                    else encode = CaptureCommandLines(true, asterCutCheck.Checked);
                }
                finally { Cursor.Current = Cursors.Default; }
            }
            var (items, error) = losslessRadio.Checked ? lossless!.Value : encode!.Value;
            jumpIndices.Clear();
            jumpCombo.Items.Clear();
            jumpCombo.Items.Add("各出力を選択");
            jumpIndices.Add(-1);
            if (items.Count == 0)
            {
                box.Text = $"コマンドラインを作成できませんでした。\r\n{error}";
            }
            else
            {
                var sb = new StringBuilder();
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append("\r\n");
                    jumpIndices.Add(sb.Length);
                    jumpCombo.Items.Add($"出力 {i + 1}: {items[i].Description}");
                    sb.Append($"── 出力 {i + 1}: {items[i].Description} ──\r\n");
                    sb.Append(items[i].Command);
                    sb.Append("\r\n");
                }
                box.Text = sb.ToString().TrimEnd('\r', '\n');
            }
            jumpCombo.SelectedIndex = 0;
            box.SelectionStart = 0;
            box.SelectionLength = 0;
        }
        jumpCombo.SelectedIndexChanged += (_, _) =>
        {
            var selected = jumpCombo.SelectedIndex;
            if (selected < 0 || selected >= jumpIndices.Count) return;
            var target = jumpIndices[selected];
            if (target < 0) return;
            box.Select(0, 0);
            box.ScrollToCaret();
            var line = box.GetLineFromCharIndex(target);
            SendMessage(box.Handle, 0x00B6, IntPtr.Zero, (IntPtr)line); // EM_LINESCROLL
            box.Select(target, 0);
        };
        losslessRadio.CheckedChanged += (_, _) => { if (losslessRadio.Checked) RefreshText(); };
        encodeRadio.CheckedChanged += (_, _) => { if (encodeRadio.Checked) RefreshText(); };
        asterCutCheck.CheckedChanged += (_, _) =>
        {
            // 両モードとも作り直しが必要になる。表示中のモードは RefreshText がその場で作り直し、
            // もう一方は切り替え時に作り直す。
            lossless = null;
            encode = null;
            RefreshText();
        };
        RefreshText();
        Theme.ApplyDarkTitleBar(popup);
        popup.ShowDialog(this);
        box.Font.Dispose();
    }

    private void UpdateOutputPathMode()
    {
        // 出力先の表示は全体・区間とも共通になったため、ファイル名表示の更新だけを行う。
        UpdateOutputNameView();
    }

    private string GetOutputDirectory()
    {
        var text = (_outputDirBox?.Text ?? "").Trim().Trim('"');
        if (text.Length == 0) throw new InvalidOperationException("出力先のフォルダーを指定してください。");
        string full;
        try { full = Path.GetFullPath(text); }
        catch { throw new InvalidOperationException("出力先フォルダーのパスが不正です: " + text); }
        if (!Directory.Exists(full)) throw new InvalidOperationException("出力先フォルダーが存在しません: " + full);
        return full;
    }

    private void ShowOutputFileNames()
    {
        if (string.IsNullOrWhiteSpace(_inputPath))
        {
            MessageBox.Show("入力動画を読み込むと、出力するファイル名を確認できます。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var stamp = "";
        if (_appendTimestampCheckBox != null && _appendTimestampCheckBox.Checked)
        {
            stamp = "_" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        }
        var names = _segments.Count > 0
            ? GetGroupOutputFileNames(stamp)
            : new System.Collections.Generic.List<string> { GetWholeOutputFileName(stamp) };

        using var dialogForm = new Form
        {
            Icon = AppIcon.Value,
            Text = $"出力ファイル名の一覧 ({names.Count} 件)",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(420, 240),
            Size = new Size(720, 420),
            SizeGripStyle = SizeGripStyle.Hide,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        var namesBox = new TextBox
        {
            Location = new Point(12, 12),
            Size = new Size(680, 310),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Text = string.Join("\r\n", names),
        };
        dialogForm.Controls.Add(namesBox);
        var closeButton = Theme.MakeButton("閉じる", 572, 334, 120, 32);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        dialogForm.Controls.Add(closeButton);
        dialogForm.CancelButton = closeButton;
        dialogForm.ActiveControl = closeButton;
        Theme.ApplyDarkTitleBar(dialogForm);
        dialogForm.ShowDialog(this);
    }

    private void BrowseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "出力先フォルダーを選択してください。",
            ShowNewFolderButton = true,
        };
        try
        {
            var current = _outputDirBox.Text.Trim().Trim('"');
            if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
            else if (!string.IsNullOrWhiteSpace(_inputPath))
            {
                var inputDir = Path.GetDirectoryName(Path.GetFullPath(_inputPath)) ?? "";
                if (inputDir.Length > 0 && Directory.Exists(inputDir)) dialog.SelectedPath = inputDir;
            }
        }
        catch { }
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _outputDirIsCustom = true;
            SetOutputDirText(FormatDirectoryForDisplay(dialog.SelectedPath));
            WriteLog($"出力先フォルダーを変更しました: {dialog.SelectedPath}");
        }
    }

    // ------------------------------------------------------------------
    // 編集内容の要約
    // ------------------------------------------------------------------

    private void UpdateEditSummary()
    {
        if (_editSummaryLabel == null) return;
        try { _editSummaryLabel.Text = GetEditSummaryText(); } catch { }
    }

    private string GetEditSummaryText()
    {
        // 再エンコードの判定は、出力時の処理分岐と同じ条件にする。
        if (_probe == null || string.IsNullOrWhiteSpace(_inputPath)) return "入力動画を読み込んでください。";
        var speedValue = GetSelectedSpeed();
        var speedChanged = Math.Abs(speedValue - 1.0) >= 0.000001;
        var rotationIndex = GetRotationIndex();
        var totalAudios = _audioStreams.Count;
        var keptAudios = GetSelectedAudioStreams().Count;
        var segmentCount = _segments.Count;
        var clippedCount = 0;
        foreach (var segment in _segments) if (segment.HasClipping) clippedCount++;

        string videoText;
        string audioText;
        if (segmentCount == 0)
        {
            var reencoded = speedChanged && keptAudios > 0;
            videoText = reencoded ? "再エンコード" : "コピー (無劣化)";
            audioText = videoText;
        }
        else
        {
            // 出力ファイル (グループ) ごとに、映像と音声の扱いを数える。
            var losslessFiles = 0;
            var videoOnlyFiles = 0;
            var reencodedFiles = 0;
            foreach (var group in BuildSegmentGroups())
            {
                if (group.Count == 1 && !group[0].HasClipping && (!speedChanged || keptAudios == 0)) losslessFiles++;
                else if (group.Count == 1 && group[0].HasClipping && !speedChanged) videoOnlyFiles++;
                else reencodedFiles++;
            }
            if (reencodedFiles == 0 && videoOnlyFiles == 0)
            {
                videoText = "コピー (無劣化)";
                audioText = videoText;
            }
            else if (losslessFiles == 0 && videoOnlyFiles == 0)
            {
                videoText = "再エンコード";
                audioText = videoText;
            }
            else if (losslessFiles == 0 && reencodedFiles == 0)
            {
                videoText = "再エンコード";
                audioText = "コピー (無劣化)";
            }
            else
            {
                videoText = $"ファイルにより異なる (再エンコード {reencodedFiles + videoOnlyFiles} ファイル / コピー {losslessFiles} ファイル)";
                audioText = $"ファイルにより異なる (再エンコード {reencodedFiles} ファイル / コピー {losslessFiles + videoOnlyFiles} ファイル)";
            }
        }
        if (totalAudios == 0) audioText = "なし (音声トラックがありません)";
        else if (keptAudios == 0) audioText = $"なし (全 {totalAudios} トラックを削除)";
        else if (keptAudios < totalAudios) audioText += $" (一部を削除し {keptAudios}/{totalAudios} トラックを保持)";

        var lines = new System.Collections.Generic.List<string> { $"映像: {videoText} / 音声: {audioText}" };
        var transforms = new System.Collections.Generic.List<string>();
        if (rotationIndex > 0) transforms.Add($"回転: {GetRotationDisplayText(rotationIndex)}");
        if (speedChanged) transforms.Add($"再生速度: {speedValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}x");
        if (transforms.Count > 0) lines.Add(string.Join(" / ", transforms));
        if (segmentCount > 0)
        {
            var counts = new System.Collections.Generic.List<string> { $"通常カット: {segmentCount - clippedCount} 区間" };
            if (clippedCount > 0) counts.Add($"クリッピング: {clippedCount} 区間");
            lines.Add(string.Join(" / ", counts));
            lines.Add($"出力ファイル: {BuildSegmentGroups().Count} 件");
        }
        return string.Join("\r\n", lines);
    }
}
