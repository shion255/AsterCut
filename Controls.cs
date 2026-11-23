using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// 自前描画のシークバー。
/// トラックとつまみを自前で描き、クリックとドラッグは自前で値へ変換する。
/// 土台は標準の TrackBar ではなく Control にする。標準コントロールはキーボード操作のときに
/// 自前のつまみを直接描くため、自前描画のはみ出し部分が跡として残ってしまうため。
/// </summary>
/// <summary>
/// 保持する音声トラック用の一覧。CheckedListBox は DrawItem イベントによる
/// オーナードローに対応していない (描画モードが無視される) ため、OnDrawItem を
/// 書き換えて、チェックの箱とチェックマークを UI のテーマに合わせた色で描く。
/// </summary>
internal sealed class ThemedCheckedListBox : CheckedListBox
{
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            base.OnDrawItem(e);
            return;
        }
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var backBrush = new SolidBrush(selected ? Color.FromArgb(86, 74, 110) : Theme.BoxBack))
        {
            e.Graphics.FillRectangle(backBrush, e.Bounds);
        }
        var boxSize = Math.Max(13, e.Bounds.Height - 6);
        var boxRect = new Rectangle(e.Bounds.X + 3, e.Bounds.Y + (e.Bounds.Height - boxSize) / 2, boxSize, boxSize);
        using (var boxBrush = new SolidBrush(Theme.BoxBack))
        {
            e.Graphics.FillRectangle(boxBrush, boxRect);
        }
        using (var borderPen = new Pen(Color.FromArgb(88, 80, 110)))
        {
            e.Graphics.DrawRectangle(borderPen, boxRect);
        }
        if (GetItemCheckState(e.Index) == CheckState.Checked)
        {
            var points = new[]
            {
                new PointF(boxRect.X + boxSize * 0.20f, boxRect.Y + boxSize * 0.52f),
                new PointF(boxRect.X + boxSize * 0.42f, boxRect.Y + boxSize * 0.74f),
                new PointF(boxRect.X + boxSize * 0.80f, boxRect.Y + boxSize * 0.26f),
            };
            var oldSmoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var checkPen = new Pen(Color.FromArgb(150, 128, 205), 2f);
            e.Graphics.DrawLines(checkPen, points);
            e.Graphics.SmoothingMode = oldSmoothing;
        }
        var textRect = new Rectangle(boxRect.Right + 6, e.Bounds.Y, e.Bounds.Width - (boxRect.Right + 6 - e.Bounds.X), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString() ?? "", Font, textRect, Theme.BoxFore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

internal class SeekTrackBar : Control
{
    private bool _manualDragging;
    private bool _hot;
    private int _minimum;
    private int _maximum = 10;
    private int _value;
    private int _wheelRemainder;
    private const int TrackMargin = 16;
    private const int ThumbWidth = 12;
    private const int ThumbHeight = 22;

    public event EventHandler? Scroll;
    public event EventHandler? ValueChanged;

    public SeekTrackBar()
    {
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
    }

    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum == value) return;
            _minimum = value;
            if (_maximum < _minimum) _maximum = _minimum;
            Value = Math.Max(_minimum, _value);
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum == value) return;
            _maximum = value;
            if (_minimum > _maximum) _minimum = _maximum;
            Value = Math.Min(_maximum, _value);
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Max(_minimum, Math.Min(_maximum, value));
            if (_value == next) return;
            _value = next;
            OnValueChanged(EventArgs.Empty);
        }
    }

    public int SmallChange { get; set; } = 1;

    public int LargeChange { get; set; } = 5;

    public int TickFrequency { get; set; } = 10;

    // 矢印キーなどは、入力キーとして扱わないとフォーカス移動に使われてしまい、キー操作を受け取れない。
    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.PageUp:
            case Keys.PageDown:
            case Keys.Home:
            case Keys.End:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected virtual void OnScroll(EventArgs e) => Scroll?.Invoke(this, e);

    protected virtual void OnValueChanged(EventArgs e)
    {
        ValueChanged?.Invoke(this, e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // アプリ側のハンドラーが処理済み (シークバーのフレーム移動など) の場合は、標準のキー動作をしない。
        if (e.Handled) return;
        var target = _value;
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                target = _value - SmallChange;
                break;
            case Keys.Right:
            case Keys.Up:
                target = _value + SmallChange;
                break;
            case Keys.PageDown:
                target = _value - LargeChange;
                break;
            case Keys.PageUp:
                target = _value + LargeChange;
                break;
            case Keys.Home:
                target = Minimum;
                break;
            case Keys.End:
                target = Maximum;
                break;
            default:
                return;
        }
        target = Math.Max(Minimum, Math.Min(Maximum, target));
        if (target != _value)
        {
            Value = target;
            OnScroll(EventArgs.Empty);
        }
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // アプリ側のハンドラーが処理済み (シークバーのフレーム移動など) の場合は、標準のホイール動作をしない。
        if (e is HandledMouseEventArgs { Handled: true }) return;
        _wheelRemainder += e.Delta;
        while (_wheelRemainder >= 120)
        {
            _wheelRemainder -= 120;
            StepByWheel(LargeChange);
        }
        while (_wheelRemainder <= -120)
        {
            _wheelRemainder += 120;
            StepByWheel(-LargeChange);
        }
    }

    private void StepByWheel(int delta)
    {
        var target = Math.Max(Minimum, Math.Min(Maximum, _value + delta));
        if (target != _value)
        {
            Value = target;
            OnScroll(EventArgs.Empty);
        }
    }

    private Rectangle GetThumbRect()
    {
        var travel = Math.Max(1, ClientSize.Width - TrackMargin * 2);
        var range = (double)(Maximum - Minimum);
        var fraction = range > 0 ? (double)(Value - Minimum) / range : 0.0;
        var cx = TrackMargin + (int)Math.Round(fraction * travel);
        var cy = ClientSize.Height / 2;
        return new Rectangle(cx - ThumbWidth / 2, cy - ThumbHeight / 2, ThumbWidth, ThumbHeight);
    }

    private bool IsOverThumb(int x, int y)
    {
        var r = GetThumbRect();
        r.Inflate(3, 3);
        return r.Contains(x, y);
    }

    private void SetHot(bool hot)
    {
        if (_hot != hot)
        {
            _hot = hot;
            Invalidate();
        }
    }

    private Color ResolveBackColor()
    {
        Control? c = Parent;
        while (c != null)
        {
            if (c.BackColor.A == 255) return c.BackColor;
            c = c.Parent;
        }
        return SystemColors.Control;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(ResolveBackColor());
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var enabled = Enabled;
        var cy = ClientSize.Height / 2;
        var channel = new Rectangle(TrackMargin, cy - 2, Math.Max(1, ClientSize.Width - TrackMargin * 2), 4);
        using (var channelBrush = new SolidBrush(enabled ? Color.FromArgb(104, 98, 120) : Color.FromArgb(66, 62, 78)))
        {
            g.FillRectangle(channelBrush, channel);
        }
        var thumb = GetThumbRect();
        var thumbShape = new Rectangle(thumb.X, thumb.Y, thumb.Width - 1, thumb.Height - 1);
        var fill = !enabled ? Color.FromArgb(88, 84, 100) : (_hot ? Color.FromArgb(178, 158, 224) : Color.FromArgb(125, 105, 168));
        var border = !enabled ? Color.FromArgb(114, 108, 126) : Color.FromArgb(238, 235, 246);
        using var path = RoundedRect(thumbShape, 3);
        using var thumbBrush = new SolidBrush(fill);
        using var borderPen = new Pen(border, 1.0f);
        g.FillPath(thumbBrush, path);
        g.DrawPath(borderPen, path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 先に MouseDown を発生させ、アプリ側のハンドラーがシークモードへ切り替えられるようにする。
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            _manualDragging = true;
            Capture = true;
            SetFromMouseX(e.X);
            SetHot(true);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_manualDragging && (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left)
        {
            SetFromMouseX(e.X);
        }
        SetHot(_manualDragging || IsOverThumb(e.X, e.Y));
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_manualDragging) SetHot(false);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _manualDragging)
        {
            SetFromMouseX(e.X);
            _manualDragging = false;
            Capture = false;
            SetHot(IsOverThumb(e.X, e.Y));
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture)
        {
            _manualDragging = false;
            if (!ClientRectangle.Contains(PointToClient(Control.MousePosition))) SetHot(false);
        }
        base.OnMouseCaptureChanged(e);
    }

    private void SetFromMouseX(int x)
    {
        var travel = Math.Max(1, ClientSize.Width - TrackMargin * 2);
        var fraction = Math.Max(0.0, Math.Min(1.0, (double)(x - TrackMargin) / travel));
        var next = Minimum + (int)Math.Round(fraction * (Maximum - Minimum));
        next = Math.Max(Minimum, Math.Min(Maximum, next));
        if (next != Value)
        {
            Value = next;
            OnScroll(EventArgs.Empty);
        }
    }
}

