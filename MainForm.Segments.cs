using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>カット区間 1 件分。クリッピング量 (上下左右 px) も持つ。</summary>
internal sealed class Segment
{
    public long StartMs;
    public long EndMs;
    public int Top;
    public int Right;
    public int Bottom;
    public int Left;
    // true なら直前の区間と同じ出力グループ (結合)。false なら新しいグループの先頭 (分割)。
    public bool JoinWithPrevious;

    public bool HasClipping => Top > 0 || Right > 0 || Bottom > 0 || Left > 0;

    public Segment Clone() => new()
    {
        StartMs = StartMs,
        EndMs = EndMs,
        Top = Top,
        Right = Right,
        Bottom = Bottom,
        Left = Left,
        JoinWithPrevious = JoinWithPrevious,
    };
}

/// <summary>
/// 区間編集 (一覧、追加・削除・並べ替え・全区間分割・上書き、元に戻す/やり直し、ファイル数表示)。
/// </summary>
internal sealed partial class MainForm
{
    private readonly List<Segment> _segments = new();
    private readonly List<SegmentUndoEntry> _segmentUndoStack = new();
    private readonly List<SegmentUndoEntry> _segmentRedoStack = new();
    private int _segmentContextIndex = -1;

    private sealed class SegmentUndoEntry
    {
        public string Description = "";
        public List<Segment> Segments = new();
    }

    // ------------------------------------------------------------------
    // 時刻テキスト
    // ------------------------------------------------------------------

