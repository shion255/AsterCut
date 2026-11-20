using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// プレビューのドラッグでメインウィンドウを移動し、
/// ダブルクリックで映像が約 1.25 倍になるよう拡大 / 復元する。
/// 映像部分は mpv の子ウィンドウ (別プロセス) のため、マウス入力はポーリングで拾う。
/// </summary>
internal sealed partial class MainForm
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern bool LockWindowUpdate(IntPtr hWndLock);

    private System.Windows.Forms.Timer _previewDragTimer = null!;
    private string _previewPointerState = "idle";
    private bool _spaceKeyWasDown;
    private bool _spaceEventWasDown;
    private long _lastSpaceToggleTicks;
    private bool _previewKeyboardActive;
    private readonly bool[] _previewArrowWasDown = new bool[4];
    private readonly long[] _previewArrowRepeatTicks = new long[4];
    private static readonly int[] PreviewArrowVirtualKeys = { 0x25, 0x27, 0x26, 0x28 };
    private Point _previewPointerDownPos;
    private Point _previewFormStartPos;
    private bool _previewButtonWasDown;
    private bool _previewRightButtonWasDown;
    private ContextMenuStrip? _previewContextMenu;
    private ToolStripMenuItem? _previewSeekCorrectionMenuItem;
    private bool _previewPressMayDrag;
    private long _previewLastClickTicks;
    private Point _previewLastClickPos;
    private Size? _previewZoomRestoreSize;

    private bool TestPreviewPointerTarget()
    {
        // マウスポインターが、表示中のタブのプレビュー画面上にあるかを返す。
        if (Form.ActiveForm != this) return false;
        return TestPreviewPointerGeometry();
    }

    private bool TestPreviewPointerGeometry()
    {
        // ウィンドウがアクティブかどうかを問わず、マウスポインターがプレビュー画面の範囲内にあるかを返す。
        var panel = _mpvHostPanel;
        if (panel.IsDisposed || !panel.Visible) return false;
        if (WindowState == FormWindowState.Minimized) return false;
        var parentPage = panel.Parent;
        if (parentPage != _cutTab && parentPage != _chapterTab) return false;
        if (_tabs.SelectedTab != parentPage) return false;
        var panelRect = panel.RectangleToScreen(panel.ClientRectangle);
        return panelRect.Contains(Control.MousePosition);
    }

    /// <summary>
    /// スペースキーを再生 / 一時停止に使う場面かどうか。
    /// 動画編集 / チャプタータブで、文字入力にカーソルがない場合に限る。
    /// </summary>
    private bool IsSpaceForPreview()
    {
        if (_tabs.SelectedTab != _cutTab && _tabs.SelectedTab != _chapterTab) return false;
        return !IsTextInputFocused();
    }

    /// <summary>
    /// 文字入力に実際にフォーカスがあるか。プレビューをクリックした後は mpv の子ウィンドウ
    /// (別プロセス) にフォーカスが移り、ActiveControl には直前の入力欄が残ったままになるため、
    /// 実際のフォーカス先から判定する。
    /// </summary>
    private bool IsTextInputFocused()
    {
        var handle = GetFocus();
        if (handle == IntPtr.Zero) return false;
        for (var control = Control.FromHandle(handle); control != null; control = control.Parent)
        {
            if (control is TextBoxBase) return true;
            if (control is ComboBox combo) return combo.DropDownStyle != ComboBoxStyle.DropDownList;
        }
        return false;
    }

    private void HandlePreviewSpaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space || e.Modifiers != Keys.None) return;
        if (!IsSpaceForPreview()) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        // 長押しの繰り返しでは切り替えない。
        if (_spaceEventWasDown) return;
        _spaceEventWasDown = true;
        TogglePreviewPlaybackFromSpace();
    }

    /// <summary>
    /// スペースキーによる再生 / 一時停止。キーイベントとポーリングの両経路から呼ばれるため、
    /// 同じ押下を二重に切り替えないよう、直前の切り替えから間もないものは無視する。
    /// </summary>
    private void TogglePreviewPlaybackFromSpace()
    {
        var now = Environment.TickCount64;
        if (now - _lastSpaceToggleTicks < 100) return;
        _lastSpaceToggleTicks = now;
        TogglePreviewPlayback();
    }

    /// <summary>
    /// 矢印キーはダイアログキーとして扱われ、通常のキーイベントより先にフォーカス移動へ
    /// 使われてしまうため、ProcessCmdKey で先に受けてプレビュー操作に使う。
    /// プレビューがキー操作の対象の間だけ有効で、文字入力中や一覧・バーの操作中は通常の動きに任せる。
    /// mpv の子ウィンドウにフォーカスがある間はキー自体が届かないため、ポーリング側が担う。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // タブ一覧にフォーカスがあるとき (←→ でタブを移動できる状態) は、
        // ↑ を最初のタブ、↓ を最後のタブへの移動にする。
        // プレビュー操作中は ↑↓ が音量なので除く (プレビューの子ウィンドウをクリックしても
        // フォーム側のフォーカスはタブ一覧に残ることがあるため、こちらでも判定する)。
        if (!_previewKeyboardActive
            && ActiveControl == _tabs
            && keyData is Keys.Up or Keys.Down)
        {
            _tabs.SelectedIndex = keyData == Keys.Up ? 0 : _tabs.TabCount - 1;
            return true;
        }
        if (_previewKeyboardActive
            && keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down
            && (_tabs.SelectedTab == _cutTab || _tabs.SelectedTab == _chapterTab)
            && !IsTextInputFocused()
            && !IsPreviewKeyExcludedControlFocused())
        {
            switch (keyData)
            {
                case Keys.Left: MovePreviewFrame(-1); return true;
                case Keys.Right: MovePreviewFrame(1); return true;
                case Keys.Up: AdjustPreviewVolume(1); return true;
                case Keys.Down: AdjustPreviewVolume(-1); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>一覧やバーなど、矢印キーを自身の操作に使うコントロールにフォーカスがあるか。</summary>
    private bool IsPreviewKeyExcludedControlFocused()
    {
        var handle = GetFocus();
        if (handle == IntPtr.Zero) return false;
        for (var control = Control.FromHandle(handle); control != null; control = control.Parent)
        {
            if (control is ListBox || control is SeekTrackBar) return true;
        }
        return false;
    }

    private void HandlePreviewSpaceKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Space) return;
        _spaceEventWasDown = false;
        if (e.Modifiers != Keys.None) return;
        if (!IsSpaceForPreview()) return;
        e.SuppressKeyPress = true;
        e.Handled = true;
    }

    /// <summary>
    /// スペースキーの押下をポーリングでも見る。プレビュー画面をクリックした後は
    /// mpv の子ウィンドウ (別プロセス) にフォーカスがあり、キー入力が自ウィンドウに届かないため。
    /// 自ウィンドウにフォーカスがある場合はキーイベント側と二重にならないよう、
    /// 切り替えの間隔で抑える。
    /// </summary>
    private void UpdatePreviewSpaceKey()
    {
        var down = (GetAsyncKeyState(0x20) & 0x8000) != 0;
        var pressed = down && !_spaceKeyWasDown;
        _spaceKeyWasDown = down;
        if (!pressed) return;
        if (Form.ActiveForm != this) return;
        if (!IsSpaceForPreview()) return;
        var panel = _mpvHostPanel;
        if (panel.IsDisposed || !panel.Visible) return;
        if (panel.Parent != _tabs.SelectedTab) return;
        TogglePreviewPlaybackFromSpace();
    }

    /// <summary>
    /// プレビュー画面をクリックした後の ← / → (1 フレーム移動) と ↑ / ↓ (音量) をポーリングで拾う。
    /// キー入力は mpv の子ウィンドウに届いて自ウィンドウに来ないため、スペースキーと同じ方式にする。
    /// 自ウィンドウのコントロールにフォーカスがある間は、そのコントロールのキー操作に任せる。
    /// </summary>
    private void UpdatePreviewArrowKeys()
    {
        if (!_previewKeyboardActive) return;
        if (Form.ActiveForm != this) return;
        if (GetFocus() != IntPtr.Zero) return;
        var panel = _mpvHostPanel;
        if (panel.IsDisposed || !panel.Visible) return;
        if (panel.Parent != _tabs.SelectedTab) return;
        if (_tabs.SelectedTab != _cutTab && _tabs.SelectedTab != _chapterTab) return;
        var now = Environment.TickCount64;
        for (var i = 0; i < PreviewArrowVirtualKeys.Length; i++)
        {
            var down = (GetAsyncKeyState(PreviewArrowVirtualKeys[i]) & 0x8000) != 0;
            if (down && (!_previewArrowWasDown[i] || now >= _previewArrowRepeatTicks[i]))
            {
                // 押し始めは間を空け、押し続けている間は一定間隔で繰り返す。
                _previewArrowRepeatTicks[i] = now + (_previewArrowWasDown[i] ? 80 : 400);
                switch (i)
                {
                    case 0: MovePreviewFrame(-1); break;
                    case 1: MovePreviewFrame(1); break;
                    case 2: AdjustPreviewVolume(1); break;
                    case 3: AdjustPreviewVolume(-1); break;
                }
            }
            _previewArrowWasDown[i] = down;
        }
    }

    /// <summary>音量をキー操作で増減する。音量バーを経由して、表示とプレーヤーへ反映する。</summary>
    private void AdjustPreviewVolume(int delta)
    {
        if (_volumeTrackBar == null) return;
        _volumeTrackBar.Value = Math.Clamp(_volumeSliderValue + delta, 0, 100);
        SetPreviewVolumeFromTrackBar(_volumeTrackBar);
    }

    private void UpdatePreviewPointerDrag()
    {
        UpdatePreviewSpaceKey();
        UpdatePreviewArrowKeys();
        UpdatePreviewRightClick();
        var buttons = Control.MouseButtons;
        var pos = Control.MousePosition;
        var leftDown = (buttons & MouseButtons.Left) != 0;
        if (leftDown && !_previewButtonWasDown)
        {
            // 押した瞬間の状況で、この押下が移動の対象かどうかを決める (ポップアップ側の WindowDragTracker と同じ判定)。
            // エクスプローラーからのファイルのドラッグのように、押下がこのウィンドウの外で
            // 始まったものは対象にしない。
            var pressInsideForm = RectangleToScreen(ClientRectangle).Contains(pos);
            _previewPressMayDrag = pressInsideForm && (Form.ActiveForm != this || TestPreviewPointerTarget());
            // プレビュー画面をクリックしたら、矢印キーと音量キーの操作対象をプレビューにする。
            _previewKeyboardActive = pressInsideForm && TestPreviewPointerGeometry();
            // 入力欄にカーソルがあっても、プレビュー側へフォーカスを移す (mpv の子ウィンドウは
            // フォーカスを奪わないため、そのままではカーソルが入力欄に残ってしまう)。
            if (_previewKeyboardActive) _mpvHostPanel.Focus();
        }
        _previewButtonWasDown = leftDown;
        if (_previewPointerState == "ignore")
        {
            if (buttons == MouseButtons.None) _previewPointerState = "idle";
            return;
        }
        if ((buttons & MouseButtons.Left) == 0)
        {
            _previewPointerState = "idle";
            return;
        }
        if (_previewPointerState == "idle")
        {
            if (!_previewPressMayDrag) return;
            if (!TestPreviewPointerTarget()) return;
            var now = Environment.TickCount64;
            var clickSize = SystemInformation.DoubleClickSize;
            if (now - _previewLastClickTicks <= SystemInformation.DoubleClickTime
                && Math.Abs(pos.X - _previewLastClickPos.X) <= clickSize.Width
                && Math.Abs(pos.Y - _previewLastClickPos.Y) <= clickSize.Height)
            {
                TogglePreviewZoom();
                _previewPointerState = "ignore";
                _previewLastClickTicks = 0;
                return;
            }
            _previewPointerState = "pressed";
            _previewPointerDownPos = pos;
            _previewFormStartPos = Location;
            _previewLastClickTicks = now;
            _previewLastClickPos = pos;
            return;
        }
        if (_previewPointerState == "pressed")
        {
            var dragSize = SystemInformation.DragSize;
            if (Math.Abs(pos.X - _previewPointerDownPos.X) > dragSize.Width
                || Math.Abs(pos.Y - _previewPointerDownPos.Y) > dragSize.Height)
            {
                _previewPointerState = "dragging";
            }
        }
        if (_previewPointerState == "dragging" && WindowState == FormWindowState.Normal)
        {
            Location = new Point(
                _previewFormStartPos.X + (pos.X - _previewPointerDownPos.X),
                _previewFormStartPos.Y + (pos.Y - _previewPointerDownPos.Y));
        }
    }

    /// <summary>
    /// プレビュー画面の右クリックをポーリングで拾い、メニューを表示する。
    /// 映像は mpv の子ウィンドウ (別プロセス) のため、通常のマウスイベントでは受け取れない。
    /// </summary>
    private void UpdatePreviewRightClick()
    {
        var rightDown = (Control.MouseButtons & MouseButtons.Right) != 0;
        var pressed = rightDown && !_previewRightButtonWasDown;
        _previewRightButtonWasDown = rightDown;
        if (!pressed) return;
        if (Form.ActiveForm != this) return;
        if (!TestPreviewPointerGeometry()) return;
        ShowPreviewContextMenu();
    }

    /// <summary>
    /// プレビュー画面の右クリックメニュー。項目はシーク補正の有効 / 無効の切り替えだけ。
    /// </summary>
    private void ShowPreviewContextMenu()
    {
        if (_previewContextMenu == null)
        {
            _previewContextMenu = new ContextMenuStrip
            {
                Renderer = new AsterCutMenuRenderer(),
            };
            _previewSeekCorrectionMenuItem = new ToolStripMenuItem("プレビューのシーク位置を厳密に合わせる") { ForeColor = Theme.MenuFore };
            _previewSeekCorrectionMenuItem.Click += (_, _) =>
            {
                _settings.PreviewSeekCorrectionEnabled = !_settings.PreviewSeekCorrectionEnabled;
                SaveSettingsFromUi();
            };
            _previewContextMenu.Items.Add(_previewSeekCorrectionMenuItem);
            _previewContextMenu.Opening += (_, _) =>
            {
                if (_previewSeekCorrectionMenuItem != null)
                {
                    _previewSeekCorrectionMenuItem.Checked = _settings.PreviewSeekCorrectionEnabled;
                }
            };
        }
        _previewContextMenu.Show(Control.MousePosition);
    }

    /// <summary>
    /// ポップアップの映像部分のドラッグで、そのウィンドウを移動するための状態管理。
    /// 映像は mpv の子ウィンドウ (別プロセス) のため、マウス入力はポーリングで拾う。
    /// </summary>
    private sealed class WindowDragTracker
    {
        private string _state = "idle";
        private Point _downPos;
        private Point _formStartPos;
        private bool _buttonWasDown;
        private bool _pressMayDrag;

        public void Update(Form form, Func<bool> isOverVideo)
        {
            var buttons = Control.MouseButtons;
            var pos = Control.MousePosition;
            var down = (buttons & MouseButtons.Left) != 0;
            if (down && !_buttonWasDown)
            {
                // 押した瞬間の状況で、この押下が移動の対象かどうかを決める。
                // シークバーなど他のコントロールを押したまま映像へ入ってきた場合は、対象にしない。
                // ウィンドウがまだアクティブでない状態からの押下は、対象としてよい (クリックでアクティブになる)。
                // エクスプローラーからのドラッグのように、押下がこのウィンドウの外で始まったものは対象にしない。
                var pressInsideForm = form.RectangleToScreen(form.ClientRectangle).Contains(pos);
                _pressMayDrag = pressInsideForm && (Form.ActiveForm != form || isOverVideo());
                _downPos = pos;
                _formStartPos = form.Location;
            }
            _buttonWasDown = down;
            if (_state == "idle")
            {
                if (!down || !_pressMayDrag) return;
                if (Form.ActiveForm != form) return;
                if (!isOverVideo()) return;
                _state = "pressed";
                return;
            }
            if (!down)
            {
                _state = "idle";
                return;
            }
            if (_state == "pressed")
            {
                var dragSize = SystemInformation.DragSize;
                if (Math.Abs(pos.X - _downPos.X) > dragSize.Width
                    || Math.Abs(pos.Y - _downPos.Y) > dragSize.Height)
                {
                    _state = "dragging";
                }
            }
            if (_state == "dragging" && form.WindowState == FormWindowState.Normal)
            {
                form.Location = new Point(
                    _formStartPos.X + (pos.X - _downPos.X),
                    _formStartPos.Y + (pos.Y - _downPos.Y));
            }
        }
    }

    private readonly WindowDragTracker _previewPopupDrag = new();

    private void UpdatePopupPointerDrag()
    {
        if (_previewPopupForm is not { IsDisposed: false } popup) return;
        if (_mpvHostPanel.IsDisposed) return;
        _previewPopupDrag.Update(popup, () =>
            _mpvHostPanel.Visible
            && _mpvHostPanel.Parent == popup
            && _mpvHostPanel.RectangleToScreen(_mpvHostPanel.ClientRectangle).Contains(Control.MousePosition));
    }

    /// <summary>
    /// ウィンドウの大きさを、描画を止めたまま変える。サイズ変更の途中で、
    /// 描き直される前の白い枠が一瞬見えてしまうのを防ぐため。
    /// </summary>
    private void SetFormSizeWithoutFlash(Size size)
    {
        LockWindowUpdate(Handle);
        try
        {
            Size = size;
        }
        finally
        {
            LockWindowUpdate(IntPtr.Zero);
            Invalidate(true);
        }
    }

    private void TogglePreviewZoom()
    {
        if (_previewZoomRestoreSize is { } restoreSize)
        {
            SetFormSizeWithoutFlash(restoreSize);
            _previewZoomRestoreSize = null;
            return;
        }
        int newFormW;
        int newFormH;
        try
        {
            var dimensions = GetVideoDimensions();
            var videoW = dimensions.Width;
            var videoH = dimensions.Height;
            var rotation = GetPreviewRotationClockwise();
            if (rotation is 90 or 270) (videoW, videoH) = (videoH, videoW);
            var panel = _mpvHostPanel;
            var fitScale = Math.Min((double)panel.Width / videoW, (double)panel.Height / videoH);
            var targetPanelW = (int)Math.Round(videoW * fitScale * 1.25);
            var targetPanelH = (int)Math.Round(videoH * fitScale * 1.25);
            // プレビュー列のレイアウト規則 (横の増分の半分がパネル、縦の増分はすべてパネル) から逆算する。
            newFormW = MinimumSize.Width + 2 * (targetPanelW - 508);
            newFormH = MinimumSize.Height + (targetPanelH - 282);
        }
        catch
        {
            // 映像の大きさが分からない場合は、ウィンドウ自体を一律に拡大する。
            newFormW = (int)Math.Round(Width * 1.25);
            newFormH = (int)Math.Round(Height * 1.25);
        }
        var workingArea = Screen.FromControl(this).WorkingArea;
        newFormW = Math.Max(MinimumSize.Width, Math.Min(newFormW, workingArea.Width));
        newFormH = Math.Max(MinimumSize.Height, Math.Min(newFormH, workingArea.Height));
        _previewZoomRestoreSize = Size;
        SetFormSizeWithoutFlash(new Size(newFormW, newFormH));
    }
}