/// <summary>埋め込んだ mpv の子ウィンドウを親いっぱいに広げる。</summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    public static bool FitFirstChild(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return false;
        var child = GetWindow(parent, 5); // GW_CHILD
        if (child == IntPtr.Zero) return false;
        if (!GetClientRect(parent, out var rect)) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;
        return MoveWindow(child, 0, 0, width, height, true);
    }
}

/// <summary>
/// 音量ボタン。絵文字 (🔊/🔇) はこの環境の描画では暗く潰れてしまうため、
/// シークボタンのアイコンと同じくスピーカーの絵を自前で描く。
/// </summary>
internal sealed class VolumeIconButton : Button
{
    private bool _muted;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted != value)
            {
                _muted = value;
                Invalidate();
            }
        }
    }

    public VolumeIconButton(int x, int y, int w, int h)
    {
        Text = "";
        Location = new Point(x, y);
        Size = new Size(w, h);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Color.FromArgb(88, 80, 110);
        BackColor = Color.FromArgb(58, 54, 72);
        ForeColor = Color.FromArgb(246, 245, 250);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ForeColor);
        using var pen = new Pen(ForeColor, 1.8f);
        // 絵全体の外接範囲がボタンの中央に来るよう、原点を右へ 2px・上へ 1px ずらす。
        var ox = ClientSize.Width / 2 + 2;
        var oy = ClientSize.Height / 2 - 1;
        // スピーカー本体は左半分に置く。
        g.FillRectangle(brush, ox - 11, oy - 3, 4, 6);
        var cone = new[]
        {
            new Point(ox - 7, oy - 3),
            new Point(ox - 2, oy - 8),
            new Point(ox - 2, oy + 8),
            new Point(ox - 7, oy + 3),
        };
        g.FillPolygon(brush, cone);
        if (_muted)
        {
            // ミュート時は、音波の 1 つ目と同じ距離に × を描く。
            g.DrawLine(pen, ox, oy - 4, ox + 5, oy + 4);
            g.DrawLine(pen, ox, oy + 4, ox + 5, oy - 4);
        }
        else
        {
            // 波紋は同心円の弧にする。弧の大きさは変えず、中心をスピーカー側へ寄せて間隔を狭める。
            g.DrawArc(pen, ox - 9, oy - 5, 10, 10, -45, 90);
            g.DrawArc(pen, ox - 13, oy - 9, 18, 18, -45, 90);
        }
        g.SmoothingMode = oldSmoothing;
    }
}

