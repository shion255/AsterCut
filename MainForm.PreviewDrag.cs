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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool LockWindowUpdate(IntPtr hWndLock);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, PreviewWheelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr PreviewWheelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewWheelHookInfo
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private System.Windows.Forms.Timer _previewDragTimer = null!;
    private string _previewPointerState = "idle";
    private bool _spaceKeyWasDown;
    private bool _spaceEventWasDown;
    private long _lastSpaceToggleTicks;
    private bool _previewKeyboardActive;
    private readonly bool[] _previewArrowWasDown = new bool[4];
    private bool _previewHomeWasDown;
    private bool _previewEndWasDown;
    private readonly long[] _previewArrowRepeatTicks = new long[4];
    private static readonly int[] PreviewArrowVirtualKeys = { 0x25, 0x27, 0x26, 0x28 };
    private Point _previewPointerDownPos;
    private Point _previewFormStartPos;
    private bool _previewButtonWasDown;
    private bool _previewPressMayDrag;
    private long _previewLastClickTicks;
    private Point _previewLastClickPos;
    private Size? _previewZoomRestoreSize;
    private IntPtr _previewWheelHook;
    private PreviewWheelHookProc? _previewWheelHookProc;
    private int _previewWheelPendingDelta;
    private int _previewWheelRemainder;
    private int _segmentPopupWheelPendingDelta;
    private int _segmentPopupWheelRemainder;

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
    /// ポインターの下にある一番手前のウィンドウがプレビューの映像 (mpv の子ウィンドウ) かどうか。
    /// 実行ログのポップアップなど、プレビューの上に重なった別ウィンドウへの押下を
    /// プレビューへの押下と取り違えないための判定 (範囲の重なりだけでは区別できない)。
    /// </summary>
    private bool IsPointerOverPreviewWindow(Point pos)
    {
        if (_mpvHostPanel.IsDisposed || !_mpvHostPanel.IsHandleCreated) return false;
        for (var handle = WindowFromPoint(pos); handle != IntPtr.Zero; handle = GetParent(handle))
        {
            if (handle == _mpvHostPanel.Handle) return true;
        }
        return false;
    }

    // 映像の上のマウスホイールは mpv の子ウィンドウが受けてしまうため、低レベルフックで拾い、
    // 実際の移動は 20ms のポーリング側 (UpdatePreviewWheel) で行う。
    private const int WhMouseLl = 14;
    private const int WmMouseWheelMessage = 0x020A;

    private void InstallPreviewWheelHook()
    {
        _previewWheelHookProc = PreviewWheelHookCallback;
        _previewWheelHook = SetWindowsHookEx(WhMouseLl, _previewWheelHookProc, GetModuleHandle(null), 0);
    }

    private void RemovePreviewWheelHook()
    {
        if (_previewWheelHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_previewWheelHook);
            _previewWheelHook = IntPtr.Zero;
        }
        _previewWheelHookProc = null;
    }

    private IntPtr PreviewWheelHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmMouseWheelMessage)
        {
            var info = Marshal.PtrToStructure<PreviewWheelHookInfo>(lParam);
            var delta = (short)(info.MouseData >> 16);
            if (delta != 0)
            {
                if (IsSegmentPopupWheelTarget(info.Pt))
                {
                    _segmentPopupWheelPendingDelta += delta;
                    return (IntPtr)1;
                }
                if (_previewReady && IsPreviewWheelTarget(info.Pt))
                {
                    _previewWheelPendingDelta += delta;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_previewWheelHook, nCode, wParam, lParam);
    }

    private bool IsSegmentPopupWheelTarget(Point screenPoint)
    {
        // 区間ポップアップが前面で、ポインターがその映像 (専用 mpv の子ウィンドウ) の上にあるときだけ対象にする。
        if (_segmentPopupForm is not { IsDisposed: false } popup || !popup.Visible) return false;
        if (_segmentPopupVideoPanel is not { IsDisposed: false } panel || !panel.Visible || !panel.IsHandleCreated) return false;
        if (GetForegroundWindow() != popup.Handle) return false;
        for (var handle = WindowFromPoint(screenPoint); handle != IntPtr.Zero; handle = GetParent(handle))
        {
            if (handle == panel.Handle) return true;
        }
        return false;
    }

    private void UpdateSegmentPopupWheel()
    {
        var pending = _segmentPopupWheelPendingDelta;
        if (pending == 0) return;
        _segmentPopupWheelPendingDelta = 0;
        if (_segmentPopupForm is not { IsDisposed: false }) return;
        _segmentPopupWheelRemainder += pending;
        var notches = 0;
        while (_segmentPopupWheelRemainder >= 120) { _segmentPopupWheelRemainder -= 120; notches--; }
        while (_segmentPopupWheelRemainder <= -120) { _segmentPopupWheelRemainder += 120; notches++; }
        if (notches == 0) return;
        if (_segmentPopupPlaying)
        {
            // 再生中はホイール1ノッチで5秒移動する (上が戻る、下が進む)。
            SeekSegmentPopupToMs(GetSegmentPopupPositionMs() + 5000L * notches);
        }
        else
        {
            // 一時停止中はホイール1ノッチで5フレーム移動する (上が戻る、下が進む)。
            StepSegmentPopupFrame(notches > 0 ? 1 : -1, 5 * Math.Abs(notches));
        }
    }

    private bool IsPreviewWheelTarget(Point screenPoint)
    {
        // 映像を表示している画面 (メイン画面 / ポップアップ / クリッピング画面) が前面で、
        // ポインターが映像の範囲内にあるときだけ対象にする。
        if (_mpvHostPanel.IsDisposed || !_mpvHostPanel.IsHandleCreated || !_mpvHostPanel.Visible) return false;
        if (_mpvHostPanel.TopLevelControl is not Form host || !host.Visible) return false;
        if (GetForegroundWindow() != host.Handle) return false;
        if (!IsPointerOverPreviewWindow(screenPoint)) return false;
        var rect = _mpvHostPanel.RectangleToScreen(_mpvHostPanel.ClientRectangle);
        return rect.Contains(screenPoint);
    }

    private void UpdatePreviewWheel()
    {
        var pending = _previewWheelPendingDelta;
        if (pending == 0) return;
        _previewWheelPendingDelta = 0;
        if (!_previewReady) return;
        _previewWheelRemainder += pending;
        var notches = 0;
        while (_previewWheelRemainder >= 120) { _previewWheelRemainder -= 120; notches--; }
        while (_previewWheelRemainder <= -120) { _previewWheelRemainder += 120; notches++; }
        if (notches == 0) return;
        if (_previewIsPlaying)
        {
            // 再生中はホイール1ノッチで5秒移動する (上が戻る、下が進む)。
            UpdatePlayerPositionNow();
            var target = GetPlayerPositionTimeSpan() + TimeSpan.FromSeconds(5.0 * notches);
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            SetPlayerPosition(target);
        }
        else
        {
            // 一時停止中はホイール1ノッチで1フレーム移動する (上が戻る、下が進む)。
            MovePreviewFrame(notches);
        }
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
        // タブ一覧がアクティブなとき (見出しをクリックした直後など) は、
        // ↑ を最初のタブ、↓ を最後のタブへの移動にする。
        // プレビュー操作中は ↑↓ が音量なので除く。
        if (!_previewKeyboardActive
            && _tabStripKeyActive
            && keyData is Keys.Up or Keys.Down)
        {
            _tabs.SelectedIndex = keyData == Keys.Up ? 0 : _tabs.TabCount - 1;
            return true;
        }
        if (_previewKeyboardActive
            && keyData is Keys.Home or Keys.End
            && (_tabs.SelectedTab == _cutTab || _tabs.SelectedTab == _chapterTab)
            && !IsTextInputFocused()
            && !IsPreviewKeyExcludedControlFocused())
        {
            if (keyData == Keys.Home) MovePreviewToStartKeepingPlayback();
            else SetPreviewEnded();
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
                case Keys.Up: MovePreviewFrame(-5); return true;
                case Keys.Down: MovePreviewFrame(5); return true;
            }
        }
        // タブ一覧にフォーカスが残っているのにアクティブでないときは、←→ が
        // タブコントロール本来のタブ移動に使われてしまうため、ここで飲み込む。
        if (!_tabStripKeyActive
            && keyData is Keys.Left or Keys.Right
            && IsTabStripNativeFocused())
        {
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// 実際のフォーカス先 (GetFocus) がタブ一覧そのものか。←→ の飲み込み判定専用で、
    /// アクティブかどうかの判定には使わない (クリックで記録したフラグで決める)。
    /// </summary>
    private bool IsTabStripNativeFocused()
    {
        var handle = GetFocus();
        return handle != IntPtr.Zero && Control.FromHandle(handle) == _tabs;
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

    private bool _popupSpaceKeyWasDown;
    private bool _clipSpaceKeyWasDown;

    /// <summary>
    /// ポップアップ (動画プレビュー / 区間プレビュー / 回転プレビュー) でのスペースキーによる
    /// 再生 / 一時停止をポーリングで拾う。ポップアップの映像をクリックした後は mpv の子ウィンドウに
    /// フォーカスがあり、フォームのキーイベントが届かないため、本体と同じ方式にする。
    /// フォームにフォーカスがある場合はキーイベント側と二重にならないよう、切り替え側の間隔ガードで抑える。
    /// </summary>
    private void UpdatePopupSpaceKey()
    {
        var down = (GetAsyncKeyState(0x20) & 0x8000) != 0;
        var pressed = down && !_popupSpaceKeyWasDown;
        _popupSpaceKeyWasDown = down;
        if (!pressed) return;
        if (_previewPopupForm is { IsDisposed: false } && Form.ActiveForm == _previewPopupForm)
        {
            TogglePreviewPlaybackFromSpace();
        }
        else if (_segmentPopupForm is { IsDisposed: false } && Form.ActiveForm == _segmentPopupForm)
        {
            ToggleSegmentPopupPlaybackFromSpace();
        }
    }

    /// <summary>
    /// クリッピング画面でのスペースキーによる再生 / 一時停止をポーリングで拾う。
    /// クリッピングした範囲の外 (黒帯) をクリックした後は、映像の上に重ねた透過フォームや
    /// mpv の子ウィンドウにフォーカスがあり、フォームのキーイベントが届かないため、
    /// ポップアップと同じ方式にする。
    /// </summary>
    private void UpdateClipSpaceKey()
    {
        var down = (GetAsyncKeyState(0x20) & 0x8000) != 0;
        var pressed = down && !_clipSpaceKeyWasDown;
        _clipSpaceKeyWasDown = down;
        if (!pressed) return;
        if (_clipForm is not { IsDisposed: false }) return;
        var active = Form.ActiveForm;
        if (active != _clipForm && active != _clipOverlay) return;
        TogglePreviewPlaybackFromSpace();
    }

    /// <summary>
    /// プレビュー画面をクリックした後の ← / → (1 フレーム移動) と ↑ / ↓ (5 フレーム移動) をポーリングで拾う。
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
                    case 2: MovePreviewFrame(-5); break;
                    case 3: MovePreviewFrame(5); break;
                }
            }
            _previewArrowWasDown[i] = down;
        }
        // Home / End は押し続けても繰り返さない。
        var homeDown = (GetAsyncKeyState(0x24) & 0x8000) != 0;
        if (homeDown && !_previewHomeWasDown) MovePreviewToStartKeepingPlayback();
        _previewHomeWasDown = homeDown;
        var endDown = (GetAsyncKeyState(0x23) & 0x8000) != 0;
        if (endDown && !_previewEndWasDown) SetPreviewEnded();
        _previewEndWasDown = endDown;
    }

    // ------------------------------------------------------------------
    // プレビュー画面へのドラッグ & ドロップ
    // 映像部分は mpv の子ウィンドウ (別プロセス) で覆われていて、こちらの
    // ドロップ先を登録しても実機では効かないため、プレビューに重ねた透明な
    // 受け付け用の画面で受ける。普段は WS_EX_TRANSPARENT でマウスを素通しし、
    // アプリの外で始まったドラッグがプレビューに近づいたときだけ受け付ける。
    // ------------------------------------------------------------------

    private PreviewDropOverlayForm? _previewDropOverlay;
    private bool _previewDropOverlayActive;
    private bool _dropDragFromOutside;

    private sealed class PreviewDropOverlayForm : Form
    {
        public PreviewDropOverlayForm(MainForm owner)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.02;
            AllowDrop = true;
            DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) owner.OpenDroppedInputFile(files[0]);
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT: 普段はマウスを素通しする
                return cp;
            }
        }
    }

    private bool IsPointOverAppWindow(Point pos)
    {
        var hwnd = WindowFromPoint(pos);
        if (hwnd == IntPtr.Zero) return false;
        var root = hwnd;
        for (;;)
        {
            var parent = GetParent(root);
            if (parent == IntPtr.Zero) break;
            root = parent;
        }
        if (root == Handle) return true;
        if (_previewPopupForm != null && root == _previewPopupForm.Handle) return true;
        if (_segmentPopupForm != null && root == _segmentPopupForm.Handle) return true;
        if (_clipForm != null && root == _clipForm.Handle) return true;
        return false;
    }

    private void UpdatePreviewDropOverlay()
    {
        // パネルが本体フォーム上にある間だけ、パネルの画面上の矩形に重ねる。
        var show = _mpvHostPanel.IsHandleCreated && _mpvHostPanel.Visible
            && _mpvHostPanel.TopLevelControl == this && WindowState != FormWindowState.Minimized;
        if (!show)
        {
            if (_previewDropOverlay is { Visible: true }) _previewDropOverlay.Hide();
            return;
        }
        _previewDropOverlay ??= new PreviewDropOverlayForm(this);
        var rect = _mpvHostPanel.RectangleToScreen(_mpvHostPanel.ClientRectangle);
        if (!_previewDropOverlay.Visible) _previewDropOverlay.Show(this);
        if (_previewDropOverlay.Bounds != rect) _previewDropOverlay.Bounds = rect;
    }

    private void SetPreviewDropOverlayActive(bool active)
    {
        if (_previewDropOverlay == null || !_previewDropOverlay.IsHandleCreated) return;
        const int gwlExStyle = -20;
        const int wsExTransparent = 0x20;
        var style = GetWindowLong(_previewDropOverlay.Handle, gwlExStyle);
        var next = active ? style & ~wsExTransparent : style | wsExTransparent;
        if (next == style) { _previewDropOverlayActive = active; return; }
        SetWindowLong(_previewDropOverlay.Handle, gwlExStyle, next);
        // スタイル変更をヒットテストへ反映させる。
        SetWindowPos(_previewDropOverlay.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        _previewDropOverlayActive = active;
    }

    private void UpdatePreviewPointerDrag()
    {
        UpdatePreviewDropOverlay();
        UpdatePreviewSpaceKey();
        UpdatePopupSpaceKey();
        UpdateClipSpaceKey();
        UpdatePreviewArrowKeys();
        var buttons = Control.MouseButtons;
        var pos = Control.MousePosition;
        var leftDown = (buttons & MouseButtons.Left) != 0;
        if (leftDown && !_previewButtonWasDown)
        {
            // 押した瞬間の状況で、この押下が移動の対象かどうかを決める (ポップアップ側の WindowDragTracker と同じ判定)。
            // エクスプローラーからのファイルのドラッグのように、押下がこのウィンドウの外で
            // 始まったものは対象にしない。
            var pressInsideForm = RectangleToScreen(ClientRectangle).Contains(pos);
            _previewPressMayDrag = pressInsideForm && IsPointerOverPreviewWindow(pos);
            // プレビュー画面をクリックしたら、矢印キーと音量キーの操作対象をプレビューにする。
            _previewKeyboardActive = pressInsideForm && TestPreviewPointerGeometry() && IsPointerOverPreviewWindow(pos);
            // 入力欄にカーソルがあっても、プレビュー側へフォーカスを移す (mpv の子ウィンドウは
            // フォーカスを奪わないため、そのままではカーソルが入力欄に残ってしまう)。
            if (_previewKeyboardActive) _mpvHostPanel.Focus();
            // 押下がアプリの外で始まった場合は、ファイルのドラッグの可能性がある。
            _dropDragFromOutside = !IsPointOverAppWindow(pos);
        }
        _previewButtonWasDown = leftDown;
        // 外部からのドラッグがプレビューの上に来たら、受け付け用の画面を有効にする。
        if (_dropDragFromOutside && leftDown && _previewDropOverlay is { Visible: true } && _previewDropOverlay.Bounds.Contains(pos))
        {
            if (!_previewDropOverlayActive) SetPreviewDropOverlayActive(true);
        }
        if (!leftDown)
        {
            if (_previewDropOverlayActive) SetPreviewDropOverlayActive(false);
            _dropDragFromOutside = false;
        }
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
            && _mpvHostPanel.RectangleToScreen(_mpvHostPanel.ClientRectangle).Contains(Control.MousePosition)
            && IsPointerOverPreviewWindow(Control.MousePosition));
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
            // アナモフィック素材では表示時の幅へ換算する (記録サイズのままで計算すると、
            // 表示の縦横比とずれて、拡大しても黒帯が増えるだけになる)。
            if (_videoStreams.Count > 0)
            {
                var sarNum = _videoStreams[0].SampleAspectNum;
                var sarDen = _videoStreams[0].SampleAspectDen;
                if (sarNum > 0 && sarDen > 0)
                {
                    videoW = (int)Math.Round(videoW * (double)sarNum / sarDen);
                }
            }
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
