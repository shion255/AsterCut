using System;
using System.Drawing;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// プレビューポップアップ (通常と回転)。
/// mpv の映像パネルごとポップアップへ移し、閉じるときにタブへ戻す。
/// </summary>
internal sealed partial class MainForm
{
    private Form? _previewPopupForm;
    private string _previewPopupKind = "";
    private Panel? _previewPopupPanel;
    private SeekTrackBar? _popupSeekTrackBar;
    private SeekTrackBar? _popupVolumeTrackBar;
    private Label? _popupPositionLabel;
    private Label? _popupVolumeValueLabel;
    private VolumeIconButton? _popupMuteButton;
    private Button? _popupPlayButton;
    private ToolStripMenuItem? _popupToggleControlsMenuItem;

    private void LayoutPreviewPopupControls()
    {
        if (_previewPopupPanel == null || _previewPopupPanel.IsDisposed) return;
        var width = _previewPopupPanel.ClientSize.Width;
        if (width < 280) return;

        if (_popupPositionLabel != null) _popupPositionLabel.Width = Math.Max(180, width - 24);
        if (_popupSeekTrackBar != null)
        {
            _popupSeekTrackBar.Left = 8;
            _popupSeekTrackBar.Top = 26;
            _popupSeekTrackBar.Width = Math.Max(180, width - 16);
        }
        // 再生操作は左下にまとめ、先頭・1 フレーム戻る・再生・1 フレーム進む・末尾の順に配置する。
        if (_popupSeekStartButton != null) { _popupSeekStartButton.Top = 70; _popupSeekStartButton.Width = 34; _popupSeekStartButton.Height = 30; _popupSeekStartButton.Left = 8; }
        if (_popupFrameBackButton != null) { _popupFrameBackButton.Top = 70; _popupFrameBackButton.Width = 34; _popupFrameBackButton.Height = 30; _popupFrameBackButton.Left = 46; }
        if (_popupPlayButton != null)
        {
            _popupPlayButton.Left = 84;
            _popupPlayButton.Top = 70;
            _popupPlayButton.Width = 92;
        }
        if (_popupFrameForwardButton != null) { _popupFrameForwardButton.Top = 70; _popupFrameForwardButton.Width = 34; _popupFrameForwardButton.Height = 30; _popupFrameForwardButton.Left = 182; }
        if (_popupSeekEndButton != null) { _popupSeekEndButton.Top = 70; _popupSeekEndButton.Width = 34; _popupSeekEndButton.Height = 30; _popupSeekEndButton.Left = 220; }
        // 音量コントロールは右下に揃える。
        const int rightPadding = 10;
        const int valueWidth = 48;
        var sliderRight = width - rightPadding - valueWidth - 6;
        const int minimumButtonStart = 264;
        var sliderWidth = Math.Max(110, Math.Min(320, sliderRight - 40 - minimumButtonStart));
        var sliderLeft = sliderRight - sliderWidth;
        if (_popupVolumeTrackBar != null)
        {
            _popupVolumeTrackBar.Left = sliderLeft;
            _popupVolumeTrackBar.Top = 66;
            _popupVolumeTrackBar.Width = sliderWidth;
        }
        if (_popupMuteButton != null)
        {
            _popupMuteButton.Left = sliderLeft - 40;
            _popupMuteButton.Top = 70;
        }
        if (_popupVolumeValueLabel != null)
        {
            _popupVolumeValueLabel.Left = width - rightPadding - valueWidth;
            _popupVolumeValueLabel.Top = 76;
            _popupVolumeValueLabel.Width = valueWidth;
        }
    }

    private Button? _popupSeekStartButton;
    private Button? _popupFrameBackButton;
    private Button? _popupFrameForwardButton;
    private Button? _popupSeekEndButton;

    private void TogglePreviewPopupControls()
    {
        // コントロールの表示を切り替える。隠すと映像がポップアップいっぱいに広がる。
        if (_previewPopupPanel == null || _previewPopupPanel.IsDisposed) return;
        _previewPopupPanel.Visible = !_previewPopupPanel.Visible;
        FitMpvWindow();
    }