/// <summary>三角形と縦線を描くシーク用アイコンボタン。</summary>
internal sealed class SeekIconButton : Button
{
    public string Icon { get; }

    public SeekIconButton(string icon, int x, int y, int w, int h, string toolTipText, ToolTip toolTip)
    {
        Icon = icon;
        Text = "";
        Location = new Point(x, y);
        Size = new Size(w, h);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = Color.FromArgb(88, 80, 110);
        BackColor = Color.FromArgb(58, 54, 72);
        ForeColor = Color.FromArgb(246, 245, 250);
        Cursor = Cursors.Hand;
        AccessibleName = toolTipText;
        AccessibleDescription = toolTipText;
        toolTip.SetToolTip(this, toolTipText);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ForeColor);
        using var pen = new Pen(ForeColor, 2.2f);
        var cx = ClientSize.Width / 2;
        var cy = ClientSize.Height / 2;
        var points = new Point[3];
        switch (Icon)
        {
            case "Previous":
                points[0] = new Point(cx - 7, cy);
                points[1] = new Point(cx + 6, cy - 8);
                points[2] = new Point(cx + 6, cy + 8);
                break;
            case "Next":
                points[0] = new Point(cx + 7, cy);
                points[1] = new Point(cx - 6, cy - 8);
                points[2] = new Point(cx - 6, cy + 8);
                break;
            case "First":
                g.DrawLine(pen, cx - 10, cy - 10, cx - 10, cy + 10);
                points[0] = new Point(cx - 6, cy);
                points[1] = new Point(cx + 7, cy - 8);
                points[2] = new Point(cx + 7, cy + 8);
                break;
            case "Last":
                g.DrawLine(pen, cx + 10, cy - 10, cx + 10, cy + 10);
                points[0] = new Point(cx + 6, cy);
                points[1] = new Point(cx - 7, cy - 8);
                points[2] = new Point(cx - 7, cy + 8);
                break;
        }
        g.FillPolygon(brush, points);
        g.SmoothingMode = oldSmoothing;
    }
}

