using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;

namespace AsterCut;

/// <summary>
/// チャプタータブ (.xchp の読み込み・編集・出力) と、
/// 動画編集タブとのプレビュー共有 (MovePreviewControls)。
/// </summary>
internal sealed partial class MainForm
{
    private sealed class Chapter
    {
        public long StartMs;
        public string Name = "";
    }

    private readonly List<Chapter> _chapters = new();
    private int _chapterSelectedIndex = -1;
    private int _chapterContextIndex = -1;

    private TextBox _chapterPathBox = null!;
    private Button _chapterHistoryButton = null!;
    private TextBox _chapterStartBox = null!;
    private Panel _chapterGroup = null!;
    private Panel _chapterRowsPanel = null!;
    private TextBox _chapterOutputPathBox = null!;
    private CheckBox _chapterTimestampCheckBox = null!;
    private ContextMenuStrip _chapterContextMenu = null!;

    private void BuildChapterTab()
    {
        // 入力チャプター (入力動画ボックスと同じ仕様: パスの表示、ドラッグ＆ドロップ、
        // 入力履歴、「参照...」「読み込み」)。今読み込まれているチャプターファイルのパスを表示する。
        var chapterPathLabel = Theme.MakeLabel("入力チャプター:", 539, 16, 102);
        chapterPathLabel.Font = Font;
        chapterPathLabel.Width = chapterPathLabel.PreferredWidth;
        _chapterTab.Controls.Add(chapterPathLabel);
        var chapterPathBoxX = 539 + chapterPathLabel.PreferredWidth + 1;
        _chapterPathBox = Theme.MakeTextBox(chapterPathBoxX, 15, 914 - chapterPathBoxX);
        _chapterPathBox.AllowDrop = true;
        _chapterPathBox.DragEnter += ChapterFileDragEnter;
        _chapterPathBox.DragOver += ChapterFileDragOver;
        _chapterPathBox.DragDrop += ChapterFileDragDrop;
        _chapterPathBox.KeyDown += (_, e) =>
        {
            if (e.Alt && e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ShowChapterHistoryPopup();
            }
        };
        _chapterPathBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_chapterPathBox, 2, 26);
        ApplyTextBoxMarginsOnce(_chapterPathBox, 2, 26);
        _chapterTab.Controls.Add(_chapterPathBox);
        _chapterHistoryButton = Theme.MakeButton("▼", 890, 16, 22, 24);
        _chapterHistoryButton.FlatAppearance.BorderSize = 0;
        _chapterHistoryButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 54, 72);
        _chapterHistoryButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(86, 74, 110);
        _chapterHistoryButton.BackColor = Theme.BoxBack;
        _chapterHistoryButton.ForeColor = Theme.MutedFore;
        _chapterHistoryButton.Font = new Font("Meiryo UI", 8f);
        _chapterHistoryButton.TabStop = false;
        var chapterHistoryToolTip = new ToolTip();
        chapterHistoryToolTip.SetToolTip(_chapterHistoryButton, "入力チャプターの履歴を表示 (最大10件)");
        _chapterHistoryButton.Click += (_, _) =>
        {
            // ポップアップを閉じるためにボタンが押された直後の Click は飲み込む (入力動画の履歴と同じ)。
            if (DateTime.UtcNow < _historyToggleSuppressedUntil)
            {
                _historyToggleSuppressedUntil = DateTime.MinValue;
                return;
            }
            ShowChapterHistoryPopup();
        };
        _chapterTab.Controls.Add(_chapterHistoryButton);
        _chapterHistoryButton.BringToFront();
        var chapterBrowseButton = Theme.MakeButton("参照...", 920, 14, 78, 30);
        chapterBrowseButton.Click += (_, _) => BrowseChapterFile();
        _chapterTab.Controls.Add(chapterBrowseButton);
        var chapterLoadButton = Theme.MakeButton("読み込み", 1004, 14, 105, 30);
        chapterLoadButton.Click += (_, _) => LoadChapterRequested();
        _chapterTab.Controls.Add(chapterLoadButton);

        var chapterStartLabel = Theme.MakeLabel("開始:", 525, 58, 48);
        chapterStartLabel.Font = Font;
        chapterStartLabel.Width = chapterStartLabel.PreferredWidth;
        _chapterTab.Controls.Add(chapterStartLabel);
        var chapterStartBoxX = 525 + chapterStartLabel.PreferredWidth + 1;
        _chapterStartBox = Theme.MakeTextBox(chapterStartBoxX, 57, 125);
        _chapterStartBox.Text = "00:00:00.000";
        _chapterStartBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_chapterStartBox, 2, 0);
        _chapterTab.Controls.Add(_chapterStartBox);
        var setCurrentStartButton = Theme.MakeButton("現在位置を開始に設定", chapterStartBoxX + 135, 56, 170, 30);
        setCurrentStartButton.Click += (_, _) =>
        {
            if (_previewReady)
            {
                var ms = GetPreviewCurrentPositionMs();
                _chapterStartBox.Text = TimeText.FormatClockFromMs(ms);
                WriteLog($"チャプター開始位置を設定: {TimeText.FormatClockFromMs(ms)} ({GetFrameInfoText(ms)})");
            }
            else
            {
                MessageBox.Show("プレビューが準備できていません。", AppTitle);
            }
        };
        _chapterTab.Controls.Add(setCurrentStartButton);
        var addButton = Theme.MakeButton("チャプターを追加", chapterStartBoxX + 313, 56, 126, 32);
        addButton.Click += (_, _) => AddChapter();
        _chapterTab.Controls.Add(addButton);
        var removeButton = Theme.MakeButton("選択を削除", chapterStartBoxX + 447, 56, 105, 32);
        removeButton.Click += (_, _) =>
        {
            var idx = _chapterSelectedIndex;
            if (idx >= 0 && idx < _chapters.Count)
            {
                var removed = _chapters[idx];
                _chapters.RemoveAt(idx);
                _chapterSelectedIndex = -1;
                RefreshChapterList();
                if (_chapters.Count > 0) SelectChapterRow(Math.Min(idx, _chapters.Count - 1));
                WriteLog($"チャプターを削除: start={TimeText.FormatClockFromMs(removed.StartMs)}");
            }
        };
        _chapterTab.Controls.Add(removeButton);
        var popupButton = Theme.MakeButton("ポップアップ", 272, 357, 115, 30);
        popupButton.Click += (_, _) => ShowPreviewPopup();
        _chapterTab.Controls.Add(popupButton);
        // 出力先は、動画編集タブで回転・速度の欄があった位置に置く。
        _chapterTab.Controls.Add(Theme.MakeLabel("出力先:", 14, 432, 60));
        _chapterOutputPathBox = Theme.MakeTextBox(14, 458, 386);
        _chapterTab.Controls.Add(_chapterOutputPathBox);
        _chapterOutputPathBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(_chapterOutputPathBox, 2, 0);
        ApplyTextBoxMarginsOnce(_chapterOutputPathBox, 2, 0);
        var browseFolderButton = Theme.MakeButton("フォルダー...", 408, 456, 100, 30);
        browseFolderButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "チャプターファイルの出力先フォルダー" };
            var current = _chapterOutputPathBox.Text.Trim();
            if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _chapterOutputPathBox.Text = dialog.SelectedPath;
            }
        };
        _chapterTab.Controls.Add(browseFolderButton);
        _chapterTimestampCheckBox = new CheckBox
        {
            Text = "出力ファイル名の末尾に現在時刻を追加",
            Location = new Point(14, 492),
            Size = new Size(494, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
        };
        _chapterTab.Controls.Add(_chapterTimestampCheckBox);
        var outputButton = Theme.MakeButton("MPC-HC チャプターを出力 (.xchp)", 14, 526, 250, 34);
        outputButton.Click += (_, _) => ExportChapters();
        _chapterTab.Controls.Add(outputButton);
        var mkvXmlButton = Theme.MakeButton("MKV チャプターを出力 (.xml)", 272, 526, 234, 34);
        mkvXmlButton.Click += (_, _) => ExportMkvChaptersXml();
        _chapterTab.Controls.Add(mkvXmlButton);

        _chapterGroup = new Panel
        {
            Location = new Point(520, 101),
            Size = new Size(510, 551),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.PageBack,
        };
        _chapterTab.Controls.Add(_chapterGroup);
        var chapterTitle = Theme.MakeLabel("チャプター 一覧", 12, 2, 480, 23);
        chapterTitle.Font = new Font(Font, FontStyle.Bold);
        chapterTitle.BackColor = Theme.PageBack;
        chapterTitle.Width = chapterTitle.PreferredWidth;
        _chapterGroup.Controls.Add(chapterTitle);
        _chapterRowsPanel = new Panel
        {
            Location = new Point(12, 28),
            Size = new Size(486, 511),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
            AutoScroll = true,
        };
        _chapterGroup.Controls.Add(_chapterRowsPanel);
        // チャプターファイルは、一覧の内枠へドラッグ＆ドロップしても読み込める。
        _chapterRowsPanel.AllowDrop = true;
        _chapterRowsPanel.DragEnter += ChapterFileDragEnter;
        _chapterRowsPanel.DragOver += ChapterFileDragOver;
        _chapterRowsPanel.DragDrop += ChapterFileDragDrop;
        _chapterRowsPanel.Resize += (_, _) =>
        {
            // スクロールバーの有無で変わる表示幅に、行と名前欄の幅を合わせる。
            foreach (Control rowPanel in _chapterRowsPanel.Controls)
            {
                rowPanel.Width = _chapterRowsPanel.ClientSize.Width;
                foreach (Control rowChild in rowPanel.Controls)
                {
                    if (rowChild is TextBox nameBox) nameBox.Width = Math.Max(50, rowPanel.Width - 118);
                }
            }
        };

        _chapterContextMenu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()) };
        var overwriteItem = new ToolStripMenuItem("上書き") { ForeColor = Theme.MenuFore };
        overwriteItem.Click += (_, _) => OverwriteChapter(_chapterContextIndex);
        _chapterContextMenu.Items.Add(overwriteItem);
    }

    // ------------------------------------------------------------------
    // チャプターの状態
    // ------------------------------------------------------------------

    private int FindChapterIndexByStartMs(long startMs)
    {
        for (var i = 0; i < _chapters.Count; i++)
        {
            if (_chapters[i].StartMs == startMs) return i;
        }
        return -1;
    }

    private void SortChapters()
    {
        if (_chapters.Count < 2) return;
        var sorted = _chapters.OrderBy(c => c.StartMs).ToList();
        _chapters.Clear();
        _chapters.AddRange(sorted);
    }

    private void RefreshChapterList()
    {
        // チャプター一覧は、行ごとに「開始時刻ラベル + 名前を直接編集できる欄」を並べて作る。
        long selectedStart = -1;
        if (_chapterSelectedIndex >= 0 && _chapterSelectedIndex < _chapters.Count)
        {
            selectedStart = _chapters[_chapterSelectedIndex].StartMs;
        }
        _chapterRowsPanel.SuspendLayout();
        try
        {
            var oldRows = _chapterRowsPanel.Controls.Cast<Control>().ToList();
            _chapterRowsPanel.Controls.Clear();
            foreach (var oldRow in oldRows) oldRow.Dispose();
            _chapterSelectedIndex = -1;
            var rowWidth = Math.Max(120, _chapterRowsPanel.ClientSize.Width);
            for (var i = 0; i < _chapters.Count; i++)
            {
                var index = i;
                var chapter = _chapters[i];
                var rowPanel = new Panel
                {
                    Location = new Point(0, i * 28),
                    Size = new Size(rowWidth, 28),
                    BackColor = Theme.BoxBack,
                    Tag = i,
                };
                var timeLabel = Theme.MakeLabel(TimeText.FormatClockFromMs(chapter.StartMs), 6, 4, 100, 20);
                timeLabel.BackColor = Color.Transparent;
                timeLabel.Tag = i;
                rowPanel.Controls.Add(timeLabel);
                var nameBox = Theme.MakeTextBox(110, 1, Math.Max(50, rowWidth - 118));
                nameBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(nameBox, 2, 0);
                ApplyTextBoxMarginsOnce(nameBox, 2, 0);
                nameBox.Text = chapter.Name;
                rowPanel.Controls.Add(nameBox);
                _chapterRowsPanel.Controls.Add(rowPanel);

                rowPanel.AllowDrop = true;
                rowPanel.DragEnter += ChapterFileDragEnter;
                rowPanel.DragOver += ChapterFileDragOver;
                rowPanel.DragDrop += ChapterFileDragDrop;
                timeLabel.AllowDrop = true;
                timeLabel.DragEnter += ChapterFileDragEnter;
                timeLabel.DragOver += ChapterFileDragOver;
                timeLabel.DragDrop += ChapterFileDragDrop;
                timeLabel.Click += (_, _) => SelectChapterRow(index);
                timeLabel.DoubleClick += (_, _) =>
                {
                    SelectChapterRow(index);
                    if (index >= 0 && index < _chapters.Count)
                    {
                        var hitChapter = _chapters[index];
                        _chapterStartBox.Text = TimeText.FormatClockFromMs(hitChapter.StartMs);
                        if (_previewReady) SetPlayerPosition(TimeSpan.FromMilliseconds(hitChapter.StartMs));
                    }
                };
                void RowMouseDown(object? sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Right) return;
                    SelectChapterRow(index);
                    _chapterContextIndex = index;
                    _chapterContextMenu.Show((Control)sender!, e.Location);
                }
                timeLabel.MouseDown += RowMouseDown;
                rowPanel.MouseDown += RowMouseDown;
                nameBox.TextChanged += (_, _) => { chapter.Name = nameBox.Text; };
                nameBox.Enter += (_, _) => SelectChapterRow(index);
                if (chapter.StartMs == selectedStart) _chapterSelectedIndex = i;
            }
        }
        finally
        {
            _chapterRowsPanel.ResumeLayout();
        }
        UpdateChapterRowColors();
    }

    private void SelectChapterRow(int index)
    {
        _chapterSelectedIndex = index;
        UpdateChapterRowColors();
    }

    private void UpdateChapterRowColors()
    {
        foreach (Control rowPanel in _chapterRowsPanel.Controls)
        {
            rowPanel.BackColor = rowPanel.Tag is int tag && tag == _chapterSelectedIndex
                ? Color.FromArgb(86, 74, 110)
                : Theme.BoxBack;
        }
    }

    private void FocusChapterNameBox(int index)
    {
        if (index < 0 || index >= _chapterRowsPanel.Controls.Count) return;
        var rowPanel = _chapterRowsPanel.Controls[index];
        foreach (Control rowChild in rowPanel.Controls)
        {
            if (rowChild is TextBox nameBox)
            {
                nameBox.Focus();
                nameBox.SelectionStart = nameBox.Text.Length;
                nameBox.SelectionLength = 0;
                return;
            }
        }
    }

    private void ClearChapters()
    {
        _chapters.Clear();
        RefreshChapterList();
    }

    private void AddChapter()
    {
        try
        {
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            var start = ConvertTimeToMs(_chapterStartBox.Text);
            if (_durationMs > 0 && start > _durationMs) throw new InvalidOperationException("開始時刻が動画の長さを超えています。");
            var existingIndex = FindChapterIndexByStartMs(start);
            if (existingIndex >= 0)
            {
                SelectChapterRow(existingIndex);
                throw new InvalidOperationException("同じ開始時刻のチャプターがすでにあります。");
            }
            _chapters.Add(new Chapter { StartMs = start, Name = "" });
            SortChapters();
            RefreshChapterList();
            var addedIndex = FindChapterIndexByStartMs(start);
            if (addedIndex >= 0)
            {
                SelectChapterRow(addedIndex);
                FocusChapterNameBox(addedIndex);
            }
            WriteLog($"チャプターを追加: start={TimeText.FormatClockFromMs(start)} ({start} ms)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OverwriteChapter(int index)
    {
        try
        {
            if (index < 0 || index >= _chapters.Count) return;
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            var start = ConvertTimeToMs(_chapterStartBox.Text);
            if (_durationMs > 0 && start > _durationMs) throw new InvalidOperationException("開始時刻が動画の長さを超えています。");
            var duplicateIndex = FindChapterIndexByStartMs(start);
            if (duplicateIndex >= 0 && duplicateIndex != index) throw new InvalidOperationException("同じ開始時刻のチャプターがすでにあります。");
            var oldStart = _chapters[index].StartMs;
            _chapters[index].StartMs = start;
            SortChapters();
            RefreshChapterList();
            var newIndex = FindChapterIndexByStartMs(start);
            if (newIndex >= 0) SelectChapterRow(newIndex);
            WriteLog($"チャプターを上書き: start={TimeText.FormatClockFromMs(oldStart)} -> {TimeText.FormatClockFromMs(start)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ------------------------------------------------------------------
    // .xchp の読み書き
    // ------------------------------------------------------------------

    private void ChapterFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void ChapterFileDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void ChapterFileDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) LoadChapterFile(files[0]);
    }

    // 入力チャプターボックスの「読み込み」。
    private void LoadChapterRequested()
    {
        var path = _chapterPathBox.Text.Trim();
        if (path.Length == 0)
        {
            SetStatus("入力チャプターを選択してください。");
            return;
        }
        LoadChapterFile(path);
    }

    private void BrowseChapterFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "チャプターファイル|*.xchp;*.xml|すべてのファイル (*.*)|*.*",
            Title = "入力チャプターを選択",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadChapterFile(dialog.FileName);
    }

    // チャプターファイル (.xchp / Matroska チャプター XML) を読み込んで一覧へ反映する。
    private void LoadChapterFile(string path)
    {
        path = path.Trim().Trim('"');
        if (!File.Exists(path))
        {
            MessageBox.Show("チャプターファイルが見つかりません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var extension = Path.GetExtension(path);
        var isXml = extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
        if (!isXml && !extension.Equals(".xchp", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("対応していないチャプターファイルです (.xchp / .xml)。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var parsed = isXml ? ParseMkvChaptersXml(path) : ParseXchpChapterFile(path);
            _chapters.Clear();
            foreach (var chapter in parsed)
            {
                if (FindChapterIndexByStartMs(chapter.StartMs) >= 0) continue;
                _chapters.Add(chapter);
            }
            SortChapters();
            RefreshChapterList();
            _chapterPathBox.Text = path;
            AddChapterInputHistory(path);
            WriteLog($"チャプターファイルを読み込み: {path} ({_chapters.Count} 件)");
            SetStatus("チャプターファイルを読み込みました");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"チャプターファイルを読み込めませんでした: {ex.Message}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static List<Chapter> ParseXchpChapterFile(string path)
    {
        var chapters = new List<Chapter>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parsed = ConvertFromXchpLine(line);
            if (parsed != null) chapters.Add(parsed);
        }
        return chapters;
    }

    // Matroska チャプター XML (「MKV チャプターを出力 (.xml)」や mkvmerge が書き出す形式) を読み込む。
    private static List<Chapter> ParseMkvChaptersXml(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        var chapters = new List<Chapter>();
        var atoms = doc.SelectNodes("//ChapterAtom");
        if (atoms == null) return chapters;
        foreach (XmlNode atom in atoms)
        {
            var startText = atom.SelectSingleNode("ChapterTimeStart")?.InnerText;
            if (startText == null || !TryParseMkvChapterTime(startText, out var startMs)) continue;
            var name = atom.SelectSingleNode("ChapterDisplay/ChapterString")?.InnerText ?? "";
            chapters.Add(new Chapter { StartMs = startMs, Name = name });
        }
        return chapters;
    }

    private static bool TryParseMkvChapterTime(string text, out long startMs)
    {
        startMs = 0;
        var match = Regex.Match(text.Trim(), @"^(\d+):(\d{1,2}):(\d{1,2})(?:\.(\d+))?$");
        if (!match.Success) return false;
        var hours = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fractionMs = 0L;
        if (match.Groups[4].Success)
        {
            var fraction = (match.Groups[4].Value + "000").Substring(0, 3);
            fractionMs = long.Parse(fraction, CultureInfo.InvariantCulture);
        }
        startMs = ((hours * 60 + minutes) * 60 + seconds) * 1000 + fractionMs;
        return true;
    }

    private static Chapter? ConvertFromXchpLine(string line)
    {
        // .xchp の 1 行: 「HH:MM:SS,ddd 名前」 (名前は省略可。区切りはカンマでもドットでも受け付ける)
        var match = Regex.Match(line, @"^\s*(\d{1,3}):(\d{1,2}):(\d{1,2})[.,](\d{1,3})(?:\s+(.*?))?\s*$");
        if (!match.Success) return null;
        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[4].Value.PadRight(3, '0');
        var startMs = ((hours * 60L + minutes) * 60 + seconds) * 1000 + int.Parse(fraction, CultureInfo.InvariantCulture);
        var name = match.Groups[5].Success ? match.Groups[5].Value.Trim() : "";
        return new Chapter { StartMs = startMs, Name = name };
    }

    private static string GetChapterFilePathForVideo(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath)) return "";
        return Path.ChangeExtension(videoPath, "xchp");
    }

    private void InitializeChaptersForInput(string videoPath)
    {
        // 入力動画に合わせてチャプターを初期化する。同名の .xchp があれば自動で読み込む。
        ClearChapters();
        _chapterStartBox.Text = "00:00:00.000";
        var chapterFile = GetChapterFilePathForVideo(videoPath);
        _chapterOutputPathBox.Text = chapterFile.Length > 0 ? Path.GetDirectoryName(chapterFile) ?? "" : "";
        _chapterPathBox.Text = chapterFile.Length > 0 && File.Exists(chapterFile) ? chapterFile : "";
        if (chapterFile.Length == 0 || !File.Exists(chapterFile)) return;
        var loaded = 0;
        var skipped = 0;
        try
        {
            var chapterLines = File.ReadAllLines(chapterFile, Encoding.UTF8);
            foreach (var chapterLine in chapterLines)
            {
                if (string.IsNullOrWhiteSpace(chapterLine)) continue;
                var parsed = ConvertFromXchpLine(chapterLine);
                if (parsed == null) { skipped++; continue; }
                if (FindChapterIndexByStartMs(parsed.StartMs) >= 0) { skipped++; continue; }
                _chapters.Add(parsed);
                loaded++;
            }
            SortChapters();
            RefreshChapterList();
            WriteLog($"チャプターファイルを読み込み: {chapterFile} ({loaded} 件)");
            if (skipped > 0) WriteLog($"チャプターファイルで解釈できなかった行: {skipped} 件");
        }
        catch (Exception ex)
        {
            WriteLog($"チャプターファイルの読み込みに失敗: {ex.Message}");
        }
    }

    private string GetChapterOutputFilePath()
    {
        var folder = _chapterOutputPathBox.Text.Trim();
        if (folder.Length == 0) return "";
        string path;
        if (folder.EndsWith(".xchp", StringComparison.OrdinalIgnoreCase))
        {
            // フルパスで書かれた場合は、そのまま使う。
            path = folder;
        }
        else
        {
            // 表示はフォルダーまでで、ファイル名は入力動画の名前から自動で決める。
            var fileName = _inputPath.Length > 0
                ? Path.GetFileName(GetChapterFilePathForVideo(_inputPath))
                : "chapters.xchp";
            path = Path.Combine(folder, fileName);
        }
        if (_chapterTimestampCheckBox.Checked)
        {
            var directory = Path.GetDirectoryName(path) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            if (extension.Length == 0) extension = ".xchp";
            var stampedName = baseName + "_" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + extension;
            path = directory.Length > 0 ? Path.Combine(directory, stampedName) : stampedName;
        }
        return path;
    }

    private void ExportChapters()
    {
        try
        {
            if (_chapters.Count == 0) throw new InvalidOperationException("チャプターが登録されていません。");
            var path = GetChapterOutputFilePath();
            if (path.Length == 0) throw new InvalidOperationException("出力先を指定してください。");
            if (File.Exists(path))
            {
                var answer = MessageBox.Show($"次のファイルを上書きしますか？\r\n{path}", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }
            var outputLines = new List<string>();
            foreach (var chapter in _chapters)
            {
                var timeText = TimeText.FormatClockFromMs(chapter.StartMs).Replace('.', ',');
                if (!string.IsNullOrEmpty(chapter.Name)) timeText += " " + chapter.Name;
                outputLines.Add(timeText);
            }
            // MPC-HC が文字コードを UTF-8 と判定できるよう、BOM 付き UTF-8 で書き出す。
            File.WriteAllText(path, string.Join("\r\n", outputLines) + "\r\n", new UTF8Encoding(true));
            WriteLog($"チャプターを出力: {path} ({_chapters.Count} 件)");
            SetStatus("チャプターを出力しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>現在のチャプターを、mkvmerge が読める Matroska チャプター XML で書き出す。</summary>
    private void ExportMkvChaptersXml()
    {
        try
        {
            if (_chapters.Count == 0) throw new InvalidOperationException("チャプターが登録されていません。");
            var xchpPath = GetChapterOutputFilePath();
            if (xchpPath.Length == 0) throw new InvalidOperationException("出力先を指定してください。");
            var path = Path.ChangeExtension(xchpPath, ".xml");
            if (File.Exists(path))
            {
                var answer = MessageBox.Show($"次のファイルを上書きしますか？\r\n{path}", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = new UTF8Encoding(false),
                NewLineChars = "\r\n",
            };
            using (var writer = XmlWriter.Create(path, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("Chapters");
                writer.WriteStartElement("EditionEntry");
                foreach (var chapter in _chapters)
                {
                    writer.WriteStartElement("ChapterAtom");
                    writer.WriteElementString("ChapterTimeStart", FormatMkvChapterTime(chapter.StartMs));
                    if (!string.IsNullOrEmpty(chapter.Name))
                    {
                        writer.WriteStartElement("ChapterDisplay");
                        writer.WriteElementString("ChapterString", chapter.Name);
                        writer.WriteElementString("ChapterLanguage", "jpn");
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
            WriteLog($"MKV チャプター XML を出力: {path} ({_chapters.Count} 件)");
            WriteLog($"使うとき (例): mkvmerge -o movie_ch.mkv --chapters {Path.GetFileName(path)} movie.mkv");
            WriteLog($"既存の MKV へ後付けするなら: mkvpropedit movie.mkv --chapters {Path.GetFileName(path)}");
            SetStatus("MKV チャプター XML を出力しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string FormatMkvChapterTime(long startMs)
    {
        var hours = startMs / 3600000;
        var minutes = startMs % 3600000 / 60000;
        var seconds = startMs % 60000 / 1000;
        var ms = startMs % 1000;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{ms:000}000000";
    }

    // ------------------------------------------------------------------
    // レイアウトとプレビューの共有
    // ------------------------------------------------------------------

    private void UpdateChapterListBounds()
    {
        if (_chapterGroup == null || _chapterRowsPanel == null) return;
        var listWidth = Math.Max(100, _chapterGroup.ClientSize.Width - 24);
        var listHeight = Math.Max(60, _chapterGroup.ClientSize.Height - 28 - 12);
        _chapterRowsPanel.SetBounds(12, 28, listWidth, listHeight);
    }

    /// <summary>ポップアップやクリッピング画面から映像パネルを戻す先 (共有ブロックのあるタブ)。</summary>
    private TabPage GetPreviewReturnTab() => _seekTrackBar.Parent == _chapterTab ? _chapterTab : _cutTab;

    private void MovePreviewControls(bool toChapter)
    {
        // プレビューまわりのコントロールは動画編集タブとチャプタータブで共有し、表示中のタブへ移動する。
        var target = toChapter ? _chapterTab : _cutTab;
        var sharedControls = new Control[]
        {
            _previewPositionLabel, _seekTrackBar, _previewToggleButton,
            _seekStartButton, _frameBackButton, _frameForwardButton, _seekEndButton,
            _volumeMuteButton, _volumeTrackBar, _volumeValueLabel,
        };
        foreach (var sharedControl in sharedControls)
        {
            if (sharedControl.Parent != target) target.Controls.Add(sharedControl);
        }
        if ((_mpvHostPanel.Parent == _cutTab || _mpvHostPanel.Parent == _chapterTab) && _mpvHostPanel.Parent != target)
        {
            target.Controls.Add(_mpvHostPanel);
            RestoreMpvHostPanelHome();
            FitMpvWindow();
        }
    }
}