    private void ShowPreviewPopup()
    {
        CloseClippingForm();
        if (!_previewReady || !TestPlayerAvailable())
        {
            MessageBox.Show("先に動画を読み込み、プレビューの準備が完了するまで待ってください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_previewPopupForm is { IsDisposed: false })
        {
            if (_previewPopupKind == "rotation")
            {
                // 回転プレビューを閉じてから、通常のプレビューを開く。
                try { _previewPopupForm.Close(); } catch { }
            }
            else
            {
                _previewPopupForm.Activate();
                return;
            }
        }

        // ポップアップはプレビュー画面の右下に右下を合わせて開く。
        Point? popupAnchor = null;
        if (_mpvHostPanel.Parent != null)
        {
            var panelRect = _mpvHostPanel.RectangleToScreen(_mpvHostPanel.ClientRectangle);
            var workArea = Screen.FromControl(_mpvHostPanel).WorkingArea;
            var anchorX = Math.Max(workArea.Left, Math.Min(panelRect.Right - 1100, workArea.Right - 1100));
            var anchorY = Math.Max(workArea.Top, Math.Min(panelRect.Bottom - 760, workArea.Bottom - 760));
            popupAnchor = new Point(anchorX, anchorY);
        }

        var popup = new Form
        {
            Icon = AppIcon.Value,
            Text = "AsterCut - 動画プレビュー",
            Size = new Size(1100, 760),
            MinimumSize = new Size(640, 420),
            BackColor = Color.Black,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.None,
            Font = Font,
        };
        if (popupAnchor is { } anchor)
        {
            popup.StartPosition = FormStartPosition.Manual;
            popup.Location = anchor;
        }
        else
        {
            popup.StartPosition = FormStartPosition.CenterScreen;
        }

        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Size = new Size(1100, 116),
            BackColor = Theme.PanelBack,
        };

        var popupPosition = Theme.MakeLabel("再生位置: --:--:--.---", 12, 4, 700, 22);
        popupPosition.TextAlign = ContentAlignment.MiddleCenter;
        popupPosition.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var popupSeek = new SeekTrackBar
        {
            Minimum = 0, Maximum = 10000, Value = 0,
            TickFrequency = 1000, SmallChange = 10, LargeChange = 100,
            AutoSize = false, Location = new Point(8, 26), Size = new Size(300, 34),
        };
        var popupSeekStart = new SeekIconButton("First", 0, 29, 34, 30, "先頭へシーク", _seekToolTip);
        var popupFrameBack = new SeekIconButton("Previous", 0, 29, 34, 30, "1 フレーム戻る", _seekToolTip);
        var popupPlay = Theme.MakeButton("再生", 0, 29, 92, 30);
        var popupFrameForward = new SeekIconButton("Next", 0, 29, 34, 30, "1 フレーム進む", _seekToolTip);
        var popupSeekEnd = new SeekIconButton("Last", 0, 29, 34, 30, "末尾へシーク", _seekToolTip);
        var popupMuteButton = new VolumeIconButton(10, 70, 34, 30);
        var popupVolume = new SeekTrackBar
        {
            Minimum = 0, Maximum = 100, Value = Math.Clamp(_volumeSliderValue, 0, 100),
            TickFrequency = 25, SmallChange = 1, LargeChange = 5,
            AutoSize = false, Location = new Point(50, 68), Size = new Size(260, 38),
        };
        var popupVolumeValue = Theme.MakeLabel($"{popupVolume.Value}%", 316, 76, 55, 22);

        // mpv の映像ウィンドウは、埋め込み先のパネルごとポップアップへ移す。
        _mpvHostPanel.Dock = DockStyle.Fill;
        popup.Controls.Add(_mpvHostPanel);
        popup.Controls.Add(panel);
        panel.Controls.Add(popupPosition);
        panel.Controls.Add(popupSeek);
        panel.Controls.Add(popupSeekStart);
        panel.Controls.Add(popupFrameBack);
        panel.Controls.Add(popupPlay);
        panel.Controls.Add(popupFrameForward);
        panel.Controls.Add(popupSeekEnd);
        panel.Controls.Add(popupMuteButton);
        panel.Controls.Add(popupVolume);
        panel.Controls.Add(popupVolumeValue);

        _previewPopupForm = popup;
        _previewPopupKind = "";
        _previewPopupPanel = panel;
        _popupSeekTrackBar = popupSeek;
        _popupVolumeTrackBar = popupVolume;
        _popupPositionLabel = popupPosition;
        _popupVolumeValueLabel = popupVolumeValue;
        _popupMuteButton = popupMuteButton;
        _popupSeekStartButton = popupSeekStart;
        _popupPlayButton = popupPlay;
        _popupFrameBackButton = popupFrameBack;
        _popupFrameForwardButton = popupFrameForward;
        _popupSeekEndButton = popupSeekEnd;
        _popupPreviewSeeking = false;
        UpdatePreviewPlaybackButtons();
        UpdatePreviewMuteButtons();
        LayoutPreviewPopupControls();
        UpdatePreviewPositionLabel();

        // 右クリックメニュー: コントロールの表示・非表示を切り替える。
        var popupMenu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()),
        };
        var toggleControlsItem = new ToolStripMenuItem("コントロールを隠す") { ForeColor = Theme.MenuFore, ShortcutKeyDisplayString = "H" };
        popupMenu.Items.Add(toggleControlsItem);
        _popupToggleControlsMenuItem = toggleControlsItem;
        toggleControlsItem.Click += (_, _) => TogglePreviewPopupControls();
        popupMenu.Opening += (_, _) =>
        {
            if (_popupToggleControlsMenuItem != null)
            {
                _popupToggleControlsMenuItem.Text = _previewPopupPanel is { Visible: true } ? "コントロールを隠す" : "コントロールを表示";
            }
        };
        _mpvHostPanel.ContextMenuStrip = popupMenu;
        panel.ContextMenuStrip = popupMenu;
        foreach (Control child in panel.Controls) child.ContextMenuStrip = popupMenu;