/// <summary>
/// クリッピング用の色透過フォーム。
/// 再描画をダブルバッファリングし、枠のちらつきを抑える。
/// </summary>
internal sealed class TransparentOverlayForm : Form
{
    public TransparentOverlayForm()
    {
        // 色透過フォームの背景消去と描画を 1 回の更新にまとめ、再描画途中のちらつきを防ぐ。
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        UpdateStyles();
    }
}

/// <summary>
/// 矢印を線で描くボタン。
/// フォントの ↑ ↓ の文字では、↓ の矢じりが切れてしまうことがあるため、自前で描く。
/// </summary>
internal sealed class ArrowButton : Button
{
    private bool _hover;
    private bool _pressed;

    public bool DirectionUp { get; set; }

    public ArrowButton()
    {
        FlatStyle = FlatStyle.Flat;
        BackColor = Color.FromArgb(58, 54, 72);
        ForeColor = Color.FromArgb(246, 245, 250);
        FlatAppearance.BorderColor = Color.FromArgb(88, 80, 110);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var rect = ClientRectangle;
        var back = _pressed ? Color.FromArgb(42, 38, 54)
            : _hover ? Color.FromArgb(72, 64, 92)
            : BackColor;
        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, rect);
        using (var borderPen = new Pen(FlatAppearance.BorderColor))
            e.Graphics.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

        // 矢印はボタンの中心に、上下対称の形で描く。
        var cx = rect.Width / 2f;
        var cy = rect.Height / 2f;
        var top = cy - 5f;
        var bottom = cy + 5f;
        const float head = 4f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(ForeColor, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        if (DirectionUp)
        {
            e.Graphics.DrawLine(pen, cx, bottom, cx, top);
            e.Graphics.DrawLine(pen, cx - head, top + head, cx, top);
            e.Graphics.DrawLine(pen, cx + head, top + head, cx, top);
        }
        else
        {
            e.Graphics.DrawLine(pen, cx, top, cx, bottom);
            e.Graphics.DrawLine(pen, cx - head, bottom - head, cx, bottom);
            e.Graphics.DrawLine(pen, cx + head, bottom - head, cx, bottom);
        }
    }
}

/// <summary>
/// フォーカスを受け取れる Panel。通常の Panel は選択可能でないため、プレビュー画面を
/// クリックしたときに入力欄からフォーカスを外す移し先として使う。
/// </summary>
internal sealed class FocusablePanel : Panel
{
    public FocusablePanel()
    {
        SetStyle(ControlStyles.Selectable, true);
    }
}

/// <summary>
/// RichTextBox のマウスホイールスクロールを、標準の処理 (アニメーション付きで、回してから
/// 動くまで遅れがある) ではなく EM_LINESCROLL による直接スクロールへ置き換えるフック。
/// Ctrl を押しながらのホイール (標準のズーム) は、そのまま標準の処理へ通す。
/// </summary>
internal sealed class RichTextBoxWheelHook : NativeWindow
{
    private const int WmMouseWheel = 0x020A;
    private const int EmLineScroll = 0x00B6;
    private const int MkControl = 0x0008;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private readonly RichTextBox _box;
    private int _remainder;

