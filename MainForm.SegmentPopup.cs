using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// 区間ポップアップ再生 (専用の mpv プロセスで区間をループ再生する)。
/// 本体のプレビューとは別の専用 mpv プロセスで、その区間だけを繰り返し再生する。
/// </summary>
internal sealed partial class MainForm
{
    /// <summary>区間ポップアップ専用 mpv の読み取り状態 (読み取りタスクと共有する)。</summary>
    private sealed class SegmentPopupMpvState
    {
        public readonly object Sync = new();
        private double _timePos;
        public StreamWriter? Writer;
        public bool Connected;
        public bool Ended;
        public string Error = "";

        public double TimePos
        {
            get { lock (Sync) return _timePos; }
            set { lock (Sync) _timePos = value; }
        }
    }

    private Form? _segmentPopupForm;
    private Panel? _segmentPopupVideoPanel;
    private readonly WindowDragTracker _segmentPopupDrag = new();
    private long _segmentPopupStartMs;
    private long _segmentPopupEndMs;
    private Segment? _segmentPopupSegment;
    private int _segmentPopupIndex = -1;
    private SeekTrackBar? _segmentPopupSeekBar;
    private System.Windows.Forms.Timer? _segmentPopupTimer;
    private bool _segmentPopupPlaying;
    private bool _segmentPopupSeeking;
    private Process? _segmentPopupMpvProcess;
    private SegmentPopupMpvState? _segmentPopupMpvState;
    private CancellationTokenSource? _segmentPopupReaderCts;

    private void CloseSegmentPreviewPopup()
    {
        if (_segmentPopupForm is { IsDisposed: false })
        {
            try { _segmentPopupForm.Close(); } catch { }
        }
    }

    /// <summary>区間ポップアップの映像部分のドラッグで、ウィンドウを移動する (本体のドラッグと同じポーリング方式)。</summary>
    private void UpdateSegmentPopupPointerDrag()
    {
        if (_segmentPopupForm is not { IsDisposed: false } popup) return;
        if (_segmentPopupVideoPanel is not { IsDisposed: false } panel) return;
        _segmentPopupDrag.Update(popup, () =>
            panel.Visible
            && panel.RectangleToScreen(panel.ClientRectangle).Contains(Control.MousePosition));
    }

    /// <summary>タイトルバーに、区間内の再生位置 (経過 / 区間の長さ) を表示する。</summary>
    private void UpdateSegmentPopupTitle(long positionMs)
    {
        if (_segmentPopupForm == null || _segmentPopupForm.IsDisposed) return;
        var lengthMs = Math.Max(0L, _segmentPopupEndMs - _segmentPopupStartMs);
        var elapsedMs = Math.Max(0L, Math.Min(lengthMs, positionMs - _segmentPopupStartMs));
        var title = $"AsterCut - 区間プレビュー ({TimeText.FormatClockFromMs(elapsedMs)} / {TimeText.FormatClockFromMs(lengthMs)})";
        if (_segmentPopupForm.Text != title) _segmentPopupForm.Text = title;
    }

    private void UpdateSegmentPopupSeekBar(long positionMs)
    {
        if (_segmentPopupSeekBar == null || _segmentPopupSeeking) return;
        var lengthMs = _segmentPopupEndMs - _segmentPopupStartMs;
        if (lengthMs <= 0) return;
        var value = (int)Math.Round((positionMs - (double)_segmentPopupStartMs) / lengthMs * 10000.0);
        value = Math.Clamp(value, 0, 10000);
        if (_segmentPopupSeekBar.Value != value) _segmentPopupSeekBar.Value = value;
    }