        popupSeek.MouseDown += (_, _) => { if (_previewReady) { _previewSeeking = false; _popupPreviewSeeking = true; } };
        popupSeek.MouseUp += (_, _) =>
        {
            if (_popupPreviewSeeking)
            {
                _popupPreviewSeeking = false;
                SetPreviewPositionFromTrackBar(popupSeek);
            }
        };
        popupSeek.Scroll += (_, _) => SetPreviewPositionFromTrackBar(popupSeek);
        popupSeek.KeyDown += (_, e) => HandleSeekTrackBarKeyDown(e);
        popupSeek.KeyUp += (_, e) => HandleSeekTrackBarKeyUp(popupSeek, e);
        popupSeek.MouseWheel += (_, e) => HandleSeekTrackBarMouseWheel(e);
        popupVolume.Scroll += (_, _) => SetPreviewVolumeFromTrackBar(popupVolume);
        popupVolume.MouseUp += (_, _) => SaveSettingsFromUi();
        popupVolume.KeyUp += (_, _) => SaveSettingsFromUi();
        popupMuteButton.Click += (_, _) => TogglePreviewMute();
        popupSeekStart.Click += (_, _) => MovePreviewToBoundary(false);
        popupFrameBack.Click += (_, _) => MovePreviewFrame(-1);
        popupPlay.Click += (_, _) => TogglePreviewPlayback();
        popupFrameForward.Click += (_, _) => MovePreviewFrame(1);
        popupSeekEnd.Click += (_, _) => MovePreviewToBoundary(true);
        popup.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                popup.Close();
            }
            else if (e.KeyCode == Keys.H)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                TogglePreviewPopupControls();
            }
        };
        popup.Resize += (_, _) => LayoutPreviewPopupControls();
        popup.FormClosing += (_, _) =>
        {
            // 映像ウィンドウを載せたパネルを、プレビューを表示しているタブへ戻す。
            try
            {
                _mpvHostPanel.Dock = DockStyle.None;
                _mpvHostPanel.ContextMenuStrip = null;
                GetPreviewReturnTab().Controls.Add(_mpvHostPanel);
                RestoreMpvHostPanelHome();
                _mpvHostPanel.Visible = true;
                FitMpvWindow();
            }
            catch { /* 戻せなくてもポップアップは閉じる */ }
            _previewPopupForm = null;
            _previewPopupKind = "";
            _previewPopupPanel = null;
            _popupToggleControlsMenuItem = null;
            _popupSeekTrackBar = null;
            _popupVolumeTrackBar = null;
            _popupPositionLabel = null;
            _popupMuteButton = null;
            _popupVolumeValueLabel = null;
            _popupSeekStartButton = null;
            _popupPlayButton = null;
            _popupFrameBackButton = null;
            _popupFrameForwardButton = null;
            _popupSeekEndButton = null;
            _popupPreviewSeeking = false;
        };
        Theme.ApplyDarkTitleBar(popup);
        Theme.ApplyDarkTitleBar(popup);
        popup.Show(this);
        LayoutPreviewPopupControls();
        FitMpvWindow();
    }

    // ------------------------------------------------------------------
    // 回転プレビュー
    // ------------------------------------------------------------------

    private void SetPreviewRotation()
    {
        var angle = GetPreviewRotationClockwise();
        if (TestPlayerAvailable())
        {
            try { SendMpvCommand(new object[] { "set_property", "video-rotate", angle }); } catch { }
            FitMpvWindow();
        }
    }

    /// <summary>回転プレビューを閉じるときに、プレーヤーの回転を元に戻す (編集タブのプレビューへ影響させない)。</summary>
    private void ResetPreviewRotation()
    {
        if (TestPlayerAvailable())
        {
            try { SendMpvCommand(new object[] { "set_property", "video-rotate", 0 }); } catch { }
        }
    }

    /// <summary>編集タブの「回転 (プレビュー):」から開く、映像とシークバーだけのポップアップ。</summary>
    private void ShowRotationPreviewPopup()
    {
        CloseClippingForm();
        if (!_previewReady || !TestPlayerAvailable())
        {
            MessageBox.Show("先に動画を読み込み、プレビューの準備が完了するまで待ってください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_previewPopupForm is { IsDisposed: false })
        {
            if (_previewPopupKind == "rotation")
            {
                SetPreviewRotation();
                _previewPopupForm.Activate();
                return;
            }
            // 通常のプレビューポップアップを閉じてから開く。
            try { _previewPopupForm.Close(); } catch { }
        }

        var popup = new Form
        {
            Icon = AppIcon.Value,
            Text = "AsterCut - 回転プレビュー",
            MinimumSize = new Size(480, 360),
            BackColor = Color.Black,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.None,
            Font = Font,
        };
        // 回転ラベルと重ならないよう、ラベルの少し上に表示する。画面の上端に収まらない場合は高さを縮める。
        try
        {
            var labelTopLeft = _rotationLabel.PointToScreen(Point.Empty);
            var workArea = Screen.FromControl(_rotationLabel).WorkingArea;
            const int popupGap = 12;
            var popupWidth = Math.Min(900, workArea.Width);
            var popupHeight = Math.Max(360, Math.Min(640, labelTopLeft.Y - popupGap - workArea.Top));
            var popupX = Math.Max(workArea.Left, Math.Min(labelTopLeft.X, workArea.Right - popupWidth));
            var popupY = Math.Max(workArea.Top, labelTopLeft.Y - popupGap - popupHeight);
            popup.StartPosition = FormStartPosition.Manual;
            popup.Size = new Size(popupWidth, popupHeight);
            popup.Location = new Point(popupX, popupY);
        }
        catch
        {
            popup.StartPosition = FormStartPosition.CenterScreen;
            popup.Size = new Size(900, 640);
        }

        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Size = new Size(popup.ClientSize.Width, 44),
            Height = 44,
            BackColor = Theme.PanelBack,
        };
        var popupSeek = new SeekTrackBar
        {
            Minimum = 0, Maximum = 10000, Value = 0,
            TickFrequency = 1000, SmallChange = 10, LargeChange = 100,
            AutoSize = false,
            Location = new Point(8, 5),
            Size = new Size(Math.Max(180, popup.ClientSize.Width - 16), 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        // mpv の映像ウィンドウは、埋め込み先のパネルごとポップアップへ移す。
        _mpvHostPanel.Dock = DockStyle.Fill;
        popup.Controls.Add(_mpvHostPanel);
        popup.Controls.Add(panel);
        panel.Controls.Add(popupSeek);

        _previewPopupForm = popup;
        _previewPopupKind = "rotation";
        _previewPopupPanel = null; // 通常ポップアップ用の配置処理 (LayoutPreviewPopupControls) の対象外にする
        _popupSeekTrackBar = popupSeek;
        _popupPreviewSeeking = false;

        popupSeek.MouseDown += (_, _) => { if (_previewReady) { _previewSeeking = false; _popupPreviewSeeking = true; } };
        popupSeek.MouseUp += (_, _) =>
        {
            if (_popupPreviewSeeking)
            {
                _popupPreviewSeeking = false;
                SetPreviewPositionFromTrackBar(popupSeek);
            }
        };
        popupSeek.Scroll += (_, _) => SetPreviewPositionFromTrackBar(popupSeek);
        popupSeek.KeyDown += (_, e) => HandleSeekTrackBarKeyDown(e);
        popupSeek.KeyUp += (_, e) => HandleSeekTrackBarKeyUp(popupSeek, e);
        popupSeek.MouseWheel += (_, e) => HandleSeekTrackBarMouseWheel(e);
        // 再生ボタンは置かないので、空白キーで再生・一時停止する。Esc キーで閉じる。
        popup.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                TogglePreviewPlayback();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                popup.Close();
            }
        };
        popup.FormClosing += (_, _) =>
        {
            ResetPreviewRotation();
            // 映像ウィンドウを載せたパネルを、プレビューを表示しているタブへ戻す。
            try
            {
                _mpvHostPanel.Dock = DockStyle.None;
                GetPreviewReturnTab().Controls.Add(_mpvHostPanel);
                RestoreMpvHostPanelHome();
                _mpvHostPanel.Visible = true;
                FitMpvWindow();
            }
            catch { /* 戻せなくてもポップアップは閉じる */ }
            _previewPopupForm = null;
            _previewPopupKind = "";
            _previewPopupPanel = null;
            _popupSeekTrackBar = null;
            _popupPreviewSeeking = false;
        };
        popup.Show(this);
        SetPreviewRotation();
        FitMpvWindow();
        var rotationText = GetRotationIndex() > 0 ? GetRotationDisplayText(GetRotationIndex()) : "回転なし";
        WriteLog($"回転プレビューを表示しました（{rotationText}）。表示のみで、出力ファイルには影響しません。");
    }
}