    public RichTextBoxWheelHook(RichTextBox box)
    {
        _box = box;
        if (box.IsHandleCreated) AssignHandle(box.Handle);
        box.HandleCreated += (_, _) =>
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
            AssignHandle(box.Handle);
        };
        box.HandleDestroyed += (_, _) =>
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseWheel && (m.WParam.ToInt64() & MkControl) == 0)
        {
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            // 高解像度ホイールの細かい回転は、余りを蓄積して取りこぼさない。
            _remainder += delta;
            var notches = _remainder / 120;
            _remainder -= notches * 120;
            if (notches != 0)
            {
                var linesPerNotch = SystemInformation.MouseWheelScrollLines;
                if (linesPerNotch < 0)
                {
                    // Windows の「1 画面ずつスクロール」設定では、見えている行数分を送る。
                    linesPerNotch = Math.Max(1, _box.ClientSize.Height / Math.Max(1, _box.Font.Height));
                }
                else if (linesPerNotch == 0)
                {
                    linesPerNotch = 3;
                }
                // ホイールを上へ回す (delta が正) と、上へスクロールする。
                SendMessage(_box.Handle, EmLineScroll, IntPtr.Zero, (IntPtr)(-notches * linesPerNotch));
            }
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// ドロップダウンボタンをテーマ色で描くコンボボックス。
/// 標準の描画ではボタンが OS の見た目 (白) になってテーマに馴染まないため、
/// 描画のたびにボタン部分をテーマ色で描き直す。
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    private const int WmPaint = 0x000F;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint || DropDownStyle != ComboBoxStyle.DropDownList) return;
        using var graphics = CreateGraphics();
        // 枠線も OS の色 (白など) で描かれてしまうため、テーマの枠色で描き直す。
        using (var pen = new Pen(Color.FromArgb(88, 80, 110)))
        {
            graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
        var buttonWidth = SystemInformation.VerticalScrollBarWidth;
        var buttonRect = new Rectangle(Width - buttonWidth - 1, 1, buttonWidth, Height - 2);
        using (var brush = new SolidBrush(Enabled ? Color.FromArgb(58, 54, 72) : Color.FromArgb(44, 42, 54)))
        {
            graphics.FillRectangle(brush, buttonRect);
        }
        var centerX = buttonRect.Left + buttonRect.Width / 2;
        var centerY = buttonRect.Top + buttonRect.Height / 2;
        var arrow = new[]
        {
            new Point(centerX - 4, centerY - 2),
            new Point(centerX + 5, centerY - 2),
            new Point(centerX, centerY + 3),
        };
        using var arrowBrush = new SolidBrush(Enabled ? Color.FromArgb(239, 238, 244) : Theme.MutedFore);
        graphics.FillPolygon(arrowBrush, arrow);
    }
}