    /// <summary>区間ポップアップの専用 mpv へ、IPC でコマンドを送る (応答は待たない)。</summary>
    private void SendSegmentPopupMpvCommand(object[] command)
    {
        var writer = _segmentPopupMpvState?.Writer;
        if (writer == null) return;
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new { command }));
        }
        catch { }
    }

    /// <summary>区間ポップアップの現在の再生位置 (ミリ秒) を返す。</summary>
    private long GetSegmentPopupPositionMs()
    {
        if (_segmentPopupMpvState != null)
        {
            var timePos = _segmentPopupMpvState.TimePos;
            if (timePos >= 0) return (long)Math.Round(timePos * 1000.0);
        }
        return _segmentPopupStartMs;
    }

    private void SeekSegmentPopupToMs(long targetMs)
    {
        targetMs = Math.Max(_segmentPopupStartMs, Math.Min(_segmentPopupEndMs, targetMs));
        SendSegmentPopupMpvCommand(new object[] { "seek", targetMs / 1000.0, "absolute+exact" });
        UpdateSegmentPopupTitle(targetMs);
        UpdateSegmentPopupSeekBar(targetMs);
    }

    /// <summary>区間ポップアップのシークバーの位置からシークする。</summary>
    private void SeekSegmentPopupFromBar()
    {
        if (_segmentPopupSeekBar == null) return;
        var fraction = _segmentPopupSeekBar.Value / 10000.0;
        SeekSegmentPopupToMs(_segmentPopupStartMs + (long)Math.Round((_segmentPopupEndMs - (double)_segmentPopupStartMs) * fraction));
    }

    /// <summary>区間ポップアップでフレーム移動する (再生中なら一時停止してからステップする)。</summary>
    private void StepSegmentPopupFrame(int direction, int frames)
    {
        if (_segmentPopupPlaying)
        {
            _segmentPopupPlaying = false;
            SendSegmentPopupMpvCommand(new object[] { "set_property", "pause", true });
        }
        if (frames > 1 && TestFrameTableReady())
        {
            // frame-back-step は続けて送ると、前のフレームへ戻る動作同士が重なって
            // 1 フレームしか戻らないことがあるため、複数フレームの移動は
            // フレーム表から移動先を求めてシークする。
            var currentMs = _segmentPopupStartMs + GetSegmentPopupPositionMs();
            // ポップアップの mpv の位置はフレーム表の時刻と 1ms 程度ずれることがあるため、
            // 半フレーム分を足して、表示中のフレームそのものを基準にする。
            var halfFrameUs = _previewFrameRate > 0 ? (long)(500000.0 / _previewFrameRate) : 16667L;
            var index = FindFrameIndexAtOrBefore(currentMs * 1000L + halfFrameUs);
            if (index >= 0)
            {
                var target = Math.Clamp(index + direction * frames, 0, _frameTimesUs!.Length - 1);
                SeekSegmentPopupToMs(GetFrameFloorMs(target) - _segmentPopupStartMs);
                return;
            }
        }
        var command = direction > 0 ? "frame-step" : "frame-back-step";
        for (var i = 0; i < frames; i++) SendSegmentPopupMpvCommand(new object[] { command });
    }

    private void ToggleSegmentPopupPlayback()
    {
        _segmentPopupPlaying = !_segmentPopupPlaying;
        SendSegmentPopupMpvCommand(new object[] { "set_property", "pause", !_segmentPopupPlaying });
    }

    private long _lastSegmentPopupSpaceToggleTicks;

    /// <summary>
    /// スペースキー経路 (フォームのキーイベントとポーリング) からの再生 / 一時停止。
    /// 同じ押下が両経路で二重に切り替わらないよう、直前の切り替えから間もないものは無視する。
    /// </summary>
    private void ToggleSegmentPopupPlaybackFromSpace()
    {
        var now = Environment.TickCount64;
        if (now - _lastSegmentPopupSpaceToggleTicks < 100) return;
        _lastSegmentPopupSpaceToggleTicks = now;
        ToggleSegmentPopupPlayback();
    }

    /// <summary>区間ポップアップ専用 mpv の time-pos を受け取る読み取り処理。</summary>
    private void RunSegmentPopupReader(string pipeName, SegmentPopupMpvState state, CancellationToken token)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var connected = false;
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                try { pipe.Connect(300); connected = true; break; }
                catch { Thread.Sleep(80); }
            }
            if (!connected)
            {
                state.Error = "接続できませんでした";
                return;
            }
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            state.Writer = writer;
            state.Connected = true;
            writer.WriteLine("{\"command\":[\"observe_property\",1,\"time-pos\"]}");
            using var reader = new StreamReader(pipe);
            while (!token.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                try
                {
                    using var message = JsonDocument.Parse(line);
                    var root = message.RootElement;
                    if (root.TryGetProperty("event", out var eventElement) && eventElement.GetString() == "property-change"
                        && root.TryGetProperty("name", out var nameElement) && nameElement.GetString() == "time-pos"
                        && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Number)
                    {
                        state.TimePos = dataElement.GetDouble();
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
        }
        state.Ended = true;
    }

    /// <summary>
    /// 区間一覧の右クリックから開く、その区間だけを繰り返し再生するポップアップ。
    /// 本体のプレビューとは別の専用プレーヤーで再生するため、本体の再生状態には影響しない。
    /// 大きさと配置は回転プレビューにそろえる (区間一覧の少し上、映像とシークバーだけ)。
    /// </summary>
    private void ShowSegmentPreviewPopup(int index)
    {
        if (index < 0 || index >= _segments.Count) return;
        if (string.IsNullOrEmpty(_inputPath) || !File.Exists(_inputPath))
        {
            MessageBox.Show("先に動画を読み込んでください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // すでに開いている場合は閉じて、選んだ区間で開き直す。
        CloseSegmentPreviewPopup();
        var segment = _segments[index].Clone();
        _segmentPopupStartMs = segment.StartMs;
        _segmentPopupEndMs = segment.EndMs;
        _segmentPopupSegment = segment;
        _segmentPopupIndex = index;
        _segmentPopupPlaying = false;
        _segmentPopupSeeking = false;

        var popup = new Form
        {
            Icon = AppIcon.Value,
            Text = "AsterCut - 区間プレビュー",
            MinimumSize = new Size(480, 360),
            BackColor = Color.Black,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.None,
            Font = Font,
        };
        // 区間一覧と重ならないよう、一覧の少し上に表示する。画面の上端に収まらない場合は高さを縮める。
        try
        {
            var listTopLeft = _segmentList.PointToScreen(Point.Empty);
            var workArea = Screen.FromControl(_segmentList).WorkingArea;
            const int popupGap = 12;
            var popupWidth = Math.Min(900, workArea.Width);
            var popupHeight = Math.Max(360, Math.Min(640, listTopLeft.Y - popupGap - workArea.Top));
            var popupX = Math.Max(workArea.Left, Math.Min(listTopLeft.X, workArea.Right - popupWidth));
            var popupY = Math.Max(workArea.Top, listTopLeft.Y - popupGap - popupHeight);
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
        _segmentPopupSeekBar = popupSeek;

        var mpvPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
        };
        _segmentPopupVideoPanel = mpvPanel;
        popup.Controls.Add(mpvPanel);
        popup.Controls.Add(panel);
        panel.Controls.Add(popupSeek);
        _segmentPopupForm = popup;

        popupSeek.MouseDown += (_, _) => { _segmentPopupSeeking = true; };
        popupSeek.MouseUp += (_, _) =>
        {
            if (_segmentPopupSeeking)
            {
                _segmentPopupSeeking = false;
                SeekSegmentPopupFromBar();
            }
        };
        popupSeek.Scroll += (_, _) => SeekSegmentPopupFromBar();
        // 矢印キーと PageUp / PageDown は画面側のキー操作に使うため、離したときのシークでは戻さない。
        popupSeek.KeyUp += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown) return; SeekSegmentPopupFromBar(); };
        // キー操作: 空白で再生・一時停止、Enter で現在の回転・速度を反映して読み込み直す、Esc で閉じる、
        // ← / → で1フレーム戻る・進む、↑ / ↓ で5フレーム戻る・進む、PageUp / PageDown で前・次の区間を再生、Home で区間の先頭へ戻る。
        popup.KeyDown += (sender, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.Space: ToggleSegmentPopupPlaybackFromSpace(); break;
                case Keys.Enter: if (_segmentPopupIndex >= 0) ShowSegmentPreviewPopup(_segmentPopupIndex); break;
                case Keys.Escape: ((Form)sender!).Close(); break;
                case Keys.Left: StepSegmentPopupFrame(-1, 1); break;
                case Keys.Right: StepSegmentPopupFrame(1, 1); break;
                case Keys.Up: StepSegmentPopupFrame(-1, 5); break;
                case Keys.Down: StepSegmentPopupFrame(1, 5); break;
                case Keys.PageUp: if (_segmentPopupIndex > 0) { ShowSegmentPreviewPopup(_segmentPopupIndex - 1); SelectSegmentIndices(_segmentPopupIndex); } break;
                case Keys.PageDown: if (_segmentPopupIndex >= 0 && _segmentPopupIndex < _segments.Count - 1) { ShowSegmentPreviewPopup(_segmentPopupIndex + 1); SelectSegmentIndices(_segmentPopupIndex); } break;
                case Keys.Home: SeekSegmentPopupToMs(_segmentPopupStartMs); break;
                default: return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        _segmentPopupTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (_segmentPopupForm == null || _segmentPopupForm.IsDisposed) return;
            try
            {
                if (_segmentPopupMpvState != null)
                {
                    var timePos = _segmentPopupMpvState.TimePos;
                    if (timePos >= 0)
                    {
                        var positionMs = (long)Math.Round(timePos * 1000.0);
                        UpdateSegmentPopupTitle(positionMs);
                        UpdateSegmentPopupSeekBar(positionMs);
                    }
                }
            }
            catch { }
        };

        popup.FormClosing += (_, _) =>
        {
            if (_segmentPopupTimer != null)
            {
                try { _segmentPopupTimer.Stop(); _segmentPopupTimer.Dispose(); } catch { }
                _segmentPopupTimer = null;
            }
            if (_segmentPopupReaderCts != null)
            {
                try { _segmentPopupReaderCts.Cancel(); } catch { }
                _segmentPopupReaderCts = null;
            }
            if (_segmentPopupMpvProcess != null)
            {
                try { if (!_segmentPopupMpvProcess.HasExited) _segmentPopupMpvProcess.Kill(); } catch { }
                try { _segmentPopupMpvProcess.Dispose(); } catch { }
                _segmentPopupMpvProcess = null;
            }
            if (_segmentPopupMpvState != null)
            {
                try { _segmentPopupMpvState.Writer?.Dispose(); } catch { }
                _segmentPopupMpvState = null;
            }
            _segmentPopupForm = null;
            _segmentPopupVideoPanel = null;
            _segmentPopupSeekBar = null;
            _segmentPopupPlaying = false;
            _segmentPopupSeeking = false;
            _segmentPopupSegment = null;
            _segmentPopupIndex = -1;
            _segmentPopupStartMs = 0;
            _segmentPopupEndMs = 0;
        };
        Theme.ApplyDarkTitleBar(popup);
        popup.Show(this);
        timer.Start();

        // 本体の mpv とは別プロセス・別の IPC で、専用の mpv を起動する。
        try
        {
            var mpv = GetMpvPath();
            var pipeName = $"astercut-mpvseg-{Environment.ProcessId}-{Environment.TickCount}";
            var state = new SegmentPopupMpvState { TimePos = segment.StartMs / 1000.0 };
            _segmentPopupMpvState = state;
            var psi = new ProcessStartInfo
            {
                FileName = mpv,
                WorkingDirectory = Path.GetDirectoryName(mpv) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var option in new[]
            {
                "--no-config", "--force-window=yes", "--keep-open=yes", "--no-terminal", "--really-quiet",
                "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no", "--osc=no",
                "--osd-level=0", "--hr-seek=yes", "--audio-display=no", "--sub-auto=no",
            })
            {
                psi.ArgumentList.Add(option);
            }
            var initialVolume = _previewMuted ? 0 : Math.Clamp(_volumeSliderValue, 0, 100);
            psi.ArgumentList.Add($"--volume={initialVolume}");
            var culture = CultureInfo.InvariantCulture;
            var startSec = (segment.StartMs / 1000.0).ToString("0.###", culture);
            var endSec = (segment.EndMs / 1000.0).ToString("0.###", culture);
            psi.ArgumentList.Add($"--start={startSec}");
            // 区間の範囲を ab-loop に指定し、mpv 自身に繰り返し再生させる。
            psi.ArgumentList.Add($"--ab-loop-a={startSec}");
            psi.ArgumentList.Add($"--ab-loop-b={endSec}");
            var speed = GetSelectedSpeed();
            if (Math.Abs(speed - 1.0) >= 0.000001)
            {
                psi.ArgumentList.Add($"--speed={speed.ToString("0.###", culture)}");
            }
            var angle = GetPreviewRotationClockwise();
            if (angle != 0)
            {
                psi.ArgumentList.Add($"--video-rotate={angle}");
            }
            if (segment.HasClipping)
            {
                try
                {
                    var dimensions = GetVideoDimensions();
                    var cropWidth = dimensions.Width - segment.Left - segment.Right;
                    var cropHeight = dimensions.Height - segment.Top - segment.Bottom;
                    if (cropWidth >= 2 && cropHeight >= 2)
                    {
                        psi.ArgumentList.Add($"--video-crop={cropWidth}x{cropHeight}+{segment.Left}+{segment.Top}");
                    }
                }
                catch { }
            }
            psi.ArgumentList.Add($"--wid={mpvPanel.Handle.ToInt64()}");
            psi.ArgumentList.Add($"--input-ipc-server=\\\\.\\pipe\\{pipeName}");
            psi.ArgumentList.Add(_inputPath);
            _segmentPopupMpvProcess = Process.Start(psi);
            _segmentPopupPlaying = true;
            // 再生位置 (time-pos) を受け取る専用の読み取り処理を、別のタスクで動かす。
            _segmentPopupReaderCts = new CancellationTokenSource();
            var token = _segmentPopupReaderCts.Token;
            _ = Task.Run(() => RunSegmentPopupReader(pipeName, state, token), token);
        }
        catch (Exception ex)
        {
            WriteLog($"区間プレビュー用の mpv を起動できませんでした: {ex.Message}");
            CloseSegmentPreviewPopup();
            return;
        }
        UpdateSegmentPopupTitle(segment.StartMs);
        WriteLog($"区間プレビューを表示しました（{TimeText.FormatClockFromMs(segment.StartMs)} → {TimeText.FormatClockFromMs(segment.EndMs)}）。本体のプレビューとは別に、閉じるまで繰り返し再生します。");
    }
}
