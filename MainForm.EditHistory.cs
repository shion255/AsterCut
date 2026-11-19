using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// 編集状態の保存形式、編集履歴 (復元ダイアログ)、編集プロジェクト (.acproj)。
/// </summary>
internal sealed partial class MainForm
{
    private EditHistoryEntry? _pendingEditHistoryEntry;

    // ------------------------------------------------------------------
    // 編集状態の取得と適用
    // ------------------------------------------------------------------

    private EditState GetCurrentEditState()
    {
        var segments = new List<EditSegmentState>();
        foreach (var segment in _segments)
        {
            segments.Add(new EditSegmentState
            {
                StartMs = segment.StartMs,
                EndMs = segment.EndMs,
                Top = segment.Top,
                Right = segment.Right,
                Bottom = segment.Bottom,
                Left = segment.Left,
                JoinWithPrevious = segment.JoinWithPrevious,
            });
        }
        var selectedAudio = new List<int>();
        for (var i = 0; i < _audioStreams.Count; i++)
        {
            if (i < _audioList.Items.Count && _audioList.GetItemChecked(i)) selectedAudio.Add(i);
        }
        var clip = _clipCurrent;
        return new EditState
        {
            InputPath = _inputPath,
            Segments = segments,
            SelectedAudioIndices = selectedAudio,
            Speed = _speedValue.ToString("0.00", CultureInfo.InvariantCulture),
            RotationIndex = GetRotationIndex(),
            CutStartText = _cutStartBox.Text,
            CutEndText = _cutEndBox.Text,
            ClipTop = clip.Top,
            ClipRight = clip.Right,
            ClipBottom = clip.Bottom,
            ClipLeft = clip.Left,
            OutputModeIndex = OutputMode,
            OutputModeUserSelected = _outputModeUserSelected,
            VideoCrfText = _videoCrfBox.Text,
            AudioBitrateIndex = _audioBitrateCombo.SelectedIndex >= 0 ? _audioBitrateCombo.SelectedIndex : 2,
            OutputPathText = _outputDirBox.Text,
            OutputPathIsCustom = _outputDirIsCustom,
            SplitOutputDirText = _outputDirBox.Text,
            SplitOutputDirIsCustom = _outputDirIsCustom,
            OutputProfileName = _profileCombo.SelectedItem?.ToString() ?? "",
            VideoFormat = GetSelectedVideoFormat(),
            BitDepth = GetSelectedBitDepth(),
            AudioFormat = GetSelectedAudioFormat(),
            AudioBitrateKbps = GetAudioBitrateKbpsOrDefault(),
            OutputContainer = GetSelectedContainer(),
            AppendTimestamp = _appendTimestampCheckBox.Checked,
            AppendSuffix = _appendSuffixCheckBox.Checked,
            KeepMetadata = _keepMetadataCheckBox.Checked,
            WriteEditorMetadata = _writeEditorMetadataCheckBox.Checked,
            KeepDataBroadcast = _keepDataBroadcastCheckBox.Checked,
        };
    }

    /// <summary>保存された出力先テキストからフォルダーパスを取り出す。ファイルパスが記録されている旧形式ではフォルダー部分だけを使う。</summary>
    private static string ExtractFolderPath(string text)
    {
        var trimmed = text.Trim().Trim('"');
        if (trimmed.Length == 0) return "";
        if (Directory.Exists(trimmed)) return trimmed;
        var ext = Path.GetExtension(trimmed);
        if (ext.Length >= 2 && ext.Length <= 5)
        {
            try { return Path.GetDirectoryName(Path.GetFullPath(trimmed)) ?? trimmed; }
            catch { return trimmed; }
        }
        return trimmed;
    }

    /// <summary>プロジェクトファイルに記録する出力先の表示 (区間出力はフォルダー、全体編集は出力パス)。</summary>
    private string GetProjectOutputPathText()
    {
        try
        {
            if (_segments.Count > 0) return _outputDirBox.Text;
            return GetWholeOutputPath();
        }
        catch { return _outputDirBox.Text; }
    }

