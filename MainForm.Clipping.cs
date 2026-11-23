using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// クリッピング画面 (プレビュー上での範囲指定と数値入力)。
/// 本体のプレビュー (mpv) を画面へ移し、色透過オーバーレイで黒帯と枠・ハンドルを描く。
/// </summary>
internal sealed partial class MainForm
{
    private Segment _clipCurrent = new();
    private Form? _clipForm;
    private ClipBoxKeyFilter? _clipBoxKeyFilter;
    private Panel? _clipVideoPanel;
    private TransparentOverlayForm? _clipOverlay;
    private Panel? _clipPreviewPanel;
    private SeekTrackBar? _clipSeekTrackBar;
    private Label? _clipPositionLabel;
    private Button? _clipPlayButton;
    private NumericUpDown? _clipTopBox;
    private NumericUpDown? _clipRightBox;
    private NumericUpDown? _clipBottomBox;
    private NumericUpDown? _clipLeftBox;
    private Label? _clipResolutionLabel;
    private bool _clipPreviewSeeking;
    private bool _clipUpdatingControls;
    private bool _clipMouseDragging;
    private string _clipDragEdge = "";
    private Segment? _clipDragStart;
    private readonly WindowDragTracker _clipFormDrag = new();
    private double _clipSavedVolume;
    private Rectangle? _clipMpvRect;
    private Size _clipMpvRectPanelSize = Size.Empty;