    private static long ConvertTimeToMs(string text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) throw new InvalidOperationException("開始／終了時刻を入力してください。");
        if (Regex.IsMatch(value, @"^\d+(?:[\.,]\d+)?$"))
        {
            var seconds = double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (seconds < 0) throw new InvalidOperationException("時刻に負の値は指定できません。");
            return (long)Math.Round(seconds * 1000, 0, MidpointRounding.AwayFromZero);
        }
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) || span.TotalMilliseconds < 0)
        {
            throw new InvalidOperationException($"時刻 '{value}' を解釈できません。例: 00:01:23.450 または 83.45");
        }
        return (long)Math.Round(span.TotalMilliseconds, 0, MidpointRounding.AwayFromZero);
    }

    private string GetFrameInfoText(long ms)
    {
        if (!TestFrameTableReady()) return "フレーム時刻テーブル未取得: fps からの推定値";
        var frameIndex = FindFrameIndexAtOrBefore(ms * 1000L + 999L);
        if (frameIndex < 0) return "先頭フレームより前";
        return $"フレーム #{frameIndex + 1}/{_frameTimesUs!.Length}; PTS={_frameTimesUs[frameIndex] / 1000000.0:0.000000} s";
    }

    // ------------------------------------------------------------------
    // 速度と回転の選択
    // ------------------------------------------------------------------

    // 変更後の速度は、選択肢を持たない欄で指定する。ホイールでも直接入力でも、
    // 0.05 刻み (0.05 ～ 100.00) にそろえて確定する。
    private double _speedValue = 1.0;

    private double GetSelectedSpeed() => _speedValue;

    /// <summary>速度の値を 0.05 刻みと範囲内にそろえて確定し、欄の表示と出力名を更新する。</summary>
    private void SetSpeedValue(double value)
    {
        var hundredths = (int)Math.Round(value * 100.0, MidpointRounding.AwayFromZero);
        hundredths = (int)Math.Round(hundredths / 5.0, MidpointRounding.AwayFromZero) * 5;
        hundredths = Math.Clamp(hundredths, 5, 10000);
        var snapped = hundredths / 100.0;
        var text = snapped.ToString("0.00", CultureInfo.InvariantCulture);
        if (_speedBox != null && _speedBox.Text != text) _speedBox.Text = text;
        if (Math.Abs(snapped - _speedValue) < 0.000001) return;
        _speedValue = snapped;
        UpdateCutOutputName();
    }

    /// <summary>速度の欄への直接入力を確定する。解釈できないときは現在の値に戻す。</summary>
    private void CommitSpeedBoxText()
    {
        if (_speedBox == null) return;
        var text = _speedBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            SetSpeedValue(value);
        }
        else
        {
            _speedBox.Text = _speedValue.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private void SpeedBoxMouseWheel(MouseEventArgs e)
    {
        // ホイール 1 ノッチで 0.05 ずつ。上で上げ、下で下げる。
        var notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        if (notches == 0) return;
        SetSpeedValue(_speedValue + 0.05 * notches);
    }

    private void SpeedBoxKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CommitSpeedBoxText();
        }
        else if (e.KeyCode == Keys.Up)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SetSpeedValue(_speedValue + 0.05);
        }
        else if (e.KeyCode == Keys.Down)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SetSpeedValue(_speedValue - 0.05);
        }
    }

    private int GetRotationIndex()
    {
        if (_rotationCombo != null && _rotationCombo.SelectedIndex > 0) return _rotationCombo.SelectedIndex;
        return 0;
    }

    /// <summary>プレビューへ適用する回転量 (時計回りの角度)。</summary>
    private int GetPreviewRotationClockwise()
    {
        return GetRotationIndex() switch
        {
            1 => 90,
            2 => 180,
            3 => 270,
            _ => 0,
        };
    }

    private static string GetRotationDisplayText(int index)
    {
        return index switch
        {
            1 => "右90°（時計回り）",
            2 => "180°",
            3 => "左90°（反時計回り）",
            _ => "",
        };
    }

    /// <summary>入力動画の解像度を返す。取得できない場合は例外を投げる。</summary>
    private (int Width, int Height) GetVideoDimensions()
    {
        if (_videoStreams.Count == 0)
        {
            throw new InvalidOperationException("先に入力動画を読み込んでください。");
        }
        var width = _videoStreams[0].Width;
        var height = _videoStreams[0].Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("入力動画の解像度を取得できません。");
        }
        return (width, height);
    }

    // ------------------------------------------------------------------
    // 元に戻す / やり直し
    // ------------------------------------------------------------------

    private List<Segment> CopySegmentsSnapshot() => _segments.Select(s => s.Clone()).ToList();

    private void UpdateSegmentUndoRedoMenuItems()
    {
        _undoMenuItem.Enabled = _segmentUndoStack.Count > 0;
        _redoMenuItem.Enabled = _segmentRedoStack.Count > 0;
    }

    private void PushSegmentUndoSnapshot(string description)
    {
        // 区間を変更する直前に呼ぶ。変更前の状態を最大 50 件まで保持し、新しい操作をしたらやり直し履歴は消す。
        _segmentUndoStack.Add(new SegmentUndoEntry { Description = description, Segments = CopySegmentsSnapshot() });
        while (_segmentUndoStack.Count > 50) _segmentUndoStack.RemoveAt(0);
        _segmentRedoStack.Clear();
        UpdateSegmentUndoRedoMenuItems();
    }

    private void ClearSegmentUndoStack()
    {
        _segmentUndoStack.Clear();
        _segmentRedoStack.Clear();
        UpdateSegmentUndoRedoMenuItems();
    }

    private void UndoSegmentOperation()
    {
        if (_segmentUndoStack.Count == 0) return;
        var entry = _segmentUndoStack[^1];
        // 現在の状態をやり直し用に控えてから、取り消す。
        _segmentRedoStack.Add(new SegmentUndoEntry { Description = entry.Description, Segments = CopySegmentsSnapshot() });
        while (_segmentRedoStack.Count > 50) _segmentRedoStack.RemoveAt(0);
        _segmentUndoStack.RemoveAt(_segmentUndoStack.Count - 1);
        var selectedIndex = _segmentList?.SelectedIndex ?? -1;
        _segments.Clear();
        _segments.AddRange(entry.Segments);
        RefreshSegmentList();
        if (_segmentList != null && _segmentList.Items.Count > 0)
        {
            _segmentList.SelectedIndex = Math.Min(Math.Max(0, selectedIndex), _segmentList.Items.Count - 1);
        }
        WriteLog($"区間操作を元に戻しました: {entry.Description}");
        UpdateSegmentUndoRedoMenuItems();
    }

    private void RedoSegmentOperation()
    {
        if (_segmentRedoStack.Count == 0) return;
        var entry = _segmentRedoStack[^1];
        // 現在の状態を取り消し用に控えてから、やり直す。
        _segmentUndoStack.Add(new SegmentUndoEntry { Description = entry.Description, Segments = CopySegmentsSnapshot() });
        while (_segmentUndoStack.Count > 50) _segmentUndoStack.RemoveAt(0);
        _segmentRedoStack.RemoveAt(_segmentRedoStack.Count - 1);
        var selectedIndex = _segmentList?.SelectedIndex ?? -1;
        _segments.Clear();
        _segments.AddRange(entry.Segments);
        RefreshSegmentList();
        if (_segmentList != null && _segmentList.Items.Count > 0)
        {
            _segmentList.SelectedIndex = Math.Min(Math.Max(0, selectedIndex), _segmentList.Items.Count - 1);
        }
        WriteLog($"区間操作をやり直しました: {entry.Description}");
        UpdateSegmentUndoRedoMenuItems();
    }

    // ------------------------------------------------------------------
    // 一覧の表示
    // ------------------------------------------------------------------

    private string GetSegmentTotalText()
    {
        // 出力ファイルの数。1 区間だけのグループは分割ファイル、2 区間以上のグループは結合ファイルとして数える。
        var splitCount = 0;
        var joinCount = 0;
        foreach (var group in BuildSegmentGroups())
        {
            if (group.Count >= 2) joinCount++;
            else splitCount++;
        }
        return $"分割ファイル数: {splitCount}　結合ファイル数: {joinCount}";
    }

    private void UpdateSegmentTotalLabel()
    {
        if (_segmentTotalLabel != null) _segmentTotalLabel.Text = GetSegmentTotalText();
    }

    // グループの区切り (一覧でグループの先頭行の上に空ける間隔)。
    private const int SegmentGroupGap = 10;

    // 結合グループ内での段差 (グループの 2 つ目以降の行を、1 つごとに右へずらす幅)。
    private const int SegmentGroupIndent = 15;

    /// <summary>その区間がグループの中で何番目か (先頭 = 0)。</summary>
    private int GetSegmentGroupPosition(int index)
    {
        var position = 0;
        for (var i = 1; i <= index && i < _segments.Count; i++)
        {
            position = _segments[i].JoinWithPrevious ? position + 1 : 0;
        }
        return position;
    }

    /// <summary>その区間を含むグループの区間数。</summary>
    private int GetSegmentGroupSize(int index)
    {
        var start = index;
        while (start > 0 && _segments[start].JoinWithPrevious) start--;
        var size = 1;
        for (var i = start + 1; i < _segments.Count && _segments[i].JoinWithPrevious; i++) size++;
        return size;
    }

    /// <summary>その区間が新しい出力グループの先頭かどうか。</summary>
    private bool IsSegmentGroupStart(int index) => index <= 0 || !_segments[index].JoinWithPrevious;

    /// <summary>区間を出力グループごとに分ける (グループ内は結合、グループ間は分割)。</summary>
    private List<List<Segment>> BuildSegmentGroups()
    {
        var groups = new List<List<Segment>>();
        foreach (var segment in _segments)
        {
            if (groups.Count == 0 || !segment.JoinWithPrevious) groups.Add(new List<Segment>());
            groups[^1].Add(segment);
        }
        return groups;
    }

    /// <summary>
    /// 区間のフレーム数 (開始位置のフレームから、終了位置に表示されるフレームまで) を返す。
    /// フレーム表が使えない場合は -1。
    /// </summary>
    private int GetSegmentFrameCount(Segment segment)
    {
        if (!TestFrameTableReady()) return -1;
        var startIndex = FindFrameIndexAtOrAfter(segment.StartMs * 1000L);
        var endIndex = FindFrameIndexAtOrAfter(segment.EndMs * 1000L);
        if (startIndex < 0 || endIndex < startIndex) return -1;
        return endIndex - startIndex + 1;
    }

    private void RefreshSegmentList()
    {
        if (_segmentList == null) return;
        _segmentList.BeginUpdate();
        _segmentList.Items.Clear();
        for (var i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var length = segment.EndMs - segment.StartMs;
            var clipText = segment.HasClipping
                ? $"  【クリッピング 上{segment.Top} 右{segment.Right} 下{segment.Bottom} 左{segment.Left}px】"
                : "";
            // 行頭の番号は、結合グループの区間にだけ、グループごとに 01 から付ける (単独の区間 = 分割には付けない)。
            var numberText = GetSegmentGroupSize(i) >= 2 ? $"{GetSegmentGroupPosition(i) + 1:00}.  " : "";
            var frameCount = GetSegmentFrameCount(segment);
            var lengthText = frameCount >= 0 ? $"{TimeText.FormatClockFromMs(length)} / {frameCount}f" : TimeText.FormatClockFromMs(length);
            var text = $"{numberText}{TimeText.FormatClockFromMs(segment.StartMs)} - {TimeText.FormatClockFromMs(segment.EndMs)}   ({lengthText}){clipText}";
            _segmentList.Items.Add(text);
        }
        _segmentList.EndUpdate();
        _segmentStableSelection = Array.Empty<int>();
        UpdateSegmentTotalLabel();
        UpdateOutputPathMode();
        UpdateEditSummary();
    }

    private void DrawSegmentListItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _segmentList.Items.Count) return;
        var segment = _segments[e.Index];
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        var bounds = e.Bounds;
        if (e.Index > 0 && IsSegmentGroupStart(e.Index))
        {
            // グループの区切り: 上に間隔を空け、背景色で帯を描く。
            using (var gapBrush = new SolidBrush(Theme.PageBack))
            {
                e.Graphics.FillRectangle(gapBrush, bounds.X, bounds.Y, bounds.Width, SegmentGroupGap);
            }
            bounds = new Rectangle(bounds.X, bounds.Y + SegmentGroupGap, bounds.Width, bounds.Height - SegmentGroupGap);
        }
        // 行の下地は一覧の背景色で消しておく (前の描画が残らないように)。
        using (var baseBrush = new SolidBrush(_segmentList.BackColor))
        {
            e.Graphics.FillRectangle(baseBrush, bounds);
        }

        var isGroupStart = IsSegmentGroupStart(e.Index);
        var nextIsJoined = e.Index + 1 < _segments.Count && _segments[e.Index + 1].JoinWithPrevious;
        // カードの範囲: 結合でつながる側は上下の余白をなくし、グループを 1 つのブロックに見せる。
        var cardBounds = new Rectangle(
            bounds.X + 2,
            bounds.Y + (isGroupStart ? 2 : 0),
            Math.Max(10, bounds.Width - 4),
            Math.Max(10, bounds.Height - (isGroupStart ? 2 : 0) - (nextIsJoined ? 0 : 2)));
        var previousSmoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = CreateSegmentCardPath(cardBounds, roundTop: isGroupStart, roundBottom: !nextIsJoined))
        using (var fillBrush = new SolidBrush(isSelected ? Color.FromArgb(86, 74, 110) : Theme.PanelBack))
        using (var borderPen = new Pen(isSelected ? Theme.Accent : Color.FromArgb(88, 80, 110)))
        {
            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);
        }

        // テキスト: 結合グループ内では段々になるよう 1 つごとに右へずらす。
        var indent = GetSegmentGroupPosition(e.Index) * SegmentGroupIndent;
        var textX = cardBounds.X + 10 + indent;
        var drawFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var measureSize = new Size(int.MaxValue, cardBounds.Height);
        if (GetSegmentGroupSize(e.Index) >= 2)
        {
            // グループ内の番号はバッジで描く。
            var badgeBounds = new Rectangle(textX, cardBounds.Y + (cardBounds.Height - 18) / 2, 30, 18);
            using (var badgePath = CreateRoundedRectPath(badgeBounds, 5))
            using (var badgeBrush = new SolidBrush(isSelected ? Theme.Accent : Color.FromArgb(58, 54, 72)))
            {
                e.Graphics.FillPath(badgeBrush, badgePath);
            }
            TextRenderer.DrawText(e.Graphics, $"{GetSegmentGroupPosition(e.Index) + 1:00}", _segmentList.Font, badgeBounds, Color.FromArgb(246, 245, 250),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            textX += 38;
        }
        var timesText = $"{TimeText.FormatClockFromMs(segment.StartMs)} - {TimeText.FormatClockFromMs(segment.EndMs)}";
        var timesWidth = TextRenderer.MeasureText(e.Graphics, timesText, _segmentList.Font, measureSize, drawFlags).Width;
        TextRenderer.DrawText(e.Graphics, timesText, _segmentList.Font, new Rectangle(textX, cardBounds.Y, timesWidth, cardBounds.Height), Color.FromArgb(239, 238, 244), drawFlags);
        textX += timesWidth;
        var frameCount = GetSegmentFrameCount(segment);
        var lengthText = frameCount >= 0
            ? $" ({TimeText.FormatClockFromMs(segment.EndMs - segment.StartMs)} / {frameCount}f)"
            : $" ({TimeText.FormatClockFromMs(segment.EndMs - segment.StartMs)})";
        var lengthWidth = TextRenderer.MeasureText(e.Graphics, lengthText, _segmentList.Font, measureSize, drawFlags).Width;
        TextRenderer.DrawText(e.Graphics, lengthText, _segmentList.Font, new Rectangle(textX, cardBounds.Y, lengthWidth, cardBounds.Height), Theme.MutedFore, drawFlags);
        textX += lengthWidth;
        if (segment.HasClipping)
        {
            // クリッピングはピンクのタグで描く。
            var clipColor = Color.FromArgb(255, 145, 190);
            var tagText = $"clip 上{segment.Top} 右{segment.Right} 下{segment.Bottom} 左{segment.Left}px";
            var tagTextWidth = TextRenderer.MeasureText(e.Graphics, tagText, _segmentList.Font, measureSize, drawFlags).Width;
            var tagBounds = new Rectangle(textX + 8, cardBounds.Y + (cardBounds.Height - 18) / 2, tagTextWidth + 14, 18);
            using (var tagPath = CreateRoundedRectPath(tagBounds, 5))
            using (var tagPen = new Pen(clipColor))
            {
                e.Graphics.DrawPath(tagPen, tagPath);
            }
            TextRenderer.DrawText(e.Graphics, tagText, _segmentList.Font, tagBounds, clipColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        e.Graphics.SmoothingMode = previousSmoothing;
        DrawSegmentDropFeedbackInItem(e, bounds, cardBounds);
    }

    /// <summary>角丸の四角形のパスを作る。</summary>
    private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>区間カードのパスを作る。結合でつながる側の角は丸めない。</summary>
    private static GraphicsPath CreateSegmentCardPath(Rectangle rect, bool roundTop, bool roundBottom)
    {
        const int radius = 6;
        var d = radius * 2;
        var path = new GraphicsPath();
        if (roundTop)
        {
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        }
        else
        {
            path.AddLine(rect.X, rect.Y, rect.Right, rect.Y);
        }
        if (roundBottom)
        {
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        }
        else
        {
            path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>ドラッグ中のドロップ先表示を、行の描画の中で描く (直接描画だと前の枠が残るため)。</summary>
    private void DrawSegmentDropFeedbackInItem(DrawItemEventArgs e, Rectangle itemBounds, Rectangle rowBounds)
    {
        if (_segmentDropKind == 0) return;
        var feedbackColor = Color.FromArgb(170, 150, 240);
        if (_segmentDropKind == 1 && _segmentDropIndex == e.Index)
        {
            // 結合先の行は、行の内容 (段差を付けた範囲) だけを枠で囲む。
            using var pen = new Pen(feedbackColor, 2);
            e.Graphics.DrawRectangle(pen, rowBounds.X + 1, rowBounds.Y + 1, Math.Max(1, rowBounds.Width - 3), Math.Max(1, rowBounds.Height - 3));
            return;
        }
        // 境目と末尾は横線で示す。線の位置は、この行の上端 (挿入位置がこの行の前) か下端 (末尾)。
        var lineY = -1;
        if ((_segmentDropKind == 2 || _segmentDropKind == 3) && _segmentDropIndex == e.Index) lineY = itemBounds.Top;
        else if ((_segmentDropKind == 2 || _segmentDropKind == 3) && _segmentDropIndex >= _segments.Count && e.Index == _segments.Count - 1) lineY = itemBounds.Bottom - 3;
        if (lineY < 0) return;
        using var brush = new SolidBrush(feedbackColor);
        e.Graphics.FillRectangle(brush, itemBounds.X, lineY, itemBounds.Width, 3);
    }

    // ------------------------------------------------------------------
    // 区間の操作
    // ------------------------------------------------------------------

    private void AddCutSegment(bool useClipping = false)
    {
        try
        {
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            var start = ConvertTimeToMs(_cutStartBox.Text);
            var end = ConvertTimeToMs(_cutEndBox.Text);
            if (end <= start) throw new InvalidOperationException("終了時刻は開始時刻より後にしてください。");
            if (_durationMs > 0 && end > _durationMs) throw new InvalidOperationException("終了時刻が動画の長さを超えています。");
            var crop = new Segment();
            if (useClipping)
            {
                crop = GetCurrentClipValues();
                if (!crop.HasClipping)
                {
                    throw new InvalidOperationException("クリッピング範囲が設定されていません。先に「クリッピング」画面で範囲を指定してください。");
                }
                _ = GetSegmentCropFilter(crop);
            }
            PushSegmentUndoSnapshot("区間の追加");
            _segments.Add(new Segment
            {
                StartMs = start,
                EndMs = end,
                Top = crop.Top,
                Right = crop.Right,
                Bottom = crop.Bottom,
                Left = crop.Left,
            });
            RefreshSegmentList();
            _segmentList.SelectedIndex = _segmentList.Items.Count - 1;
            if (useClipping)
            {
                WriteLog($"クリッピング付き区間を追加: index={_segments.Count}; start={TimeText.FormatClockFromMs(start)}; end={TimeText.FormatClockFromMs(end)}; top={crop.Top}; right={crop.Right}; bottom={crop.Bottom}; left={crop.Left}");
            }
            else
            {
                WriteLog($"カット区間を追加: index={_segments.Count}; start={TimeText.FormatClockFromMs(start)} ({start} ms); end={TimeText.FormatClockFromMs(end)} ({end} ms); length={TimeText.FormatClockFromMs(end - start)} ({end - start} ms)");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MoveCutSegment(int direction)
    {
        var index = _segmentList.SelectedIndex;
        if (index < 0) return;
        var next = index + direction;
        if (next < 0 || next >= _segments.Count) return;
        var item = _segments[index];
        PushSegmentUndoSnapshot("区間の並べ替え");
        _segments.RemoveAt(index);
        _segments.Insert(next, item);
        RefreshSegmentList();
        _segmentList.SelectedIndex = next;
    }

    private void SplitAllCutSegments()
    {
        if (_segments.Count < 2) return;
        Segment? selectedSegment = null;
        var selectedIndex = _segmentList.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < _segments.Count)
        {
            selectedSegment = _segments[selectedIndex];
        }
        // 結合・分割を問わず、すべての区間を切り離して開始時刻順に並べ替える。
        var ordered = _segments
            .Select((segment, i) => (Segment: segment, segment.StartMs, OriginalIndex: i))
            .OrderBy(x => x.StartMs)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Segment)
            .ToList();
        PushSegmentUndoSnapshot("全区間分割");
        _segments.Clear();
        _segments.AddRange(ordered);
        foreach (var segment in _segments) segment.JoinWithPrevious = false;
        RefreshSegmentList();
        if (selectedSegment != null)
        {
            for (var i = 0; i < _segments.Count; i++)
            {
                if (ReferenceEquals(_segments[i], selectedSegment))
                {
                    _segmentList.SelectedIndex = i;
                    break;
                }
            }
        }
        WriteLog("全区間分割: すべての区間を分割し、開始時刻順に並べ替えました。");
    }

    private void ApplySegmentToCutBoxes(int index)
    {
        if (index < 0 || index >= _segments.Count) return;
        var segment = _segments[index];
        _cutStartBox.Text = TimeText.FormatClockFromMs(segment.StartMs);
        _cutEndBox.Text = TimeText.FormatClockFromMs(segment.EndMs);
        // クリッピング付き区間なら、保存済みの値をクリッピング画面へ読み込む。
        if (segment.HasClipping) LoadSegmentClippingToEditor(segment);
        WriteLog($"区間 {index + 1} を開始/終了時間に反映: start={TimeText.FormatClockFromMs(segment.StartMs)}; end={TimeText.FormatClockFromMs(segment.EndMs)}");
    }

    private void OverwriteCutSegment(int index)
    {
        try
        {
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            if (index < 0 || index >= _segments.Count) return;
            var start = ConvertTimeToMs(_cutStartBox.Text);
            var end = ConvertTimeToMs(_cutEndBox.Text);
            if (end <= start) throw new InvalidOperationException("終了時刻は開始時刻より後にしてください。");
            if (_durationMs > 0 && end > _durationMs) throw new InvalidOperationException("終了時刻が動画の長さを超えています。");
            var old = _segments[index];
            PushSegmentUndoSnapshot("区間の上書き");
            _segments[index] = new Segment
            {
                StartMs = start,
                EndMs = end,
                Top = old.Top,
                Right = old.Right,
                Bottom = old.Bottom,
                Left = old.Left,
                JoinWithPrevious = old.JoinWithPrevious,
            };
            RefreshSegmentList();
            _segmentList.SelectedIndex = index;
            WriteLog($"カット区間を上書き: index={index + 1}; 旧 start={TimeText.FormatClockFromMs(old.StartMs)} end={TimeText.FormatClockFromMs(old.EndMs)} -> 新 start={TimeText.FormatClockFromMs(start)} end={TimeText.FormatClockFromMs(end)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveSelectedSegment()
    {
        // 複数選択されているときは、選択中の区間をすべて削除する。
        var indices = _segmentList.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (indices.Count == 0) return;
        PushSegmentUndoSnapshot("区間の削除");
        var details = new List<string>();
        foreach (var idx in indices)
        {
            var segment = _segments[idx];
            details.Add($"index={idx + 1}; start={TimeText.FormatClockFromMs(segment.StartMs)}; end={TimeText.FormatClockFromMs(segment.EndMs)}");
        }
        for (var i = indices.Count - 1; i >= 0; i--) _segments.RemoveAt(indices[i]);
        RefreshSegmentList();
        WriteLog($"カット区間を削除 ({indices.Count} 件): " + string.Join(" / ", details));
    }

    /// <summary>指定した行だけを選択状態にする (複数選択の一覧で、操作後の選択を戻すために使う)。</summary>
    private void SelectSegmentIndices(params int[] indices)
    {
        if (_segmentList == null) return;
        _segmentList.BeginUpdate();
        _segmentList.ClearSelected();
        var applied = new List<int>();
        foreach (var index in indices)
        {
            if (index >= 0 && index < _segmentList.Items.Count)
            {
                _segmentList.SetSelected(index, true);
                applied.Add(index);
            }
        }
        _segmentList.EndUpdate();
        applied.Sort();
        _segmentStableSelection = applied.ToArray();
    }

    /// <summary>クリックが確定した時点の選択を覚える (ドラッグ開始時に選択が絞られても、まとめて運べるようにするため)。</summary>
    private void UpdateSegmentStableSelection()
    {
        if (_segmentList == null) return;
        var indices = new List<int>();
        foreach (int selected in _segmentList.SelectedIndices) indices.Add(selected);
        indices.Sort();
        _segmentStableSelection = indices.ToArray();
    }

    // ------------------------------------------------------------------
    // ドラッグ & ドロップ (重ねて落とす = 結合、境目 = 並べ替え、空白 = 独立)。
    // 複数選択しているときは、選択中の区間をまとめて運ぶ。
    // ------------------------------------------------------------------

    private int _segmentDragIndex = -1;
    private int[] _segmentDragIndices = Array.Empty<int>();
    private int[] _segmentStableSelection = Array.Empty<int>();
    private bool _segmentDragMoved;
    private Point _segmentDragStartPoint;
    private int _segmentDropKind;   // 0 = 表示なし、1 = 重ねて落として結合、2 = 境目へ挿入、3 = 末尾に独立
    private int _segmentDropIndex;  // kind 1 = 対象の区間、kind 2 = 挿入位置、kind 3 = 末尾
    private int _segmentDragScrollTick;

    private void SegmentListMeasureItem(object? sender, MeasureItemEventArgs e)
    {
        var height = _segmentList.ItemHeight;
        if (e.Index > 0 && e.Index < _segments.Count && IsSegmentGroupStart(e.Index)) height += SegmentGroupGap;
        e.ItemHeight = height;
    }

    private void SegmentListMouseDownForDrag(MouseEventArgs e)
    {
        _segmentDragIndex = -1;
        _segmentDragIndices = Array.Empty<int>();
        if (e.Button != MouseButtons.Left) return;
        var index = _segmentList.IndexFromPoint(e.Location);
        if (index < 0) return;
        _segmentDragIndex = index;
        _segmentDragStartPoint = e.Location;
        // 運ぶ区間はマウスを押した時点で決める。一覧はマウスを押した時点で選択が押した行だけに
        // 絞られてしまうため、現在の選択だけでは複数選択を運べない。クリックが確定した時点の
        // 選択 (_segmentStableSelection) を使い、複数選択中の行を押したときはそれをまとめて運ぶ。
        var current = new List<int>();
        foreach (int selected in _segmentList.SelectedIndices) current.Add(selected);
        current.Sort();
        var ctrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
        var shift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        List<int> indices;
        if (!ctrl && !shift && _segmentStableSelection.Length > 1 && Array.IndexOf(_segmentStableSelection, index) >= 0)
        {
            indices = new List<int>(_segmentStableSelection);
        }
        else if (ctrl)
        {
            // Ctrl を押しながらのドラッグは、確定済みの選択へ押した行を足し引きした分を運ぶ。
            var set = new HashSet<int>(_segmentStableSelection.Length > 0 ? _segmentStableSelection : current.ToArray());
            if (!set.Add(index)) set.Remove(index);
            if (set.Count == 0) set.Add(index);
            indices = set.OrderBy(i => i).ToList();
        }
        else if (current.Contains(index))
        {
            indices = current;
        }
        else
        {
            indices = new List<int> { index };
        }
        _segmentDragIndices = indices.ToArray();
    }

    private void SegmentListMouseMoveForDrag(MouseEventArgs e)
    {
        if (_segmentDragIndex < 0 || (e.Button & MouseButtons.Left) == 0) return;
        var dragSize = SystemInformation.DragSize;
        if (Math.Abs(e.X - _segmentDragStartPoint.X) <= dragSize.Width / 2
            && Math.Abs(e.Y - _segmentDragStartPoint.Y) <= dragSize.Height / 2) return;
        _segmentDragIndex = -1;
        var indices = _segmentDragIndices;
        _segmentDragIndices = Array.Empty<int>();
        if (indices.Length == 0) return;
        // ドラッグ中も複数選択の見た目を保つ。一覧はマウスを押した時点で選択が 1 行に
        // 絞られてしまうため、ドラッグを始めるこの時点で選択し直す。
        if (indices.Length > 1) SelectSegmentIndices(indices);
        _segmentDragMoved = false;
        _segmentList.DoDragDrop(indices, DragDropEffects.Move);
        ClearSegmentDropFeedback();
        // 移動しなかったドラッグ (キャンセルやその場へのドロップ) では、複数選択の見た目を戻す。
        if (indices.Length > 1 && !_segmentDragMoved) SelectSegmentIndices(indices);
    }

    /// <summary>ドロップ位置の分類。kind: 1 = 重ねて落として結合、2 = 境目 (index = 挿入位置)、3 = 末尾に独立。</summary>
    private void ClassifySegmentDrop(Point point, out int kind, out int index)
    {
        kind = 3;
        index = _segments.Count;
        var hit = _segmentList.IndexFromPoint(point);
        if (hit < 0) return;
        var rect = _segmentList.GetItemRectangle(hit);
        var contentTop = rect.Top;
        if (hit > 0 && IsSegmentGroupStart(hit))
        {
            // グループの境目の帯は「その区間の前」の境目として扱う。
            if (point.Y < rect.Top + SegmentGroupGap) { kind = 2; index = hit; return; }
            contentTop += SegmentGroupGap;
        }
        var contentHeight = Math.Max(1, rect.Bottom - contentTop);
        var rel = (double)(point.Y - contentTop) / contentHeight;
        if (rel < 0.25)
        {
            // 上端の帯。グループの途中なら、上の区間へのドロップとして扱う。
            if (hit > 0 && _segments[hit].JoinWithPrevious) { kind = 1; index = hit - 1; }
            else { kind = 2; index = hit; }
        }
        else if (rel > 0.75)
        {
            // 下端の帯。グループの途中なら、その区間へのドロップとして扱う。
            if (hit + 1 < _segments.Count && _segments[hit + 1].JoinWithPrevious) { kind = 1; index = hit; }
            else { kind = 2; index = hit + 1; }
        }
        else { kind = 1; index = hit; }
    }

    private void SegmentListDragOver(DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        if (e.Data == null || !e.Data.GetDataPresent(typeof(int[]))) return;
        var point = _segmentList.PointToClient(new Point(e.X, e.Y));
        // 上下端に近づいたら少しずつスクロールする。
        var now = Environment.TickCount;
        if (now - _segmentDragScrollTick >= 120)
        {
            if (point.Y < 16 && _segmentList.TopIndex > 0) { _segmentList.TopIndex--; _segmentDragScrollTick = now; }
            else if (point.Y > _segmentList.ClientSize.Height - 16 && _segmentList.TopIndex < _segments.Count - 1) { _segmentList.TopIndex++; _segmentDragScrollTick = now; }
        }
        ClassifySegmentDrop(point, out var kind, out var index);
        e.Effect = DragDropEffects.Move;
        if (kind != _segmentDropKind || index != _segmentDropIndex)
        {
            _segmentDropKind = kind;
            _segmentDropIndex = index;
            _segmentList.Invalidate();
            _segmentList.Update();
        }
    }

    private void ClearSegmentDropFeedback()
    {
        if (_segmentDropKind == 0) return;
        _segmentDropKind = 0;
        _segmentList.Invalidate();
        _segmentList.Update();
    }

    private void SegmentListDragDrop(DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(typeof(int[]))) return;
        var fromIndices = (int[])(e.Data.GetData(typeof(int[])) ?? Array.Empty<int>());
        var point = _segmentList.PointToClient(new Point(e.X, e.Y));
        ClassifySegmentDrop(point, out var kind, out var index);
        ClearSegmentDropFeedback();
        MoveSegmentsByDrag(fromIndices, kind, index);
    }

    private void MoveSegmentsByDrag(int[] fromIndices, int kind, int targetIndex)
    {
        var movingIndices = fromIndices
            .Where(i => i >= 0 && i < _segments.Count)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
        if (movingIndices.Count == 0) return;
        var movingSet = new HashSet<int>(movingIndices);
        var original = _segments.ToList();
        var originalFlags = original.Select(segment => segment.JoinWithPrevious).ToList();
        // 運ぶ区間 (元の並び順のまま)。落とし先に結合するときはブロック全体が結合し、
        // そうでないときはブロックの中のつながりを元のまま保つ。
        var block = movingIndices.Select(i => original[i]).ToList();
        // 残る区間。残る区間のつながりも元のまま保つ (従来の 1 件移動と同じ扱い)。
        var rest = new List<Segment>();
        var restFlags = new List<bool>();
        for (var i = 0; i < original.Count; i++)
        {
            if (movingSet.Contains(i)) continue;
            rest.Add(original[i]);
            restFlags.Add(originalFlags[i]);
        }

        int insertAt;
        bool joinHead;
        var joinTarget = false;
        if (kind == 1)
        {
            // 重ねて落とすと、対象のグループに結合する。運ぶ区間自身の上へのドロップは何もしない。
            // 同じグループの中では対象の位置に入り (対象は前後へずれる)、
            // グループの外から落とすと、対象の区間のすぐ下に追加する。
            if (targetIndex < 0 || targetIndex >= original.Count || movingSet.Contains(targetIndex)) return;
            var groupStart = targetIndex;
            while (groupStart > 0 && originalFlags[groupStart]) groupStart--;
            var groupEnd = targetIndex;
            while (groupEnd + 1 < original.Count && originalFlags[groupEnd + 1]) groupEnd++;
            var sameGroup = false;
            foreach (var movingIndex in movingIndices)
            {
                if (movingIndex >= groupStart && movingIndex <= groupEnd) { sameGroup = true; break; }
            }
            if (sameGroup)
            {
                insertAt = rest.IndexOf(original[targetIndex]);
                if (targetIndex > movingIndices[movingIndices.Count - 1])
                {
                    // 後ろの区間へ落とすときは、その区間の位置 (直後) に入る。
                    insertAt += 1;
                    joinHead = true;
                }
                else
                {
                    joinHead = insertAt > 0 && originalFlags[targetIndex];
                    joinTarget = true;
                }
            }
            else
            {
                insertAt = rest.IndexOf(original[targetIndex]) + 1;
                joinHead = true;
            }
        }
        else if (kind == 2 && targetIndex < original.Count)
        {
            // 境目: その位置へ挿入する。下の区間が直前とつながっている (グループの途中) なら同じグループに入る。
            // 運ぶ区間自身の境目へのドロップは何もしない。
            if (movingSet.Contains(targetIndex)) return;
            insertAt = rest.IndexOf(original[targetIndex]);
            joinHead = insertAt > 0 && originalFlags[targetIndex];
        }
        else
        {
            // 末尾に独立したグループとして置く。
            insertAt = rest.Count;
            joinHead = false;
        }

        var newList = new List<Segment>();
        var newFlags = new List<bool>();
        for (var i = 0; i < insertAt; i++)
        {
            newList.Add(rest[i]);
            newFlags.Add(restFlags[i]);
        }
        for (var i = 0; i < block.Count; i++)
        {
            newList.Add(block[i]);
            newFlags.Add(i == 0 ? joinHead : kind == 1 || joinHead || originalFlags[movingIndices[i]]);
        }
        for (var i = insertAt; i < rest.Count; i++)
        {
            newList.Add(rest[i]);
            newFlags.Add(restFlags[i]);
        }
        // 同じグループの中で前へ落としたときは、対象の区間がブロックの後ろにつながる。
        if (joinTarget && insertAt + block.Count < newFlags.Count) newFlags[insertAt + block.Count] = true;
        if (newFlags.Count > 0) newFlags[0] = false;

        // 並びもグループ分けも変わらないドロップは何もしない。
        var unchanged = newList.Count == original.Count;
        if (unchanged)
        {
            for (var i = 0; i < newList.Count; i++)
            {
                if (!ReferenceEquals(newList[i], original[i]) || newFlags[i] != originalFlags[i])
                {
                    unchanged = false;
                    break;
                }
            }
        }
        if (unchanged) return;

        PushSegmentUndoSnapshot("区間の移動");
        _segmentDragMoved = true;
        for (var i = 0; i < newList.Count; i++) newList[i].JoinWithPrevious = newFlags[i];
        _segments.Clear();
        _segments.AddRange(newList);
        RefreshSegmentList();
        var selection = new int[block.Count];
        for (var i = 0; i < block.Count; i++) selection[i] = insertAt + i;
        SelectSegmentIndices(selection);
        var fromText = string.Join(", ", movingIndices.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
        var joinNote = kind == 1 ? " (結合)" : joinHead ? " (前の区間と結合)" : "";
        WriteLog($"区間をドラッグで移動: {fromText} 番 -> {insertAt + 1} 番{joinNote}");
    }

    /// <summary>入力の読み込み・「新規」で区間まわりを初期状態へ戻す。</summary>
    private void ClearSegmentsForNewInput()
    {
        _segments.Clear();
        ClearSegmentUndoStack();
        RefreshSegmentList();
    }

    // クリッピングの値と画面は MainForm.Clipping.cs にある。
}