/// <summary>
/// タブで覆われていない帯の部分やページ周りの枠が OS の色 (白) で描かれてしまうため、
/// 描画のたびにテーマの色で塗り直すタブコントロール。
/// </summary>
internal sealed class ThemedTabControl : TabControl
{
    private const int WmPaint = 0x000F;
    private const int TcmAdjustRect = 0x1328;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref NativeRect lParam);

    private Rectangle GetDisplayRect()
    {
        var rect = new NativeRect { Left = 0, Top = 0, Right = Width, Bottom = Height };
        SendMessage(Handle, TcmAdjustRect, IntPtr.Zero, ref rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint || TabCount == 0) return;
        try
        {
            using var graphics = CreateGraphics();
            var stripBottom = 0;
            for (var i = 0; i < TabCount; i++) stripBottom = Math.Max(stripBottom, GetTabRect(i).Bottom);
            var display = GetDisplayRect();
            // ページの内側以外をすべてテーマの色で塗り直し、タブも描き直す
            // (OS が描くタブの枠線が白く残ってしまうため)。
            using (var pageBack = new SolidBrush(Theme.PageBack))
            {
                graphics.FillRectangle(pageBack, 0, 0, Width, display.Top);
                if (display.Left > 0) graphics.FillRectangle(pageBack, 0, display.Top, display.Left, Height - display.Top);
                if (display.Right < Width) graphics.FillRectangle(pageBack, display.Right, display.Top, Width - display.Right, Height - display.Top);
                if (display.Bottom < Height) graphics.FillRectangle(pageBack, 0, display.Bottom, Width, Height - display.Bottom);
            }
            for (var i = 0; i < TabCount; i++)
            {
                var rect = GetTabRect(i);
                var isSelected = i == SelectedIndex;
                using (var brush = new SolidBrush(isSelected ? Theme.Accent : Color.FromArgb(46, 43, 56)))
                {
                    graphics.FillRectangle(brush, rect);
                }
                // タブ同士の境目が分かるよう、テーマの枠色で囲む。
                using (var borderPen = new Pen(Color.FromArgb(88, 80, 110)))
                {
                    graphics.DrawRectangle(borderPen, rect.Left, rect.Top, rect.Width - 1, rect.Height - 1);
                }
                TextRenderer.DrawText(graphics, TabPages[i].Text, Font, rect, isSelected ? Color.White : Color.FromArgb(192, 186, 204),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            // ページ周りの枠は、上の辺をタブの下端に合わせて描く (タブの帯と枠の線が二重にならないように)。
            // 上の辺は左右の縦線の間にだけ引き、角で縦線より外へはみ出さないようにする。
            // 左の縦線は、先頭のタブの左端に合わせる (表示領域の左端より 1ピクセル外にあり、そのままだとタブの左端と縦線がずれるため)。
            var frameLeft = GetTabRect(0).Left;
            using (var border = new Pen(Color.FromArgb(88, 80, 110)))
            {
                graphics.DrawLine(border, frameLeft, stripBottom - 1, display.Right, stripBottom - 1);
                graphics.DrawLine(border, frameLeft, stripBottom - 1, frameLeft, display.Bottom);
                graphics.DrawLine(border, display.Right, stripBottom - 1, display.Right, display.Bottom);
                graphics.DrawLine(border, frameLeft, display.Bottom, display.Right, display.Bottom);
            }
            // 選択中のタブの下線は、枠の上の辺と同じ位置に重ねる。
            if (SelectedIndex >= 0 && SelectedIndex < TabCount)
            {
                var selected = GetTabRect(SelectedIndex);
                using var pen = new Pen(Color.FromArgb(150, 128, 205), 2f);
                graphics.DrawLine(pen, selected.Left + 5, stripBottom - 2, selected.Right - 5, stripBottom - 2);
            }
        }
        catch { }
    }
}

/// <summary>
/// OS のテーマで白く描かれてしまうため、自前で描くプログレスバー。
/// </summary>
internal sealed class ThemedProgressBar : Control
{
    private readonly System.Windows.Forms.Timer _marqueeTimer;
    private int _marqueeOffset;
    private int _minimum;
    private int _maximum = 100;
    private int _value;
    private ProgressBarStyle _style = ProgressBarStyle.Blocks;
    private int _marqueeAnimationSpeed;

    public ThemedProgressBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 24 };
        _marqueeTimer.Tick += (_, _) =>
        {
            _marqueeOffset += 3;
            Invalidate();
        };
    }

    public int Minimum
    {
        get => _minimum;
        set { _minimum = value; Invalidate(); }
    }

    public int Maximum
    {
        get => _maximum;
        set { _maximum = value; Invalidate(); }
    }

    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, _minimum, Math.Max(_minimum, _maximum));
            Invalidate();
        }
    }

    public ProgressBarStyle Style
    {
        get => _style;
        set
        {
            _style = value;
            UpdateMarqueeTimer();
            Invalidate();
        }
    }

    public int MarqueeAnimationSpeed
    {
        get => _marqueeAnimationSpeed;
        set
        {
            _marqueeAnimationSpeed = value;
            if (value > 0) _marqueeTimer.Interval = value;
            UpdateMarqueeTimer();
        }
    }

    private void UpdateMarqueeTimer()
    {
        var run = _style == ProgressBarStyle.Marquee && _marqueeAnimationSpeed > 0;
        if (run && !_marqueeTimer.Enabled) _marqueeTimer.Start();
        if (!run && _marqueeTimer.Enabled) _marqueeTimer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var track = new SolidBrush(Theme.BoxBack)) graphics.FillRectangle(track, outer);
        using (var pen = new Pen(Color.FromArgb(88, 80, 110))) graphics.DrawRectangle(pen, outer);
        var inner = new Rectangle(2, 2, Math.Max(0, Width - 4), Math.Max(0, Height - 4));
        if (inner.Width <= 0 || inner.Height <= 0) return;
        using var fill = new SolidBrush(Theme.Accent);
        if (_style == ProgressBarStyle.Marquee)
        {
            var blockWidth = Math.Max(24, inner.Width / 3);
            var span = inner.Width + blockWidth;
            var x = inner.Left - blockWidth + _marqueeOffset % span;
            var block = new Rectangle(x, inner.Top, blockWidth, inner.Height);
            block.Intersect(inner);
            if (block.Width > 0) graphics.FillRectangle(fill, block);
        }
        else
        {
            var range = _maximum - _minimum;
            if (range > 0 && _value > _minimum)
            {
                var width = (int)((long)inner.Width * (_value - _minimum) / range);
                if (width > 0) graphics.FillRectangle(fill, new Rectangle(inner.Left, inner.Top, width, inner.Height));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _marqueeTimer.Dispose();
        base.Dispose(disposing);
    }
}