    private void CloseClippingForm()
    {
        if (_clipForm is { IsDisposed: false })
        {
            try { _clipForm.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // クリッピングの値
    // ------------------------------------------------------------------

    private Segment GetCurrentClipValues()
    {
        if (_clipTopBox != null && _clipRightBox != null && _clipBottomBox != null && _clipLeftBox != null)
        {
            return new Segment
            {
                Top = (int)_clipTopBox.Value,
                Right = (int)_clipRightBox.Value,
                Bottom = (int)_clipBottomBox.Value,
                Left = (int)_clipLeftBox.Value,
            };
        }
        return _clipCurrent.Clone();
    }

    private string GetSegmentCropFilter(Segment segment)
    {
        if (!segment.HasClipping) return "";
        var dimensions = GetVideoDimensions();
        var top = segment.Top;
        var right = segment.Right;
        var bottom = segment.Bottom;
        var left = segment.Left;
        if (top < 0 || right < 0 || bottom < 0 || left < 0)
        {
            throw new InvalidOperationException("クリッピング値に負の数は指定できません。");
        }
        var cropWidth = dimensions.Width - left - right;
        var cropHeight = dimensions.Height - top - bottom;
        if (cropWidth < 2 || cropHeight < 2)
        {
            throw new InvalidOperationException("クリッピング後の幅・高さは 2 ピクセル以上必要です。");
        }
        // HEVC / VP9 の一般的な 4:2:0 出力で扱えるよう、出力サイズを偶数に揃える。
        if (cropWidth % 2 != 0) cropWidth--;
        if (cropHeight % 2 != 0) cropHeight--;
        if (cropWidth < 2 || cropHeight < 2)
        {
            throw new InvalidOperationException("クリッピング後の幅・高さが小さすぎます。");
        }
        return $"crop={cropWidth}:{cropHeight}:{left}:{top}:exact=1";
    }

    /// <summary>クリッピング付き区間をダブルクリックしたときに、その値をクリッピング画面へ読み込む。</summary>
    private void LoadSegmentClippingToEditor(Segment segment)
    {
        var values = new Segment
        {
            Top = segment.Top,
            Right = segment.Right,
            Bottom = segment.Bottom,
            Left = segment.Left,
        };
        if (_clipForm is { IsDisposed: false })
        {
            SetClipBoxValues(values.Top, values.Right, values.Bottom, values.Left);
            _clipForm.Activate();
        }
        else
        {
            _clipCurrent = values;
            ShowClippingForm(values);
        }
    }

    private void UpdateClipResolutionLabel()
    {
        if (_clipResolutionLabel == null || _clipResolutionLabel.IsDisposed) return;
        try
        {
            var dimensions = GetVideoDimensions();
            var values = GetCurrentClipValues();
            var cropWidth = dimensions.Width - values.Left - values.Right;
            var cropHeight = dimensions.Height - values.Top - values.Bottom;
            // 実際に crop フィルターを適用する場合と同じく、出力寸法を偶数に揃える。
            if (values.HasClipping)
            {
                if (cropWidth % 2 != 0) cropWidth--;
                if (cropHeight % 2 != 0) cropHeight--;
            }
            _clipResolutionLabel.Text = cropWidth < 2 || cropHeight < 2
                ? "クリップ後: -- × -- px"
                : $"クリップ後: {cropWidth} × {cropHeight} px";
        }
        catch
        {
            _clipResolutionLabel.Text = "クリップ後: -- × -- px";
        }
    }

    private void SetClipBoxValues(int top, int right, int bottom, int left)
    {
        var dimensions = GetVideoDimensions();
        var previousValues = _clipCurrent;
        top = Math.Max(0, Math.Min(dimensions.Height - 2, top));
        bottom = Math.Max(0, Math.Min(dimensions.Height - 2, bottom));
        left = Math.Max(0, Math.Min(dimensions.Width - 2, left));
        right = Math.Max(0, Math.Min(dimensions.Width - 2, right));
        if (left + right > dimensions.Width - 2) right = Math.Max(0, dimensions.Width - 2 - left);
        if (top + bottom > dimensions.Height - 2) bottom = Math.Max(0, dimensions.Height - 2 - top);

        _clipCurrent = new Segment { Top = top, Right = right, Bottom = bottom, Left = left };
        _clipUpdatingControls = true;
        try
        {
            if (_clipTopBox != null) { _clipTopBox.Maximum = dimensions.Height - 2; _clipTopBox.Value = top; }
            if (_clipBottomBox != null) { _clipBottomBox.Maximum = dimensions.Height - 2; _clipBottomBox.Value = bottom; }
            if (_clipLeftBox != null) { _clipLeftBox.Maximum = dimensions.Width - 2; _clipLeftBox.Value = left; }
            if (_clipRightBox != null) { _clipRightBox.Maximum = dimensions.Width - 2; _clipRightBox.Value = right; }
        }
        finally { _clipUpdatingControls = false; }
        UpdateClipResolutionLabel();
        var valuesChanged = previousValues.Top != top || previousValues.Right != right
            || previousValues.Bottom != bottom || previousValues.Left != left;
        if (valuesChanged && _clipOverlay is { IsDisposed: false })
        {
            _clipOverlay.Invalidate();
        }
    }

    private void UpdateClipValuesFromInputs()
    {
        if (_clipUpdatingControls) return;
        if (_clipTopBox == null || _clipRightBox == null || _clipBottomBox == null || _clipLeftBox == null) return;
        SetClipBoxValues((int)_clipTopBox.Value, (int)_clipRightBox.Value, (int)_clipBottomBox.Value, (int)_clipLeftBox.Value);
    }

    // ------------------------------------------------------------------
    // オーバーレイの座標変換とドラッグ
    // ------------------------------------------------------------------

    /// <summary>
    /// mpv が実際に映像を描いている範囲 (プレビューパネル左上基準) を問い合わせる。
    /// 帯の位置を計算で推測すると mpv の整数ピクセルへの丸めと数 px ずれることがあるため、
    /// mpv 自身が報告する余白 (osd-dimensions) から範囲を求める。パネルの大きさが変わったときだけ問い合わせる。
    /// </summary>
    private void RefreshClipMpvRect()
    {
        if (_clipVideoPanel == null || _clipVideoPanel.IsDisposed) return;
        var panelSize = _clipVideoPanel.ClientSize;
        if (panelSize.Width <= 1 || panelSize.Height <= 1) return;
        if (_clipMpvRect != null && _clipMpvRectPanelSize == panelSize) return;
        _clipMpvRectPanelSize = panelSize;
        _clipMpvRect = null;
        try
        {
            var reply = SendMpvCommand(new object[] { "get_property", "osd-dimensions" }, true, 300);
            if (reply is not { } r) return;
            if (!r.TryGetProperty("error", out var error) || error.GetString() != "success") return;
            if (!r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
            double Get(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var ml = Get("ml");
            var mr = Get("mr");
            var mt = Get("mt");
            var mb = Get("mb");
            var rect = new Rectangle(
                (int)Math.Round(ml), (int)Math.Round(mt),
                (int)Math.Round(panelSize.Width - ml - mr), (int)Math.Round(panelSize.Height - mt - mb));
            if (rect.Width <= 0 || rect.Height <= 0) return;
            if (rect.Left < -2 || rect.Top < -2 || rect.Right > panelSize.Width + 2 || rect.Bottom > panelSize.Height + 2) return;
            _clipMpvRect = rect;
        }
        catch { }
    }

    private RectangleF GetClipDisplayRect(Control control)
    {
        // mpv の実描画範囲が取れている場合は、計算値よりそちらを優先する。
        if (_clipMpvRect is { } mpvRect && control.ClientSize == _clipMpvRectPanelSize)
        {
            return new RectangleF(mpvRect.X, mpvRect.Y, mpvRect.Width, mpvRect.Height);
        }
        var dimensions = GetVideoDimensions();
        var areaWidth = (double)control.ClientSize.Width;
        var areaHeight = (double)control.ClientSize.Height;
        if (areaWidth <= 1 || areaHeight <= 1) return RectangleF.Empty;
        var scale = Math.Min(areaWidth / dimensions.Width, areaHeight / dimensions.Height);
        var displayWidth = dimensions.Width * scale;
        var displayHeight = dimensions.Height * scale;
        var x = (areaWidth - displayWidth) / 2.0;
        var y = (areaHeight - displayHeight) / 2.0;
        return new RectangleF((float)x, (float)y, (float)displayWidth, (float)displayHeight);
    }

    private (int X, int Y)? ConvertClipPointToVideoPixel(Point point)
    {
        if (_clipOverlay == null) return null;
        var dimensions = GetVideoDimensions();
        var imageRect = GetClipDisplayRect(_clipOverlay);
        if (imageRect.Width <= 0 || imageRect.Height <= 0) return null;
        // ドラッグ中は映像外へ出ても端で止まるよう、座標を映像範囲内へクランプする。
        var clampedX = Math.Max((double)imageRect.Left, Math.Min((double)imageRect.Right, point.X));
        var clampedY = Math.Max((double)imageRect.Top, Math.Min((double)imageRect.Bottom, point.Y));
        var xFraction = Math.Max(0.0, Math.Min(1.0, (clampedX - imageRect.X) / imageRect.Width));
        var yFraction = Math.Max(0.0, Math.Min(1.0, (clampedY - imageRect.Y) / imageRect.Height));
        return ((int)Math.Round(xFraction * dimensions.Width), (int)Math.Round(yFraction * dimensions.Height));
    }

    private string GetClipDragEdge(Point point)
    {
        if (_clipOverlay == null) return "";
        var rect = GetClipDisplayRect(_clipOverlay);
        if (rect.Width <= 0 || rect.Height <= 0) return "";
        var values = GetCurrentClipValues();
        var dimensions = GetVideoDimensions();
        var left = rect.Left + values.Left / (double)dimensions.Width * rect.Width;
        var right = rect.Right - values.Right / (double)dimensions.Width * rect.Width;
        var top = rect.Top + values.Top / (double)dimensions.Height * rect.Height;
        var bottom = rect.Bottom - values.Bottom / (double)dimensions.Height * rect.Height;

        // 現在の切り抜き枠の四辺から最も近い辺を選び、選択した一辺だけを操作する。
        var nearestX = Math.Max(left, Math.Min(right, (double)point.X));
        var nearestY = Math.Max(top, Math.Min(bottom, (double)point.Y));
        var distances = new Dictionary<string, double>
        {
            ["Top"] = Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - top, 2)),
            ["Bottom"] = Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - bottom, 2)),
            ["Left"] = Math.Sqrt(Math.Pow(point.X - left, 2) + Math.Pow(point.Y - nearestY, 2)),
            ["Right"] = Math.Sqrt(Math.Pow(point.X - right, 2) + Math.Pow(point.Y - nearestY, 2)),
        };
        var edge = "";
        var bestDistance = double.PositiveInfinity;
        foreach (var name in new[] { "Top", "Right", "Bottom", "Left" })
        {
            if (distances[name] < bestDistance) { bestDistance = distances[name]; edge = name; }
        }
        // 判定は白いハンドルの半径 (9px) まで。黒帯はオーバーレイに描かれていて判定の幅がそのまま効くが、
        // 映像側は透明で入力が下へ抜けるため、実際に摘めるのは枠線とハンドルの上だけになる。幅をそろえて、黒帯側だけ広くならないようにする。
        if (bestDistance > 9.0) return "";
        return edge;
    }

    private void UpdateClipCropFromDrag(Point currentPoint)
    {
        if (string.IsNullOrEmpty(_clipDragEdge)) return;
        var current = ConvertClipPointToVideoPixel(currentPoint);
        if (current == null) return;
        var dimensions = GetVideoDimensions();
        var values = GetCurrentClipValues();
        switch (_clipDragEdge)
        {
            case "Top":
                SetClipBoxValues(Math.Max(0, Math.Min(dimensions.Height - values.Bottom - 2, current.Value.Y)), values.Right, values.Bottom, values.Left);
                break;
            case "Right":
                SetClipBoxValues(values.Top, Math.Max(0, Math.Min(dimensions.Width - values.Left - 2, dimensions.Width - current.Value.X)), values.Bottom, values.Left);
                break;
            case "Bottom":
                SetClipBoxValues(values.Top, values.Right, Math.Max(0, Math.Min(dimensions.Height - values.Top - 2, dimensions.Height - current.Value.Y)), values.Left);
                break;
            case "Left":
                SetClipBoxValues(values.Top, values.Right, values.Bottom, Math.Max(0, Math.Min(dimensions.Width - values.Right - 2, current.Value.X)));
                break;
        }
    }

    /// <summary>
    /// クリッピング画面の映像部分のドラッグで、ウィンドウを移動する。
    /// オーバーレイの透明な部分はマウス入力が下の mpv へ抜けるため、他のポップアップと同じポーリング方式にする。
    /// 枠とハンドルの近くは、今まで通りオーバーレイ側のドラッグ操作に譲る。
    /// </summary>
    private void UpdateClipPointerDrag()
    {
        if (_clipForm is not { IsDisposed: false } form) return;
        if (_clipVideoPanel is not { IsDisposed: false } panel) return;
        if (_clipOverlay is not { IsDisposed: false } overlay) return;
        _clipFormDrag.Update(form, () =>
            panel.Visible
            && panel.RectangleToScreen(panel.ClientRectangle).Contains(Control.MousePosition)
            && string.IsNullOrEmpty(GetClipDragEdge(overlay.PointToClient(Control.MousePosition))));
    }

    private void SyncClippingOverlay()
    {
        if (_clipOverlay == null || _clipOverlay.IsDisposed) return;
        if (_clipVideoPanel == null || _clipVideoPanel.IsDisposed) return;
        if (_clipForm == null || _clipForm.IsDisposed || !_clipForm.Visible) return;
        try
        {
            RefreshClipMpvRect();
            var screenLocation = _clipVideoPanel.PointToScreen(Point.Empty);
            var width = Math.Max(1, _clipVideoPanel.ClientSize.Width);
            var height = Math.Max(1, _clipVideoPanel.ClientSize.Height);
            _clipOverlay.SetBounds(screenLocation.X, screenLocation.Y, width, height);
            if (!_clipOverlay.Visible) _clipOverlay.Show(_clipForm);
            _clipOverlay.BringToFront();
            _clipOverlay.Invalidate();
        }
        catch { }
    }

    private void LayoutClippingControls()
    {
        if (_clipPreviewPanel == null || _clipPreviewPanel.IsDisposed) return;
        var width = _clipPreviewPanel.ClientSize.Width;
        if (_clipPositionLabel != null)
        {
            // 再生時間は、設定パネルを除いたプレビュー画面 (映像) の幅に合わせて中央揃えにする。
            var videoWidth = _clipVideoPanel?.ClientSize.Width ?? width;
            _clipPositionLabel.Width = Math.Max(180, videoWidth - 24);
        }
        if (_clipSeekTrackBar != null) _clipSeekTrackBar.Width = Math.Max(220, width - 24);
    }

    // ------------------------------------------------------------------
    // クリッピング画面
    // ------------------------------------------------------------------

    // クリッピング画面の数値ボックス宛てのスペースキーを、ボックスへ届く前に受け取るフィルター。
    // 日本語入力のときは IME 経由でキーや文字が届くため、そちらのメッセージも受ける。
    private sealed class ClipBoxKeyFilter : IMessageFilter
    {
        private const int WmKeyDown = 0x0100;
        private const int WmChar = 0x0102;
        private const int WmImeKeyDown = 0x0290;
        private const int WmImeChar = 0x0286;
        private readonly MainForm _form;
        private readonly Control[] _boxes;

        public ClipBoxKeyFilter(MainForm form, Control[] boxes)
        {
            _form = form;
            _boxes = boxes;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (_form.IsDisposed) return false;
            var isSpaceKey = (m.Msg == WmKeyDown || m.Msg == WmImeKeyDown) && m.WParam == (IntPtr)Keys.Space;
            var isSpaceChar = (m.Msg == WmChar || m.Msg == WmImeChar) && (m.WParam == (IntPtr)Keys.Space || m.WParam == (IntPtr)0x3000);
            if (!isSpaceKey && !isSpaceChar) return false;
            if (!IsForBoxes(m.HWnd)) return false;
            if (isSpaceKey) _form.TogglePreviewPlaybackFromSpace();
            return true;
        }

        private bool IsForBoxes(IntPtr hwnd)
        {
            for (var handle = hwnd; handle != IntPtr.Zero; handle = GetParent(handle))
            {
                foreach (var box in _boxes)
                {
                    if (handle == box.Handle) return true;
                }
            }
            return false;
        }
    }

    private void ShowClippingForm(Segment? initialClipValues = null)
    {
        if (_previewPopupForm is { IsDisposed: false }) _previewPopupForm.Close();
        if (!_previewReady || !TestPlayerAvailable())
        {
            MessageBox.Show("先に動画を読み込み、プレビューの準備が完了するまで待ってください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_clipForm is { IsDisposed: false })
        {
            if (initialClipValues != null)
            {
                SetClipBoxValues(initialClipValues.Top, initialClipValues.Right, initialClipValues.Bottom, initialClipValues.Left);
            }
            _clipForm.Activate();
            return;
        }

        if (initialClipValues != null)
        {
            _clipCurrent = new Segment
            {
                Top = initialClipValues.Top,
                Right = initialClipValues.Right,
                Bottom = initialClipValues.Bottom,
                Left = initialClipValues.Left,
            };
        }
        var dimensions = GetVideoDimensions();
        var clipForm = new Form
        {
            Icon = AppIcon.Value,
            Text = "AsterCut - クリッピング",
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1120, 780),
            MinimumSize = new Size(760, 520),
            BackColor = Theme.PageBack,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.None,
            Font = Font,
        };

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        var settings = new Panel
        {
            Dock = DockStyle.Right,
            Size = new Size(270, 780),
            Width = 270,
            AutoScroll = true,
            BackColor = Theme.PanelBack,
        };
        var previewPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Size = new Size(1120, 126),
            Height = 126,
            BackColor = Theme.PanelBack,
        };
        clipForm.Controls.Add(content);
        clipForm.Controls.Add(bottom);
        content.Controls.Add(previewPanel);
        content.Controls.Add(settings);

        _mpvHostPanel.Dock = DockStyle.Fill;
        previewPanel.Controls.Add(_mpvHostPanel);
        // mpv のネイティブ HWND は通常の兄弟 Panel より前面に描画されるため、
        // プレビュー領域の上に独立した色透過フォームを重ねて黒帯を描画する。
        var overlay = new TransparentOverlayForm
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            ControlBox = false,
            Text = "",
            BackColor = Color.Fuchsia,
            TransparencyKey = Color.Fuchsia,
            ClientSize = previewPanel.ClientSize,
        };

        var clipPosition = Theme.MakeLabel("再生位置: --:--:--.---", 12, 4, 700, 22);
        clipPosition.TextAlign = ContentAlignment.MiddleCenter;
        clipPosition.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var clipSeek = new SeekTrackBar
        {
            Minimum = 0, Maximum = 10000, Value = 0,
            TickFrequency = 1000, SmallChange = 10, LargeChange = 100,
            AutoSize = false,
            Location = new Point(8, 26),
            Size = new Size(700, 34),
        };
        var clipSeekStart = new SeekIconButton("First", 8, 76, 34, 30, "先頭へシーク", _seekToolTip);
        var clipFrameBack = new SeekIconButton("Previous", 46, 76, 34, 30, "1 フレーム戻る", _seekToolTip);
        var clipPlay = Theme.MakeButton("再生", 84, 76, 92, 30);
        var clipFrameForward = new SeekIconButton("Next", 182, 76, 34, 30, "1 フレーム進む", _seekToolTip);
        var clipSeekEnd = new SeekIconButton("Last", 220, 76, 34, 30, "末尾へシーク", _seekToolTip);
        foreach (var control in new Control[] { clipPosition, clipSeek, clipSeekStart, clipFrameBack, clipPlay, clipFrameForward, clipSeekEnd })
        {
            bottom.Controls.Add(control);
        }

        var settingsTitle = Theme.MakeLabel("クリッピング範囲 (px)", 14, 14, 230, 26);
        settingsTitle.Font = new Font(clipForm.Font, FontStyle.Bold);
        settings.Controls.Add(settingsTitle);
        settings.Controls.Add(Theme.MakeLabel($"元動画: {dimensions.Width} × {dimensions.Height} px", 14, 46, 230, 22));
        var resolutionLabel = Theme.MakeLabel("", 14, 68, 230, 22);
        settings.Controls.Add(resolutionLabel);
        settings.Controls.Add(Theme.MakeLabel("映像上の白いハンドル（四辺）をドラッグすると、その辺だけを調整できます。数値入力も連動します。", 14, 96, 240, 72));

        NumericUpDown MakeNumberBox(int top, int left)
        {
            return new NumericUpDown
            {
                Minimum = 0,
                Maximum = Math.Max(dimensions.Width, dimensions.Height),
                DecimalPlaces = 0,
                Increment = 1,
                ThousandsSeparator = true,
                Width = 105,
                Height = 28,
                Location = new Point(left, top),
                Font = new Font("Segoe UI", 10),
                BackColor = Theme.BoxBack,
                ForeColor = Color.White,
            };
        }
        settings.Controls.Add(Theme.MakeLabel("上", 14, 172, 70, 24));
        var topBox = MakeNumberBox(196, 14);
        settings.Controls.Add(topBox);
        settings.Controls.Add(Theme.MakeLabel("右", 14, 236, 70, 24));
        var rightBox = MakeNumberBox(260, 14);
        settings.Controls.Add(rightBox);
        settings.Controls.Add(Theme.MakeLabel("下", 14, 300, 70, 24));
        var bottomBox = MakeNumberBox(324, 14);
        settings.Controls.Add(bottomBox);
        settings.Controls.Add(Theme.MakeLabel("左", 14, 364, 70, 24));
        var leftBox = MakeNumberBox(388, 14);
        settings.Controls.Add(leftBox);
        var resetButton = Theme.MakeButton("クリッピングをリセット", 14, 440, 220, 34);
        settings.Controls.Add(resetButton);
        settings.Controls.Add(Theme.MakeLabel("クリッピング範囲外は黒帯で隠して表示します。", 14, 490, 240, 38));
        var pixelUnitNote = Theme.MakeLabel("※ 数値は元動画のピクセル単位です。", 14, 532, 240, 42);
        pixelUnitNote.AutoSize = false;
        settings.Controls.Add(pixelUnitNote);

        _clipForm = clipForm;
        _clipVideoPanel = previewPanel;
        _clipOverlay = overlay;
        _clipPreviewPanel = bottom;
        _clipSeekTrackBar = clipSeek;
        _clipPositionLabel = clipPosition;
        _clipPlayButton = clipPlay;
        _clipTopBox = topBox;
        _clipRightBox = rightBox;
        _clipBottomBox = bottomBox;
        _clipLeftBox = leftBox;
        _clipResolutionLabel = resolutionLabel;
        _clipPreviewSeeking = false;
        _clipMpvRect = null;
        _clipMpvRectPanelSize = Size.Empty;
        _clipSavedVolume = GetPlayerVolume();
        SetPlayerVolume(0.0);
        SetClipBoxValues(_clipCurrent.Top, _clipCurrent.Right, _clipCurrent.Bottom, _clipCurrent.Left);

        overlay.Paint += (_, e) =>
        {
            if (_videoStreams.Count == 0) return;
            var rect = GetClipDisplayRect(overlay);
            if (rect.Width <= 0 || rect.Height <= 0) return;
            var values = GetCurrentClipValues();
            var dimensionsNow = GetVideoDimensions();
            var left = rect.X + values.Left / (double)dimensionsNow.Width * rect.Width;
            var right = rect.Right - values.Right / (double)dimensionsNow.Width * rect.Width;
            var top = rect.Y + values.Top / (double)dimensionsNow.Height * rect.Height;
            var bottomEdge = rect.Bottom - values.Bottom / (double)dimensionsNow.Height * rect.Height;
            var g = e.Graphics;
            using var mask = new SolidBrush(Color.Black);
            using var border = new Pen(Color.White, 2.0f);
            using var handleFill = new SolidBrush(Color.White);
            using var handleBorder = new Pen(Color.FromArgb(255, 140, 110, 255), 2.0f);
            // 色透過フォームの透明部分には映像が見え、黒い矩形部分だけが黒帯として重なる。
            if (top > rect.Top) g.FillRectangle(mask, rect.Left, rect.Top, rect.Width, (float)(top - rect.Top));
            if (bottomEdge < rect.Bottom) g.FillRectangle(mask, rect.Left, (float)bottomEdge, rect.Width, (float)(rect.Bottom - bottomEdge));
            if (left > rect.Left) g.FillRectangle(mask, rect.Left, (float)top, (float)(left - rect.Left), (float)(bottomEdge - top));
            if (right < rect.Right) g.FillRectangle(mask, (float)right, (float)top, (float)(rect.Right - right), (float)(bottomEdge - top));
            // 四辺を常に表示し、各辺の中央ハンドルをドラッグできるようにする。
            g.DrawLine(border, (float)left, (float)top, (float)right, (float)top);
            g.DrawLine(border, (float)right, (float)top, (float)right, (float)bottomEdge);
            g.DrawLine(border, (float)right, (float)bottomEdge, (float)left, (float)bottomEdge);
            g.DrawLine(border, (float)left, (float)bottomEdge, (float)left, (float)top);
            const float handleSize = 18.0f;
            var handles = new[]
            {
                new RectangleF((float)((left + right) / 2.0 - handleSize / 2.0), (float)(top - handleSize / 2.0), handleSize, handleSize),
                new RectangleF((float)(right - handleSize / 2.0), (float)((top + bottomEdge) / 2.0 - handleSize / 2.0), handleSize, handleSize),
                new RectangleF((float)((left + right) / 2.0 - handleSize / 2.0), (float)(bottomEdge - handleSize / 2.0), handleSize, handleSize),
                new RectangleF((float)(left - handleSize / 2.0), (float)((top + bottomEdge) / 2.0 - handleSize / 2.0), handleSize, handleSize),
            };
            foreach (var handle in handles)
            {
                g.FillEllipse(handleFill, handle);
                g.DrawEllipse(handleBorder, handle);
            }
        };
        overlay.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var edge = GetClipDragEdge(e.Location);
            if (string.IsNullOrEmpty(edge)) return;
            _clipMouseDragging = true;
            _clipDragEdge = edge;
            _clipDragStart = GetCurrentClipValues();
            overlay.Capture = true;
            overlay.Cursor = edge is "Top" or "Bottom" ? Cursors.SizeNS : Cursors.SizeWE;
        };
        overlay.MouseMove += (_, e) =>
        {
            if (_clipMouseDragging && (e.Button & MouseButtons.Left) != 0)
            {
                UpdateClipCropFromDrag(e.Location);
            }
            else
            {
                var edge = GetClipDragEdge(e.Location);
                overlay.Cursor = edge is "Top" or "Bottom" ? Cursors.SizeNS
                    : edge is "Left" or "Right" ? Cursors.SizeWE
                    : Cursors.Default;
            }
        };
        overlay.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _clipMouseDragging)
            {
                UpdateClipCropFromDrag(e.Location);
                _clipMouseDragging = false;
                _clipDragStart = null;
                _clipDragEdge = "";
                overlay.Capture = false;
                overlay.Cursor = Cursors.Default;
            }
        };
        overlay.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                TogglePreviewPlaybackFromSpace();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                clipForm.Close();
            }
        };
        foreach (var box in new[] { topBox, rightBox, bottomBox, leftBox })
        {
            box.ValueChanged += (_, _) => UpdateClipValuesFromInputs();
            // 数値だけを入れる欄なので IME は無効にする (日本語入力のままだと、スペースが
            // IME 経由の文字として届き、キーイベントやメッセージでは止められず音が鳴るため)。
            box.ImeMode = ImeMode.Disable;
        }
        // 数値ボックス宛てのスペースキーは、ボックスやフォームのキーイベントで抑えても
        // 入力検証の音が止まらなかったため、メッセージの段階で受け取るフィルターを付ける。
        _clipBoxKeyFilter = new ClipBoxKeyFilter(this, new Control[] { topBox, rightBox, bottomBox, leftBox });
        Application.AddMessageFilter(_clipBoxKeyFilter);
        resetButton.Click += (_, _) => SetClipBoxValues(0, 0, 0, 0);

        clipSeek.MouseDown += (_, _) => { if (_previewReady) { _previewSeeking = false; _popupPreviewSeeking = false; _clipPreviewSeeking = true; } };
        clipSeek.MouseUp += (_, _) =>
        {
            if (_clipPreviewSeeking)
            {
                _clipPreviewSeeking = false;
                SetPreviewPositionFromTrackBar(clipSeek);
            }
        };
        clipSeek.Scroll += (_, _) => SetPreviewPositionFromTrackBar(clipSeek);
        clipSeek.KeyDown += (_, e) => HandleSeekTrackBarKeyDown(e);
        clipSeek.KeyUp += (_, e) => HandleSeekTrackBarKeyUp(clipSeek, e);
        clipSeek.MouseWheel += (_, e) => HandleSeekTrackBarMouseWheel(e);
        clipSeekStart.Click += (_, _) => MovePreviewToBoundary(false);
        clipFrameBack.Click += (_, _) => MovePreviewFrame(-1);
        clipPlay.Click += (_, _) => TogglePreviewPlayback();
        clipFrameForward.Click += (_, _) => MovePreviewFrame(1);
        clipSeekEnd.Click += (_, _) => MovePreviewToBoundary(true);
        clipForm.Resize += (_, _) => { LayoutClippingControls(); SyncClippingOverlay(); };
        // リサイズのドラッグが終わった時点で、mpv の描画が落ち着いてから範囲を取り直す。
        clipForm.ResizeEnd += (_, _) => { _clipMpvRectPanelSize = Size.Empty; SyncClippingOverlay(); };
        clipForm.LocationChanged += (_, _) => SyncClippingOverlay();
        previewPanel.SizeChanged += (_, _) => SyncClippingOverlay();
        previewPanel.LocationChanged += (_, _) => SyncClippingOverlay();
        clipForm.FormClosing += (_, _) =>
        {
            try { if (_clipOverlay is { IsDisposed: false }) { _clipOverlay.Close(); _clipOverlay.Dispose(); } } catch { }
            try
            {
                _mpvHostPanel.Dock = DockStyle.None;
                GetPreviewReturnTab().Controls.Add(_mpvHostPanel);
                RestoreMpvHostPanelHome();
                _mpvHostPanel.Visible = true;
                FitMpvWindow();
            }
            catch { }
            try { SetPlayerVolume(_clipSavedVolume); } catch { }
            if (_clipBoxKeyFilter != null)
            {
                Application.RemoveMessageFilter(_clipBoxKeyFilter);
                _clipBoxKeyFilter = null;
            }
            _clipForm = null;
            _clipVideoPanel = null;
            _clipOverlay = null;
            _clipPreviewPanel = null;
            _clipSeekTrackBar = null;
            _clipPositionLabel = null;
            _clipPlayButton = null;
            _clipTopBox = null;
            _clipRightBox = null;
            _clipBottomBox = null;
            _clipLeftBox = null;
            _clipResolutionLabel = null;
            _clipPreviewSeeking = false;
            _clipMouseDragging = false;
            _clipDragStart = null;
            _clipDragEdge = "";
            _clipMpvRect = null;
            _clipMpvRectPanelSize = Size.Empty;
        };
        // Esc キーで閉じる。スペースキーで再生 / 一時停止する。
        clipForm.KeyDown += (sender, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ((Form)sender!).Close();
            }
            else if (e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                TogglePreviewPlaybackFromSpace();
            }
        };
        clipForm.Shown += (_, _) =>
        {
            LayoutClippingControls();
            UpdatePreviewPositionLabel();
            SyncClippingOverlay();
            clipForm.Activate();
        };
        clipForm.Show(this);
        // 開いた直後にアクティブにする (映像のホストが先にフォーカスを取ると、非アクティブのまま開くことがある)。
        clipForm.Activate();
        LayoutClippingControls();
        SyncClippingOverlay();
        UpdatePreviewPlaybackButtons();
        UpdatePreviewPositionLabel();
        FitMpvWindow();
    }
}