    private void ApplyEditState(EditState? state)
    {
        if (state == null) return;
        // 区間 (クリッピング値を含む)
        _segments.Clear();
        ClearSegmentUndoStack();
        if (state.Segments != null)
        {
            foreach (var segment in state.Segments)
            {
                _segments.Add(new Segment
                {
                    StartMs = segment.StartMs,
                    EndMs = segment.EndMs,
                    Top = segment.Top,
                    Right = segment.Right,
                    Bottom = segment.Bottom,
                    Left = segment.Left,
                    JoinWithPrevious = segment.JoinWithPrevious,
                });
            }
        }
        // グループ分けを持たない旧い保存形式は、出力方式が「抽出・結合」なら全区間を 1 グループに読み替える。
        if (state.OutputModeIndex == 1)
        {
            for (var i = 1; i < _segments.Count; i++) _segments[i].JoinWithPrevious = true;
        }
        if (_segments.Count > 0) _segments[0].JoinWithPrevious = false;
        RefreshSegmentList();
        // 保持する音声トラック
        if (state.SelectedAudioIndices != null)
        {
            for (var i = 0; i < _audioStreams.Count; i++)
            {
                if (i < _audioList.Items.Count) _audioList.SetItemChecked(i, state.SelectedAudioIndices.Contains(i));
            }
        }
        // 再生速度・回転 (変更で出力名が再計算されるため、出力先の復元より先に適用する)
        if (state.Speed.Length > 0)
        {
            if (double.TryParse(NormalizeSpeedOption(state.Speed), NumberStyles.Any, CultureInfo.InvariantCulture, out var restoredSpeed)) SetSpeedValue(restoredSpeed);
        }
        if (state.RotationIndex != null)
        {
            var rotationIndex = state.RotationIndex.Value;
            if (rotationIndex >= 0 && rotationIndex < _rotationCombo.Items.Count) _rotationCombo.SelectedIndex = rotationIndex;
        }
        // 開始 / 終了欄とクリッピングの作業値
        if (state.CutStartText.Length > 0) _cutStartBox.Text = state.CutStartText;
        if (state.CutEndText.Length > 0) _cutEndBox.Text = state.CutEndText;
        _clipCurrent = new Segment
        {
            Top = state.ClipTop,
            Right = state.ClipRight,
            Bottom = state.ClipBottom,
            Left = state.ClipLeft,
        };
        // 出力設定 (数値・チェック)。フォーマット類を先に適用すると、ビット数・音質・コンテナの選択肢が連動して組み替わる。
        _profileControlsUpdating = true;
        try
        {
            if (state.VideoFormat.Length > 0) SetComboText(_videoFormatCombo, state.VideoFormat, "HEVC");
            RebuildBitDepthItems();
            if (state.BitDepth != null) SetBitDepth(state.BitDepth.Value);
            if (state.AudioFormat.Length > 0) SetComboText(_audioFormatCombo, state.AudioFormat, "Opus");
            RebuildAudioBitrateItems();
            UpdateAudioBitrateToolTip();
            if (state.AudioBitrateKbps != null) SetAudioBitrateKbps(state.AudioBitrateKbps.Value);
            else if (state.AudioBitrateIndex != null)
            {
                var bitrateIndex = state.AudioBitrateIndex.Value;
                if (bitrateIndex >= 0 && bitrateIndex < _audioBitrateCombo.Items.Count) _audioBitrateCombo.SelectedIndex = bitrateIndex;
            }
            RebuildContainerItems();
            if (state.OutputContainer.Length > 0) SetComboText(_containerCombo, state.OutputContainer, "MKV");
            if (state.OutputProfileName.Length > 0 && _profileCombo.Items.Contains(state.OutputProfileName))
            {
                _profileCombo.SelectedItem = state.OutputProfileName;
            }
        }
        finally { _profileControlsUpdating = false; }
        if (state.VideoCrfText.Length > 0)
        {
            // ホイール操作の内部値もそろえるため、数値として解釈できるときは SetCrfValue で適用する。
            if (double.TryParse(state.VideoCrfText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var restoredCrf)) SetCrfValue(restoredCrf);
            else _videoCrfBox.Text = state.VideoCrfText;
        }
        UpdateProfileDirtyMark();
        if (state.AppendTimestamp != null) _appendTimestampCheckBox.Checked = state.AppendTimestamp.Value;
        if (state.AppendSuffix != null) _appendSuffixCheckBox.Checked = state.AppendSuffix.Value;
        if (state.KeepMetadata != null) _keepMetadataCheckBox.Checked = state.KeepMetadata.Value;
        if (state.WriteEditorMetadata != null) _writeEditorMetadataCheckBox.Checked = state.WriteEditorMetadata.Value;
        if (state.KeepDataBroadcast != null) _keepDataBroadcastCheckBox.Checked = state.KeepDataBroadcast.Value;
        // 出力する処理は区間とグループ分けから決まるため、ここでは選択済みフラグだけを復元する。
        _outputModeUserSelected = state.OutputModeUserSelected;
        // 出力先フォルダー: 新形式はフォルダーパスそのもの、旧形式 (ファイルパス) はフォルダー部分だけを使う。
        var folderText = state.SplitOutputDirText.Length > 0 ? state.SplitOutputDirText : state.OutputPathText;
        if (folderText.Length > 0)
        {
            _outputDirIsCustom = state.SplitOutputDirIsCustom || state.OutputPathIsCustom;
            SetOutputDirText(FormatDirectoryForDisplay(ExtractFolderPath(folderText)));
        }
        else
        {
            _outputDirIsCustom = false;
        }
        UpdateOutputPathMode();
        RefreshSegmentList();
    }

    // ------------------------------------------------------------------
    // 編集履歴の保留と保存
    // ------------------------------------------------------------------

    private void CapturePendingEditHistory(string outputPath)
    {
        // 出力開始時に、その時点の編集状態を控える。実際に履歴へ保存するのは出力の成功時だけ。
        _pendingEditHistoryEntry = null;
        if (string.IsNullOrWhiteSpace(_inputPath)) return;
        try
        {
            // 抽出・分割では「フォルダー + ファイル名一覧」の要約文が渡されるため、先頭行だけを記録する。
            var outputForHistory = outputPath.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            _pendingEditHistoryEntry = new EditHistoryEntry
            {
                SavedAt = "",
                InputPath = _inputPath,
                OutputPath = outputForHistory,
                State = GetCurrentEditState(),
            };
        }
        catch (Exception ex)
        {
            _pendingEditHistoryEntry = null;
            WriteLog($"編集履歴の記録準備に失敗: {ex.Message}");
        }
    }

    private void SavePendingEditHistory()
    {
        var entry = _pendingEditHistoryEntry;
        _pendingEditHistoryEntry = null;
        if (entry == null) return;
        try
        {
            entry.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            // 全く同じ内容の履歴があれば、重複させずに古い方を取り除いて新しい方で上書きする。
            EditHistoryEntry? duplicate = null;
            foreach (var candidate in _settings.EditHistory)
            {
                if (IsSameHistoryContent(candidate, entry)) { duplicate = candidate; break; }
            }
            if (duplicate != null)
            {
                _settings.EditHistory.Remove(duplicate);
                WriteLog("同じ内容の編集履歴があったため、古い方を上書きしました。");
            }
            _settings.EditHistory.Insert(0, entry);
            while (_settings.EditHistory.Count > _settings.EditHistoryMaxCount)
            {
                _settings.EditHistory.RemoveAt(_settings.EditHistory.Count - 1);
            }
            SaveSettingsFromUi();
            WriteLog($"編集履歴を保存: 日時={entry.SavedAt}; 入力={entry.InputPath}; 出力={entry.OutputPath}");
        }
        catch (Exception ex)
        {
            WriteLog($"編集履歴の保存に失敗: {ex.Message}");
        }
    }

    private void RemoveEditHistoryEntry(EditHistoryEntry entry)
    {
        _settings.EditHistory.RemoveAll(candidate => ReferenceEquals(candidate, entry));
        SaveSettingsFromUi();
    }

    /// <summary>保存日時を除き、履歴の内容 (入力・出力先・編集状態) が全く同じかどうかを返す。</summary>
    private static bool IsSameHistoryContent(EditHistoryEntry a, EditHistoryEntry b)
    {
        if (!string.Equals(a.InputPath, b.InputPath, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(a.OutputPath, b.OutputPath, StringComparison.OrdinalIgnoreCase)) return false;
        var aJson = a.State == null ? "" : JsonSerializer.Serialize(a.State);
        var bJson = b.State == null ? "" : JsonSerializer.Serialize(b.State);
        return string.Equals(aJson, bJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 履歴・プロジェクトの復元
    // ------------------------------------------------------------------

    private async Task RestoreEditHistoryEntryAsync(EditHistoryEntry? entry, string logLabel = "編集履歴を復元")
    {
        if (entry == null) return;
        if (_jobRunning)
        {
            MessageBox.Show("処理中は編集履歴を復元できません。処理が終わってから実行してください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var inputPath = entry.InputPath;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show($"履歴の入力動画が見つかりません。移動または削除された可能性があります。\r\n\r\n{inputPath}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (IsEditingInProgress())
        {
            var answer = MessageBox.Show("編集中の内容を、選択した履歴の内容で置き換えますか？\r\n\r\n現在の編集内容は出力していないため、履歴には保存されません。", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
        }
        await LoadInputFileAsync(inputPath);
        var loadedPath = "";
        try { loadedPath = Path.GetFullPath(_inputPath); } catch { }
        if (_probe == null || loadedPath.Length == 0 || !loadedPath.Equals(Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("履歴の入力動画を読み込めませんでした。編集状態は復元されませんでした。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        ApplyEditState(entry.State);
        WriteLog($"{logLabel}: 保存日時={entry.SavedAt}; 入力={entry.InputPath}; 出力={entry.OutputPath}");
        SetStatus("編集履歴を復元しました");
        _tabs.SelectedTab = _cutTab;
    }

    private void ShowEditHistoryDialog()
    {
        if (_settings.EditHistory.Count == 0)
        {
            MessageBox.Show("編集履歴はまだありません。\r\n\r\n動画を出力すると、そのときの編集状態が履歴に保存されます。出力していない編集は保存されません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialogForm = new Form
        {
            Icon = AppIcon.Value,
            Text = "編集履歴の復元",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(720, 360),
            Size = new Size(900, 460),
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        var listView = new ListView
        {
            Location = new Point(12, 12),
            Size = new Size(860, 350),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = true,
            HideSelection = false,
            ShowItemToolTips = true,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
        };
        listView.Columns.Add("日時", 150);
        listView.Columns.Add("入力動画", 430);
        listView.Columns.Add("出力先", 260);
        // 前回に調整した列幅を復元する。
        if (_settings.EditHistoryColumnWidths.Count == 3)
        {
            for (var i = 0; i < 3; i++) listView.Columns[i].Width = _settings.EditHistoryColumnWidths[i];
        }
        // 閉じるときに、現在の列幅を設定へ保存する。
        dialogForm.FormClosed += (_, _) =>
        {
            _settings.EditHistoryColumnWidths = new List<int>
            {
                listView.Columns[0].Width,
                listView.Columns[1].Width,
                listView.Columns[2].Width,
            };
            _settings.Save();
        };
        Theme.ApplyDarkScrollBars(listView);
        foreach (var historyEntry in _settings.EditHistory)
        {
            var item = new ListViewItem(historyEntry.SavedAt)
            {
                Tag = historyEntry,
                ToolTipText = historyEntry.InputPath,
            };
            item.SubItems.Add(historyEntry.InputPath);
            item.SubItems.Add(historyEntry.OutputPath);
            listView.Items.Add(item);
        }
        if (listView.Items.Count > 0) listView.Items[0].Selected = true;
        dialogForm.Controls.Add(listView);

        var restoreButton = Theme.MakeButton("復元", 12, 374, 120, 32);
        restoreButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        dialogForm.Controls.Add(restoreButton);
        dialogForm.AcceptButton = restoreButton;
        var deleteButton = Theme.MakeButton("選択した履歴を削除", 140, 374, 170, 32);
        deleteButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        dialogForm.Controls.Add(deleteButton);
        var selectionCountLabel = Theme.MakeLabel("", 318, 377, 200, 26);
        selectionCountLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        dialogForm.Controls.Add(selectionCountLabel);
        var closeButton = Theme.MakeButton("閉じる", 752, 374, 120, 32);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        dialogForm.Controls.Add(closeButton);
        dialogForm.CancelButton = closeButton;

        var maxCountLabel = Theme.MakeLabel("保存する履歴数:", 536, 377, 124, 26);
        maxCountLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        dialogForm.Controls.Add(maxCountLabel);
        var maxCountBox = new NumericUpDown
        {
            Location = new Point(664, 376),
            Size = new Size(64, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Minimum = 1,
            Maximum = 100,
            Value = _settings.EditHistoryMaxCount,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.MenuFore,
        };
        dialogForm.Controls.Add(maxCountBox);
        // 数値欄の中の先頭余白も、他の入力欄と同じ 2px にする。
        // 内側の TextBox 自身の HandleCreated で適用する (親の HandleCreated 時点では内側のハンドルがまだ無いことがあるため)。
        foreach (Control child in maxCountBox.Controls)
        {
            if (child is TextBox inner)
            {
                inner.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(inner, 2, 0);
                ApplyTextBoxMarginsOnce(inner, 2, 0);
            }
        }

        void UpdateSelectionState()
        {
            var selectedCount = listView.SelectedItems.Count;
            restoreButton.Enabled = selectedCount == 1;
            selectionCountLabel.Text = selectedCount >= 2 ? $"{selectedCount} 件選択中" : "";
        }
        listView.SelectedIndexChanged += (_, _) => UpdateSelectionState();

        void RestoreAction()
        {
            if (listView.SelectedItems.Count != 1) return;
            var selectedEntry = (EditHistoryEntry)listView.SelectedItems[0].Tag!;
            dialogForm.Close();
            _ = RestoreEditHistoryEntryAsync(selectedEntry);
        }
        restoreButton.Click += (_, _) => RestoreAction();
        listView.DoubleClick += (_, _) => RestoreAction();
        deleteButton.Click += (_, _) =>
        {
            if (listView.SelectedItems.Count == 0) return;
            var selectedItems = new List<ListViewItem>();
            foreach (ListViewItem selectedItem in listView.SelectedItems) selectedItems.Add(selectedItem);
            foreach (var selectedItem in selectedItems)
            {
                RemoveEditHistoryEntry((EditHistoryEntry)selectedItem.Tag!);
                listView.Items.Remove(selectedItem);
            }
            WriteLog($"編集履歴を {selectedItems.Count} 件削除しました。");
            UpdateSelectionState();
        };
        UpdateSelectionState();
        closeButton.Click += (_, _) => dialogForm.Close();
        maxCountBox.ValueChanged += (_, _) =>
        {
            var newMaxCount = (int)maxCountBox.Value;
            if (newMaxCount == _settings.EditHistoryMaxCount) return;
            _settings.EditHistoryMaxCount = newMaxCount;
            // 件数を減らした場合は、古い履歴から切り捨てて一覧表示にも反映する。
            while (_settings.EditHistory.Count > _settings.EditHistoryMaxCount)
            {
                _settings.EditHistory.RemoveAt(_settings.EditHistory.Count - 1);
                if (listView.Items.Count > 0) listView.Items.RemoveAt(listView.Items.Count - 1);
            }
            SaveSettingsFromUi();
            WriteLog($"編集履歴の最大保存件数を {_settings.EditHistoryMaxCount} 件に変更しました。");
        };
        Theme.ApplyDarkTitleBar(dialogForm);
        dialogForm.ShowDialog(this);
    }

    // ------------------------------------------------------------------
    // 編集プロジェクト (.acproj)
    // ------------------------------------------------------------------

    private static string GetEditProjectDefaultFileName(string inputPath, DateTime now)
    {
        // プロジェクトファイルの初期ファイル名: 入力ファイル名 (拡張子なし) _(保存時刻).acproj
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "project";
        var stamp = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return $"{baseName}_({stamp}).acproj";
    }

    private void SaveEditProject()
    {
        if (_jobRunning)
        {
            MessageBox.Show("処理中は編集プロジェクトを保存できません。処理が終わってから実行してください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_inputPath))
        {
            MessageBox.Show("入力動画を読み込んでいないため、編集プロジェクトを保存できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "編集プロジェクトを保存",
            Filter = "AsterCut プロジェクト (*.acproj)|*.acproj|すべてのファイル (*.*)|*.*",
            DefaultExt = "acproj",
            AddExtension = true,
            OverwritePrompt = true,
        };
        try
        {
            var inputDir = Path.GetDirectoryName(Path.GetFullPath(_inputPath)) ?? "";
            if (inputDir.Length > 0 && Directory.Exists(inputDir)) dialog.InitialDirectory = inputDir;
        }
        catch { }
        dialog.FileName = GetEditProjectDefaultFileName(_inputPath, DateTime.Now);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var savePath = dialog.FileName;
        try
        {
            var project = new EditProject
            {
                Format = "AsterCut Project",
                Version = 1,
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                InputPath = _inputPath,
                OutputPath = GetProjectOutputPathText(),
                State = GetCurrentEditState(),
            };
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            var json = JsonSerializer.Serialize(project, options);
            File.WriteAllText(savePath, json + Environment.NewLine, new UTF8Encoding(false));
            WriteLog($"編集プロジェクトを保存: {savePath}");
            SetStatus("プロジェクトを保存しました");
            MessageBox.Show($"編集プロジェクトを保存しました。\r\n\r\n{savePath}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"編集プロジェクトの保存に失敗しました。\r\n\r\n{ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed class EditProject
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public string SavedAt { get; set; } = "";
        public string InputPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public EditState? State { get; set; }
    }

    private async Task OpenEditProjectAsync()
    {
        if (_jobRunning)
        {
            MessageBox.Show("処理中は編集プロジェクトを開けません。処理が終わってから実行してください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var dialog = new OpenFileDialog
        {
            Title = "編集プロジェクトを開く",
            Filter = "AsterCut プロジェクト (*.acproj)|*.acproj|すべてのファイル (*.*)|*.*",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(_inputPath))
        {
            try
            {
                var inputDir = Path.GetDirectoryName(Path.GetFullPath(_inputPath)) ?? "";
                if (inputDir.Length > 0 && Directory.Exists(inputDir)) dialog.InitialDirectory = inputDir;
            }
            catch { }
        }
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var projectPath = dialog.FileName;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(projectPath, Encoding.UTF8));
        }
        catch
        {
            MessageBox.Show($"編集プロジェクトを開けませんでした。ファイルが壊れているか、プロジェクトファイルではありません。\r\n\r\n{projectPath}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            EditHistoryEntry? entry = null;
            var format = root.TryGetProperty("Format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String
                ? formatElement.GetString() ?? ""
                : "";
            var hasState = root.TryGetProperty("State", out var stateElement) && stateElement.ValueKind == JsonValueKind.Object;
            var projectInput = root.TryGetProperty("InputPath", out var inputElement) && inputElement.ValueKind == JsonValueKind.String
                ? inputElement.GetString() ?? ""
                : "";
            var isProject = hasState && !string.IsNullOrWhiteSpace(projectInput);
            if (isProject && format.Length > 0 && format != "AsterCut Project") isProject = false;
            if (!isProject)
            {
                MessageBox.Show($"編集プロジェクトを開けませんでした。プロジェクトファイルではありません。\r\n\r\n{projectPath}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (root.TryGetProperty("Version", out var versionElement) && versionElement.TryGetInt32(out var version) && version > 1)
            {
                MessageBox.Show("このプロジェクトファイルは、対応していないバージョンで保存されています。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            entry = AppSettings.ParseEntry(root);
            if (entry == null) return;
            WriteLog($"編集プロジェクトを開く: {projectPath}");
            await RestoreEditHistoryEntryAsync(entry, "編集プロジェクトを復元");
        }
    }
}
