using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// mpv プレビュー。
/// mpv を子ウィンドウとして埋め込み、名前付きパイプの JSON IPC で操作する。
/// 時刻は原則として ffprobe (ffmpeg) のタイムラインで扱い、mpv の time-pos とは
/// 先頭フレームで測った補正値 (MpvTimelineOffsetUs) で相互変換する。
/// </summary>
internal sealed partial class MainForm
{
    // ---- プレビューの状態 ----
    private bool _previewReady;
    private bool _previewIsPlaying;
    private bool _previewAtEnd;
    private long _previewLogicalPositionMs;
    private long _previewPendingSeekMs = -1;
    private DateTime _previewSeekPendingUntil = DateTime.MinValue;
    // シーク後に実際に表示されたフレームへ位置を合わせるための待ち受け (狙いのフレーム番号。-1 は待ち受けなし)。
    private int _previewSnapTargetIndex = -1;
    private DateTime _previewSnapArmTime = DateTime.MinValue;
    private DateTime _previewSnapDeadline = DateTime.MinValue;
    // フレーム移動から待ち受けたときの移動元フレームと方向 (表示が動かなかったときの予備動作の判定用。方向 0 はフレーム移動以外)。
    private int _previewSnapStepFromIndex = -1;
    private int _previewSnapStepDirection = 0;
    // 予備動作で最後に試したキーフレームと、その試行回数。
    private int _previewSnapEscapeIndex = -1;
    private int _previewSnapEscapeCount = 0;
    private DateTime _previewWarmupUntil = DateTime.MinValue;
    private long _previewDurationMs;
    private bool _previewSeeking;
    private bool _popupPreviewSeeking;
    private double _previewFrameRate = 30.0;
    private int _seekWheelRemainder;

    // ---- 音量の状態 (設定と連動) ----
    private int _volumeSliderValue = 80;
    private int _volumeBeforeMute = 80;
    private bool _previewMuted;

    // ---- mpv 接続の状態 ----
    private Process? _mpvProcess;
    private NamedPipeClientStream? _mpvPipe;
    private Task<int>? _mpvReadTask;
    private readonly byte[] _mpvReadBuffer = new byte[65536];
    private Decoder _mpvDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _mpvTextBuffer = new();
    private int _mpvNextRequestId = 1;
    private readonly HashSet<int> _mpvAwaiting = new();
    private readonly Dictionary<int, JsonElement> _mpvReplies = new();
    private bool _mpvDisconnected;
    private double _mpvTimePos;
    private double _mpvDuration;
    private bool _mpvPaused = true;
    private bool _mpvFileLoadedFlag;
    private bool _mpvEofPending;
    private string _mpvLoadErrorMessage = "";
    private double? _mpvFirstTimePos;
    private DateTime _mpvCalibrateDue = DateTime.MinValue;
    private long _mpvTimelineOffsetUs;
    private string _mpvPumpLastError = "";
    private System.Windows.Forms.Timer _mpvPumpTimer = null!;
    private System.Windows.Forms.Timer _previewTimer = null!;

    private const string MpvIpcPathPrefix = @"\\.\pipe\";

    // ------------------------------------------------------------------
    // mpv プロセスと IPC
    // ------------------------------------------------------------------

    private string GetMpvPath()
    {
        var p = Environment.ExpandEnvironmentVariables(_mpvPath.Trim().Trim('"'));
        if (p.Length == 0 || !File.Exists(p))
        {
            throw new InvalidOperationException("mpv.exe が見つかりません。「ツール」メニューの「オプション」でパスを設定してください。");
        }
        return Path.GetFullPath(p);
    }

    private static string ConvertToMpvJsonValue(object? value)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => JsonSerializer.Serialize(s),
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            float f => ((double)f).ToString("0.######", CultureInfo.InvariantCulture),
            decimal m => ((double)m).ToString("0.######", CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>応答を待つ場合だけ、request_id が一致する応答を返り値にする。それ以外は送るだけ。</summary>
    private JsonElement? SendMpvCommand(object[] command, bool wait = false, int timeoutMs = 1500)
    {
        if (_mpvPipe == null) return null;
        var id = _mpvNextRequestId++;
        var items = new string[command.Length];
        for (var i = 0; i < command.Length; i++) items[i] = ConvertToMpvJsonValue(command[i]);
        var json = "{\"command\":[" + string.Join(",", items) + "],\"request_id\":" +
                   id.ToString(CultureInfo.InvariantCulture) + "}\n";
        if (wait) _mpvAwaiting.Add(id);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            _mpvPipe.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            _mpvAwaiting.Remove(id);
            _mpvDisconnected = true;
            return null;
        }
        if (!wait) return null;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ReadMpvMessages();
            if (_mpvReplies.TryGetValue(id, out var reply))
            {
                _mpvReplies.Remove(id);
                _mpvAwaiting.Remove(id);
                return reply;
            }
            Thread.Sleep(2);
        }
        _mpvAwaiting.Remove(id);
        return null;
    }

    private void ReadMpvMessages()
    {
        if (_mpvPipe == null) return;
        for (var guard = 0; guard < 64; guard++)
        {
            if (_mpvReadTask == null)
            {
                try { _mpvReadTask = _mpvPipe.ReadAsync(_mpvReadBuffer, 0, _mpvReadBuffer.Length); }
                catch { _mpvDisconnected = true; return; }
            }
            if (!_mpvReadTask.IsCompleted) return;
            var task = _mpvReadTask;
            _mpvReadTask = null;
            var count = 0;
            if (!task.IsFaulted && !task.IsCanceled)
            {
                try { count = task.Result; } catch { count = 0; }
            }
            if (count <= 0) { _mpvDisconnected = true; return; }
            var chars = new char[count + 8];
            var charCount = _mpvDecoder.GetChars(_mpvReadBuffer, 0, count, chars, 0);
            _mpvTextBuffer.Append(chars, 0, charCount);
            var text = _mpvTextBuffer.ToString();
            var lastBreak = text.LastIndexOf('\n');
            if (lastBreak < 0) continue;
            _mpvTextBuffer.Clear();
            if (lastBreak + 1 < text.Length) _mpvTextBuffer.Append(text[(lastBreak + 1)..]);
            foreach (var rawLine in text[..lastBreak].Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                ReceiveMpvMessage(line);
            }
        }
    }

    private void ReceiveMpvMessage(string line)
    {
        // ここでは状態の更新とフラグ立てだけを行い、UI の操作は ProcessMpvPendingEvents でまとめて行う。
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return; }
        using (doc)
        {
            var msg = doc.RootElement;
            if (msg.ValueKind != JsonValueKind.Object) return;
            if (msg.TryGetProperty("request_id", out var requestId) && requestId.ValueKind == JsonValueKind.Number)
            {
                var id = requestId.GetInt32();
                if (_mpvAwaiting.Contains(id)) _mpvReplies[id] = msg.Clone();
                return;
            }
            if (!msg.TryGetProperty("event", out var eventElement)) return;
            switch (eventElement.GetString())
            {
                case "property-change":
                    if (!msg.TryGetProperty("name", out var nameElement)) break;
                    var hasData = msg.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null;
                    switch (nameElement.GetString())
                    {
                        case "time-pos":
                            if (hasData && data.ValueKind == JsonValueKind.Number) _mpvTimePos = data.GetDouble();
                            break;
                        case "duration":
                            if (hasData && data.ValueKind == JsonValueKind.Number) _mpvDuration = data.GetDouble();
                            break;
                        case "pause":
                            if (hasData && (data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False)) _mpvPaused = data.GetBoolean();
                            break;
                        case "eof-reached":
                            if (hasData && data.ValueKind == JsonValueKind.True) _mpvEofPending = true;
                            break;
                    }
                    break;
                case "file-loaded":
                    _mpvFileLoadedFlag = true;
                    break;
                case "playback-restart":
                    // 先頭フレームの time-pos は playback-restart の少し後に確定するため、少し待ってから測る。
                    if (_mpvFirstTimePos == null && _mpvCalibrateDue == DateTime.MinValue)
                    {
                        _mpvCalibrateDue = DateTime.UtcNow.AddMilliseconds(250);
                    }
                    break;
                case "end-file":
                    if (msg.TryGetProperty("reason", out var reason) && reason.GetString() == "error")
                    {
                        var detail = msg.TryGetProperty("file_error", out var fileError) && fileError.ValueKind == JsonValueKind.String
                            ? fileError.GetString() ?? ""
                            : "";
                        _mpvLoadErrorMessage = detail.Length > 0 ? detail : "不明なエラー";
                    }
                    break;
                case "shutdown":
                    _mpvDisconnected = true;
                    break;
            }
        }
    }

    private void ProcessMpvPendingEvents()
    {
        if (_mpvDisconnected)
        {
            _mpvDisconnected = false;
            HandleMpvDisconnected();
            return;
        }
        if (_mpvFileLoadedFlag)
        {
            _mpvFileLoadedFlag = false;
            InvokePreviewOpened();
        }
        if (_mpvLoadErrorMessage.Length > 0)
        {
            var message = _mpvLoadErrorMessage;
            _mpvLoadErrorMessage = "";
            InvokePreviewFailed($"mpv で動画を開けません: {message}");
        }
        if (_mpvEofPending)
        {
            _mpvEofPending = false;
            if (_previewReady && _previewIsPlaying) SetPreviewEnded();
        }
        if (_mpvCalibrateDue != DateTime.MinValue && DateTime.UtcNow >= _mpvCalibrateDue)
        {
            _mpvCalibrateDue = DateTime.MinValue;
            CaptureMpvFirstFramePosition();
        }
    }

    private void CaptureMpvFirstFramePosition()
    {
        // 読み込み直後 (まだ動かしていない状態) で、mpv が最初に表示したフレームの time-pos を取得する。
        if (!_previewReady) return;
        if (_previewIsPlaying || _previewAtEnd || _previewLogicalPositionMs != 0) return;
        var reply = SendMpvCommand(new object[] { "get_property", "time-pos" }, true, 800);
        if (reply is { } r && ReplyData(r, out var value))
        {
            _mpvFirstTimePos = value;
            _mpvTimePos = value;
            UpdateMpvTimelineOffset();
        }
    }

    private static bool ReplyData(JsonElement reply, out double value)
    {
        value = 0;
        if (!reply.TryGetProperty("error", out var error) || error.GetString() != "success") return false;
        if (!reply.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Number) return false;
        value = data.GetDouble();
        return true;
    }

    private static bool ReplyBoolData(JsonElement reply, out bool value)
    {
        value = false;
        if (!reply.TryGetProperty("error", out var error) || error.GetString() != "success") return false;
        if (!reply.TryGetProperty("data", out var data)) return false;
        if (data.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (data.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    private void UpdateMpvTimelineOffset()
    {
        // mpv が最初に表示したフレームについて、mpv の時刻と ffmpeg の時刻の差を求める
        // (mpv の時刻 = ffmpeg の時刻 + 補正値)。
        if (_mpvFirstTimePos == null || !TestFrameTableReady()) return;
        var mpvFirstUs = (long)Math.Round(_mpvFirstTimePos.Value * 1000000.0);
        // テーブルの先頭は、復号できない先行 B フレームであることがあり、mpv が表示したのは
        // そのフレームではない。その場合は、mpv の時刻と一致するテーブルのエントリを基準にする
        // (一致するものが無ければ、従来通り先頭のエントリを基準にする)。
        var anchorUs = _frameTimesUs![0];
        foreach (var t in _frameTimesUs!)
        {
            if (Math.Abs(t - mpvFirstUs) <= 2000L) { anchorUs = t; break; }
            if (t > mpvFirstUs + 2000L) break;
        }
        var offsetUs = mpvFirstUs - anchorUs;
        if (Math.Abs(offsetUs) > 30000000L)
        {
            WriteLog($"mpv の時間軸の補正値が大きすぎるため無視します: {offsetUs / 1000.0:0.###} ms");
            offsetUs = 0;
        }
        _mpvTimelineOffsetUs = offsetUs;
        WriteLog($"mpv の時間軸を補正: mpv の先頭フレーム={mpvFirstUs / 1000000.0:0.000000} s / ffmpeg の先頭フレーム={anchorUs / 1000000.0:0.000000} s / 補正値={offsetUs / 1000.0:0.###} ms");
    }

    private void FitMpvWindow()
    {
        if (_mpvHostPanel == null || !_mpvHostPanel.IsHandleCreated) return;
        try { NativeMethods.FitFirstChild(_mpvHostPanel.Handle); }
        catch { /* 子ウィンドウがまだ無ければ何もしない */ }
    }

    private void CloseMpvConnection()
    {
        _mpvPumpTimer.Stop();
        var pipe = _mpvPipe;
        _mpvPipe = null;
        _mpvReadTask = null;
        _mpvAwaiting.Clear();
        _mpvReplies.Clear();
        if (pipe != null) { try { pipe.Dispose(); } catch { } }
    }

    private void StopMpvPlayer()
    {
        _mpvPumpTimer.Stop();
        var process = _mpvProcess;
        var pipe = _mpvPipe;
        _mpvProcess = null;
        _mpvPipe = null;
        _mpvReadTask = null;
        _mpvAwaiting.Clear();
        _mpvReplies.Clear();
        if (pipe != null)
        {
            try
            {
                var quit = Encoding.UTF8.GetBytes("{\"command\":[\"quit\"]}\n");
                pipe.Write(quit, 0, quit.Length);
            }
            catch { }
            try { pipe.Dispose(); } catch { }
        }
        if (process != null)
        {
            try { if (!process.WaitForExit(1000)) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
        }
    }

    private void HandleMpvDisconnected()
    {
        CloseMpvConnection();
        _previewReady = false;
        _previewIsPlaying = false;
        UpdatePreviewPlaybackButtons();
        WriteLog("mpv との接続が切れました。もう一度「読み込み」を押すと mpv を起動し直します。");
        SetStatus("プレビュー終了");
    }

    private bool StartMpvPlayer()
    {
        if (_mpvProcess is { HasExited: false } && _mpvPipe != null) return true;
        StopMpvPlayer();
        var mpv = GetMpvPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = mpv,
            WorkingDirectory = Path.GetDirectoryName(mpv) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var option in new[]
        {
            "--no-config", "--idle=yes", "--force-window=yes", "--keep-open=yes", "--pause",
            "--no-terminal", "--really-quiet",
            "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no",
            "--osc=no", "--osd-level=0", "--hr-seek=yes", "--audio-display=no", "--sub-auto=no",
            "--hwdec=auto-safe",
        })
        {
            startInfo.ArgumentList.Add(option);
        }
        var initialVolume = _previewMuted ? 0 : Math.Clamp(_volumeSliderValue, 0, 100);
        startInfo.ArgumentList.Add($"--volume={initialVolume}");
        // まだ作られていなければ、ここでウィンドウハンドルが作成される。
        var hostHandle = _mpvHostPanel.Handle;
        startInfo.ArgumentList.Add($"--wid={hostHandle.ToInt64()}");
        var pipeName = $"astercut-mpv-{Environment.ProcessId}-{Environment.TickCount}";
        startInfo.ArgumentList.Add("--input-ipc-server=" + MpvIpcPathPrefix + pipeName);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("mpv.exe を起動できませんでした。");

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var connected = false;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) break;
            try { pipe.Connect(150); connected = true; break; }
            catch (TimeoutException) { }
            catch { Thread.Sleep(80); }
        }
        if (!connected)
        {
            var exitText = "";
            try { if (process.HasExited) exitText = $"（mpv は終了コード {process.ExitCode} で終了しました）"; } catch { }
            try { pipe.Dispose(); } catch { }
            try { if (!process.HasExited) process.Kill(); } catch { }
            try { process.Dispose(); } catch { }
            throw new InvalidOperationException("mpv に接続できませんでした。mpv.exe のパスとバージョンを確認してください。" + exitText);
        }
        _mpvProcess = process;
        _mpvPipe = pipe;
        _mpvDecoder = Encoding.UTF8.GetDecoder();
        _mpvTextBuffer.Clear();
        _mpvReadTask = null;
        _mpvAwaiting.Clear();
        _mpvReplies.Clear();
        _mpvDisconnected = false;
        _mpvPaused = true;
        SendMpvCommand(new object[] { "observe_property", 1, "time-pos" });
        SendMpvCommand(new object[] { "observe_property", 2, "duration" });
        SendMpvCommand(new object[] { "observe_property", 3, "pause" });
        SendMpvCommand(new object[] { "observe_property", 4, "eof-reached" });
        _mpvPumpTimer.Start();
        FitMpvWindow();
        WriteLog($"mpv を起動しました: {mpv}");
        return true;
    }

    private void OpenMpvFile(string filePath)
    {
        _mpvFileLoadedFlag = false;
        _mpvLoadErrorMessage = "";
        _mpvEofPending = false;
        _mpvFirstTimePos = null;
        _mpvCalibrateDue = DateTime.MinValue;
        _mpvTimelineOffsetUs = 0;
        _mpvTimePos = 0.0;
        _mpvDuration = 0.0;
        // 再生中のまま loadfile すると、新しいファイルも再生状態で始まってしまうため、先に一時停止する。
        SendMpvCommand(new object[] { "set_property", "pause", true });
        _mpvPaused = true;
        SendMpvCommand(new object[] { "loadfile", Path.GetFullPath(filePath), "replace" });
    }

    // ------------------------------------------------------------------
    // プレーヤー操作
    // ------------------------------------------------------------------

    private bool TestPlayerAvailable() => _mpvPipe != null;

    private TimeSpan GetPlayerPositionTimeSpan()
    {
        var us = (long)Math.Round(_mpvTimePos * 1000000.0) - _mpvTimelineOffsetUs;
        if (us < 0) us = 0;
        return TimeSpan.FromTicks(us * 10L);
    }

    private void UpdatePlayerPositionNow()
    {
        // ボタン操作など、正確な現在位置が必要なときだけ mpv から最新の time-pos を取得する。
        if (_mpvPipe == null) return;
        var reply = SendMpvCommand(new object[] { "get_property", "time-pos" }, true, 300);
        if (reply is { } r && ReplyData(r, out var value)) _mpvTimePos = value;
    }

    private void SetPlayerPosition(TimeSpan position)
    {
        var seconds = ((double)(position.Ticks / 10L) + _mpvTimelineOffsetUs) / 1000000.0;
        // 補正値がわずかに負の場合などに、負の位置へのシークにならないようにする
        // (負の位置を送ると、mpv が末尾へ飛んでしまうことがあるため)。
        if (seconds < 0) seconds = 0;
        _mpvTimePos = seconds;
        SendMpvCommand(new object[] { "seek", seconds, "absolute+exact" });
    }

    private void InvokePlayerPlay()
    {
        _mpvPaused = false;
        SendMpvCommand(new object[] { "set_property", "pause", false });
    }

    private void InvokePlayerPause()
    {
        // すでに一時停止中なら何もしない (連打でも往復通信が増えないようにする)。
        if (_mpvPaused) return;
        SendMpvCommand(new object[] { "set_property", "pause", true }, true, 800);
        _mpvPaused = true;
        // 停止した時点の time-pos が、画面に出ているフレームの時刻になる。
        var reply = SendMpvCommand(new object[] { "get_property", "time-pos" }, true, 800);
        if (reply is { } r && ReplyData(r, out var value)) _mpvTimePos = value;
    }

    private void SetPlayerVolume(double volume)
    {
        // Volume は 0.0 から 1.0 の基準で受け取り、mpv の 0 から 100 へ変換する。
        SendMpvCommand(new object[] { "set_property", "volume", volume * 100.0 });
    }

    private double GetPlayerVolume()
    {
        if (_previewMuted || _volumeSliderValue <= 0) return 0.0;
        return _volumeSliderValue / 100.0;
    }

    private void InvokePreviewFailed(string message)
    {
        _previewReady = false;
        _previewIsPlaying = false;
        UpdatePreviewPlaybackButtons();
        WriteLog(message);
        SetStatus("プレビューを開けませんでした");
    }

    private void InvokePreviewOpened()
    {
        // mpv が動画を開き終えたとき (file-loaded) の共通処理。
        try
        {
            _previewReady = true;
            _previewIsPlaying = false;
            _previewAtEnd = false;
            _previewLogicalPositionMs = 0;
            _previewPendingSeekMs = -1;
            _previewSeekPendingUntil = DateTime.MinValue;
            _previewSnapTargetIndex = -1;
            _previewWarmupUntil = DateTime.MinValue;
            _previewDurationMs = 0;
            if (_mpvDuration > 0) _previewDurationMs = (long)Math.Round(_mpvDuration * 1000.0);
            if (_previewDurationMs <= 0) _previewDurationMs = _durationMs;
            UpdatePreviewPlaybackButtons();
            if (_seekTrackBar != null) _seekTrackBar.Value = 0;
            if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = 0;
            UpdatePreviewPositionLabel();
            SetStatus("プレビュー準備完了");
        }
        catch { /* 表示の更新に失敗しても再生は続ける */ }
    }

    private void SetPreviewSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            _previewReady = false;
            _previewIsPlaying = false;
            _previewAtEnd = false;
            _previewLogicalPositionMs = 0;
            _previewPendingSeekMs = -1;
            _previewSeekPendingUntil = DateTime.MinValue;
            _previewSnapTargetIndex = -1;
            _previewDurationMs = 0;
            _previewSeeking = false;
            _popupPreviewSeeking = false;
            UpdatePreviewPlaybackButtons();
            _previewFrameRate = GetFrameRate();
            try
            {
                StartMpvPlayer();
            }
            catch (Exception ex)
            {
                WriteLog($"mpv を使えませんでした: {ex.Message}");
                MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("プレビューを開けませんでした");
                return;
            }
            OpenMpvFile(filePath);
            _previewPositionLabel.Text = "読み込み中...";
            if (_popupPositionLabel != null) _popupPositionLabel.Text = "読み込み中...";
            if (_clipPositionLabel != null) _clipPositionLabel.Text = "読み込み中...";
            SetStatus("プレビュー読み込み中...");
        }
        catch (Exception ex)
        {
            WriteLog($"プレビュー設定エラー: {ex.Message}");
            SetStatus("プレビューを開けませんでした");
        }
    }

    private double GetFrameRate()
    {
        if (_videoStreams.Count == 0) return 30.0;
        var stream = _videoStreams[0];
        var fps = Probe.ConvertFrameRateToDouble(stream.AvgFrameRate);
        if (fps > 0) return fps;
        fps = Probe.ConvertFrameRateToDouble(stream.RFrameRate);
        if (fps > 0) return fps;
        return 30.0;
    }

    /// <summary>総時間は mpv の報告ではなく ffprobe の形式時間を最優先で使う (カットの時間軸とそろえるため)。</summary>
    private long GetPreviewDurationMs()
    {
        if (_durationMs > 0) return _durationMs;
        return _previewDurationMs;
    }

    // ------------------------------------------------------------------
    // フレーム精度の時刻変換
    // ------------------------------------------------------------------

    private bool TestFrameTableReady() => _frameTimesUs is { Length: > 1 };

    private int FindFrameIndexAtOrBefore(long timeUs)
    {
        if (!TestFrameTableReady()) return -1;
        return FrameIndex.IndexAtOrBefore(_frameTimesUs!, timeUs);
    }

    // 指定フレームより前にある最後のキーフレームのフレーム番号。なければ -1。
    private int FindPreviousKeyframeIndex(int frameIndex)
    {
        if (_frameKeyTimesUs == null || _frameTimesUs == null) return -1;
        if (frameIndex < 0 || frameIndex >= _frameTimesUs.Length) return -1;
        var keyPos = FrameIndex.IndexAtOrBefore(_frameKeyTimesUs, _frameTimesUs[frameIndex] - 1);
        if (keyPos < 0) return -1;
        return FrameIndex.IndexAtOrBefore(_frameTimesUs, _frameKeyTimesUs[keyPos]);
    }

    // 指定フレームより後にある最初のキーフレームのフレーム番号。なければ -1。
    private int FindNextKeyframeIndex(int frameIndex)
    {
        if (_frameKeyTimesUs == null || _frameTimesUs == null) return -1;
        if (frameIndex < 0 || frameIndex >= _frameTimesUs.Length) return -1;
        var keyPos = FrameIndex.IndexAtOrBefore(_frameKeyTimesUs, _frameTimesUs[frameIndex]);
        if (keyPos + 1 >= _frameKeyTimesUs.Length) return -1;
        return FrameIndex.IndexAtOrBefore(_frameTimesUs, _frameKeyTimesUs[keyPos + 1]);
    }

    /// <summary>
    /// 指定時刻以後で最初のフレームの番号。開始・終了欄の値はフレームの位置をミリ秒へ
    /// 切り捨てたものなので、そのフレーム自身に戻るには「その値を含むフレーム」、
    /// つまり値以後で最初のフレームを探す必要がある。範囲外は端のフレームにする。
    /// </summary>
    private int FindFrameIndexAtOrAfter(long timeUs)
    {
        if (!TestFrameTableReady()) return -1;
        var index = FrameIndex.IndexAtOrBefore(_frameTimesUs!, timeUs);
        if (index < 0) return 0;
        if (_frameTimesUs![index] >= timeUs) return index;
        return index + 1 < _frameTimesUs.Length ? index + 1 : index;
    }

    /// <summary>
    /// 再生位置ラベルの末尾に付ける、フレーム番号の文字列。
    /// フレーム表ができているときだけ、現在位置のフレーム番号と総フレーム数を付ける。
    /// </summary>
    private string GetPreviewFrameInfoText(long positionMs)
    {
        if (!TestFrameTableReady()) return "";
        // ミリ秒への切り捨てで 1 つ前のフレームにならないよう、フレーム移動の現在位置と同じ余裕を持たせる。
        var frameIndex = FindFrameIndexAtOrBefore(positionMs * 1000L + 999L);
        if (frameIndex < 0) frameIndex = 0;
        return $"({frameIndex}f / {_frameTimesUs!.Length}f)";
    }

    private long GetFrameFloorMs(int index)
    {
        // 先頭フレームの位置は、ファイルの先頭 (0) として扱う。テーブルの先頭は復号できない
        // 先行 B フレームで、その時刻 (例: 00:00:00.574) が位置になってしまうことがあるため。
        if (index == 0) return 0;
        return (long)Math.Floor(_frameTimesUs![index] / 1000.0);
    }

    private TimeSpan GetFrameSeekTimeSpan(int index)
    {
        // 先頭フレームは、読み込み直後と同じ状態にするため、ファイルの先頭へシークする
        // (先頭パケットの時刻へシークすると、表示が先のフレームへずれることがあるため)。
        if (index == 0) return TimeSpan.Zero;
        // フレームの時刻より 1 ms だけ先へシークし、直前のフレームではなくこのフレームを表示させる。
        return TimeSpan.FromTicks((_frameTimesUs![index] + 1000L) * 10L);
    }

    private TimeSpan GetPreviewSeekTimeSpan(long logicalMs)
    {
        if (TestFrameTableReady())
        {
            var frameIndex = FindFrameIndexAtOrBefore(logicalMs * 1000L + 999L);
            if (frameIndex >= 0) return GetFrameSeekTimeSpan(frameIndex);
        }
        return TimeSpan.FromMilliseconds(logicalMs);
    }

    private long GetPreviewPositionUs()
    {
        var us = (long)((double)GetPlayerPositionTimeSpan().Ticks / 10.0);
        // mpv の time-pos は表示中フレームの時刻そのもの。丸め誤差で 1 つ前のフレームを選ばないよう余裕を持たせる。
        us += 500L;
        return us;
    }

    // ------------------------------------------------------------------
    // 再生・シーク操作
    // ------------------------------------------------------------------

    private void UpdatePreviewPlaybackButtons()
    {
        var text = _previewIsPlaying ? "一時停止" : "再生";
        if (_previewToggleButton != null) _previewToggleButton.Text = text;
        if (_popupPlayButton != null) _popupPlayButton.Text = text;
        if (_clipPlayButton != null) _clipPlayButton.Text = text;
    }

    /// <summary>
    /// 「キーフレームを表示する」設定とフレーム表のキーフレームを、シークバーの縦線表示へ反映する。
    /// </summary>
    // キーフレーム表示が有効なとき、位置表示の左に直前のキーフレーム、右に直後のキーフレームのフレーム番号を表示する。
    // 現在のフレーム自体がキーフレームのときは、それを直前として表示する。前 (または後) にキーフレームが無いときは、その側は空にする。
    private void UpdateKeyframeFlankLabels(long positionMs)
    {
        if (_prevKeyframeLabel == null || _nextKeyframeLabel == null) return;
        var prevText = "";
        var nextText = "";
        if (_settings.ShowKeyframesOnSeekBar && _frameTimesUs is { Length: > 0 } && _frameKeyTimesUs is { Length: > 0 })
        {
            var currentIndex = FindFrameIndexAtOrBefore(positionMs * 1000L + 999L);
            // 先頭フレームより手前の位置は、位置表示と同じくフレーム 0 として扱う (途中の GOP から始まる素材用)。
            if (currentIndex < 0) currentIndex = 0;
            if (currentIndex >= 0)
            {
                var currentTimeUs = _frameTimesUs[currentIndex];
                // 直前は、現在のフレームの開始時刻以下の最後のキーフレーム。
                var lo = 0;
                var hi = _frameKeyTimesUs.Length - 1;
                var prevKey = -1;
                while (lo <= hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    if (_frameKeyTimesUs[mid] <= currentTimeUs) { prevKey = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (prevKey >= 0)
                {
                    var prevIndex = FindFrameIndexAtOrBefore(_frameKeyTimesUs[prevKey]);
                    if (prevIndex >= 0) prevText = $"◀ {prevIndex}f";
                }
                if (prevKey + 1 < _frameKeyTimesUs.Length)
                {
                    var nextIndex = FindFrameIndexAtOrBefore(_frameKeyTimesUs[prevKey + 1]);
                    if (nextIndex >= 0) nextText = $"{nextIndex}f ▶";
                }
            }
        }
        if (_prevKeyframeLabel.Text != prevText) _prevKeyframeLabel.Text = prevText;
        if (_nextKeyframeLabel.Text != nextText) _nextKeyframeLabel.Text = nextText;
    }

    /// <summary>
    /// 入力履歴の新しい方から順に、最初に存在したファイルを読み込んで再生する。
    /// 動画を入力していない状態で再生が押されたときに使う。
    /// </summary>
    private async Task LoadLatestHistoryAndPlayAsync()
    {
        if (_loadingInput) return;
        string? target = null;
        foreach (var entry in _settings.InputHistory)
        {
            if (!string.IsNullOrWhiteSpace(entry) && File.Exists(entry)) { target = entry; break; }
        }
        if (target == null)
        {
            SetStatus("入力履歴に読み込めるファイルがありません。");
            return;
        }
        await LoadInputFileAsync(target);
        if (_probe == null) return;
        // mpv がファイルを開き終える (プレビュー準備完了) のを待ってから再生する。
        for (var i = 0; i < 100 && !_previewReady; i++) await Task.Delay(100);
        if (_previewReady && !_previewIsPlaying) TogglePreviewPlayback();
    }

    private void TogglePreviewPlayback()
    {
        if (_inputPath.Length == 0 && !_loadingInput)
        {
            // 動画を入力していない状態で再生したら、入力履歴から読み込んで再生する。
            _ = LoadLatestHistoryAndPlayAsync();
            return;
        }
        if (!_previewReady || !TestPlayerAvailable()) return;
        RestoreStepAudio();
        _previewSnapTargetIndex = -1;
        try
        {
            if (_previewIsPlaying)
            {
                // 先に一時停止し、停止した時点の位置を読む。
                InvokePlayerPause();
                _previewIsPlaying = false;
                var pausedFrameIndex = -1;
                try
                {
                    _previewLogicalPositionMs = Math.Max(0L, (long)Math.Round(GetPlayerPositionTimeSpan().TotalMilliseconds));
                    if (TestFrameTableReady())
                    {
                        // 表示中のフレームに合わせ、画面の時刻が実在するフレームの時刻になるようにする。
                        pausedFrameIndex = FindFrameIndexAtOrBefore(GetPreviewPositionUs());
                        if (pausedFrameIndex >= 0) _previewLogicalPositionMs = GetFrameFloorMs(pausedFrameIndex);
                    }
                }
                catch { /* 位置が取れなくても停止は成立させる */ }
                if (pausedFrameIndex >= 0)
                {
                    // ラベルに表示している時刻のフレームを、そのまま表示する。
                    try { SetPlayerPosition(GetFrameSeekTimeSpan(pausedFrameIndex)); } catch { }
                }
            }
            else
            {
                if (_previewAtEnd)
                {
                    _previewLogicalPositionMs = 0;
                    _previewPendingSeekMs = 0;
                    _previewSeekPendingUntil = DateTime.Now.AddMilliseconds(1000);
                    SetPlayerPosition(TimeSpan.Zero);
                    _previewAtEnd = false;
                    if (_seekTrackBar != null) _seekTrackBar.Value = 0;
                    if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = 0;
                }
                else
                {
                    // 再生開始の直前に、一時停止中の論理位置を再設定する。
                    SetPlayerPosition(GetPreviewSeekTimeSpan(_previewLogicalPositionMs));
                }
                InvokePlayerPlay();
                _previewIsPlaying = true;
            }
            UpdatePreviewPlaybackButtons();
            UpdatePreviewPositionLabel();
        }
        catch (Exception ex)
        {
            WriteLog($"再生状態の変更に失敗しました: {ex.Message}");
        }
    }

    private void SetPreviewEnded()
    {
        if (!_previewReady) return;
        try { InvokePlayerPause(); } catch { }
        _previewIsPlaying = false;
        _previewAtEnd = true;
        var durationMs = GetPreviewDurationMs();
        if (durationMs > 0)
        {
            _previewLogicalPositionMs = durationMs;
            _previewPendingSeekMs = -1;
            if (_seekTrackBar != null) _seekTrackBar.Value = _seekTrackBar.Maximum;
            if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = _popupSeekTrackBar.Maximum;
        }
        UpdatePreviewPlaybackButtons();
        UpdatePreviewPositionLabel();
    }

    // フレーム移動ボタンの長押しリピート。押し続けて 400ms 後から、80ms 間隔で
    // クリック連打と同じようにフレーム移動を繰り返す (キーボードの長押しと同じ間隔)。
    private void StartFrameStepRepeat(int direction)
    {
        StopFrameStepRepeat();
        _frameStepRepeatDirection = direction;
        _frameStepRepeatFast = false;
        _frameStepRepeatTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _frameStepRepeatTimer.Tick += FrameStepRepeatTick;
        _frameStepRepeatTimer.Start();
    }

    private void FrameStepRepeatTick(object? sender, EventArgs e)
    {
        if (IsDisposed) { StopFrameStepRepeat(); return; }
        if (!_frameStepRepeatFast)
        {
            _frameStepRepeatFast = true;
            _frameStepRepeatTimer.Interval = 80;
        }
        MovePreviewFrame(_frameStepRepeatDirection);
    }

    private void StopFrameStepRepeat()
    {
        if (_frameStepRepeatTimer == null) return;
        _frameStepRepeatTimer.Stop();
        _frameStepRepeatTimer.Tick -= FrameStepRepeatTick;
        _frameStepRepeatTimer.Dispose();
        _frameStepRepeatTimer = null!;
    }

    // フレームステップ中だけ mpv の mute を使うための状態 (利用者の音量ミュートとは別)。
    private bool _stepAudioSuppressed;
    private System.Windows.Forms.Timer? _stepAudioRestoreTimer;

    /// <summary>
    /// 一時停止中のフレーム移動で、ステップしたフレームの音が一瞬出ないようにする。
    /// mpv のフレームステップは 1 フレーム分だけ再生するため、その間だけ mute をかけ、
    /// 最後のステップから少し置いて戻す。
    /// </summary>
    private void SuppressAudioForStep()
    {
        if (!_stepAudioSuppressed)
        {
            _stepAudioSuppressed = true;
            SendMpvCommand(new object[] { "set_property", "mute", true });
        }
        if (_stepAudioRestoreTimer == null)
        {
            _stepAudioRestoreTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _stepAudioRestoreTimer.Tick += (_, _) =>
            {
                _stepAudioRestoreTimer?.Stop();
                RestoreStepAudio();
            };
        }
        _stepAudioRestoreTimer.Stop();
        _stepAudioRestoreTimer.Start();
    }

    private void RestoreStepAudio()
    {
        if (!_stepAudioSuppressed) return;
        _stepAudioSuppressed = false;
        SendMpvCommand(new object[] { "set_property", "mute", false });
    }

    private void MovePreviewFrame(int direction)
    {
        if (!_previewReady || !TestPlayerAvailable()) return;
        _previewWarmupUntil = DateTime.MinValue;
        try
        {
            var wasPlaying = _previewIsPlaying;
            // 先に一時停止し、停止した時点の位置を基準にする。
            InvokePlayerPause();
            _previewIsPlaying = false;
            UpdatePreviewPlaybackButtons();

            var durationMs = GetPreviewDurationMs();
            var useTable = TestFrameTableReady();
            var currentIndex = -1;
            var currentMs = 0L;
            if (useTable)
            {
                // 実際に表示されているフレームの番号を基準にする。
                if (wasPlaying)
                {
                    currentIndex = FindFrameIndexAtOrBefore(GetPreviewPositionUs());
                }
                else if (_previewAtEnd)
                {
                    // 再生終了後は最終フレームが表示されているため、1 つ戻ると最終フレームそのものを指す。
                    currentIndex = _frameTimesUs!.Length;
                }
                else
                {
                    currentIndex = FindFrameIndexAtOrBefore(_previewLogicalPositionMs * 1000L + 999L);
                }
                if (currentIndex < 0) currentIndex = 0;
            }
            else
            {
                if (wasPlaying)
                {
                    try { _previewLogicalPositionMs = Math.Max(0L, (long)Math.Round(GetPlayerPositionTimeSpan().TotalMilliseconds)); }
                    catch { }
                }
                currentMs = GetPreviewCurrentPositionMs();
            }
            if (_previewAtEnd && direction > 0) return;

            long newMs;
            TimeSpan seekSpan;
            var snapIndex = -1;
            if (useTable)
            {
                var lastIndex = _frameTimesUs!.Length - 1;
                if (direction > 0 && currentIndex >= lastIndex && durationMs > GetFrameFloorMs(lastIndex))
                {
                    // 最終フレームから 1 つ進むと「動画の終端」の位置になる (画面は最終フレームのまま)。
                    SetPreviewEnded();
                    return;
                }
                var newIndex = Math.Max(0, Math.Min(lastIndex, currentIndex + direction));
                newMs = GetFrameFloorMs(newIndex);
                seekSpan = GetFrameSeekTimeSpan(newIndex);
                snapIndex = newIndex;
            }
            else
            {
                var fps = _previewFrameRate;
                if (fps <= 0) fps = 30.0;
                // 丸めたミリ秒を足し続けず、フレーム番号から計算する (29.97 fps でずれていかないように)。
                var currentFrameIndex = (long)Math.Round(currentMs * fps / 1000.0, MidpointRounding.AwayFromZero);
                var newFrameIndex = Math.Max(0L, currentFrameIndex + direction);
                if (durationMs > 0)
                {
                    var maxFrameIndex = (long)Math.Max(0.0, Math.Floor((durationMs - 1) * fps / 1000.0));
                    newFrameIndex = Math.Min(maxFrameIndex, newFrameIndex);
                }
                newMs = (long)Math.Round(newFrameIndex * 1000.0 / fps, MidpointRounding.AwayFromZero);
                if (newMs < 0) newMs = 0;
                if (durationMs > 0) newMs = Math.Min(Math.Max(0L, durationMs - 1), newMs);
                seekSpan = TimeSpan.FromMilliseconds(newMs);
            }
            var wasAtEnd = _previewAtEnd;
            _previewAtEnd = false;
            _previewLogicalPositionMs = newMs;
            _previewPendingSeekMs = newMs;
            _previewSeekPendingUntil = DateTime.Now.AddMilliseconds(1000);
            _previewSeeking = false;
            _popupPreviewSeeking = false;
            if (useTable && !wasPlaying && !wasAtEnd) SuppressAudioForStep();
            SetPlayerPosition(seekSpan);
            if (snapIndex >= 0) ArmPreviewLandingSnap(snapIndex, currentIndex, direction);
            if (durationMs > 0)
            {
                var sv = (int)Math.Round(newMs / (double)durationMs * 10000.0);
                sv = Math.Clamp(sv, 0, 10000);
                if (_seekTrackBar != null) _seekTrackBar.Value = sv;
                if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = sv;
                if (_clipSeekTrackBar != null) _clipSeekTrackBar.Value = sv;
            }
            UpdatePreviewPositionLabel();
        }
        catch (Exception ex)
        {
            WriteLog($"フレーム移動エラー: {ex.Message}");
        }
    }

    /// <summary>Home キー用: 先頭のフレームへ移動する。再生中なら、移動後も再生を続ける。</summary>
    private void MovePreviewToStartKeepingPlayback()
    {
        var wasPlaying = _previewIsPlaying;
        MovePreviewToBoundary(false);
        if (wasPlaying && _previewReady)
        {
            InvokePlayerPlay();
            _previewIsPlaying = true;
            UpdatePreviewPlaybackButtons();
        }
    }

    private void MovePreviewToBoundary(bool toEnd)
    {
        if (!_previewReady || !TestPlayerAvailable()) return;
        _previewWarmupUntil = DateTime.MinValue;
        try
        {
            InvokePlayerPause();
            _previewIsPlaying = false;
            UpdatePreviewPlaybackButtons();

            var durationMs = GetPreviewDurationMs();
            long targetMs = 0;
            var seekSpan = TimeSpan.Zero;
            var snapIndex = -1;
            if (TestFrameTableReady())
            {
                // フレーム時刻テーブルがある場合は、実在する先頭／最終フレームへ正確に移動する。
                var targetIndex = toEnd ? _frameTimesUs!.Length - 1 : 0;
                targetMs = GetFrameFloorMs(targetIndex);
                seekSpan = GetFrameSeekTimeSpan(targetIndex);
                snapIndex = targetIndex;
            }
            else if (toEnd)
            {
                var fps = _previewFrameRate;
                if (fps <= 0) fps = 30.0;
                if (durationMs > 0)
                {
                    var lastFrameIndex = (long)Math.Max(0.0, Math.Floor((durationMs - 1) * fps / 1000.0));
                    targetMs = (long)Math.Round(lastFrameIndex * 1000.0 / fps, MidpointRounding.AwayFromZero);
                    targetMs = Math.Min(Math.Max(0L, durationMs - 1), targetMs);
                }
                seekSpan = TimeSpan.FromMilliseconds(targetMs);
            }

            _previewAtEnd = false;
            _previewLogicalPositionMs = targetMs;
            _previewPendingSeekMs = targetMs;
            _previewSeekPendingUntil = DateTime.Now.AddMilliseconds(1200);
            _previewSeeking = false;
            _popupPreviewSeeking = false;
            SetPlayerPosition(seekSpan);
            if (snapIndex >= 0) ArmPreviewLandingSnap(snapIndex);
            if (durationMs > 0)
            {
                var sliderValue = (int)Math.Round(targetMs / (double)durationMs * 10000.0);
                sliderValue = Math.Clamp(sliderValue, 0, 10000);
                if (_seekTrackBar != null) _seekTrackBar.Value = sliderValue;
                if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = sliderValue;
                if (_clipSeekTrackBar != null) _clipSeekTrackBar.Value = sliderValue;
            }
            UpdatePreviewPositionLabel();
        }
        catch (Exception ex)
        {
            WriteLog($"先頭／末尾へのシークエラー: {ex.Message}");
        }
    }

    private long GetPreviewCurrentPositionMs()
    {
        if (!_previewReady) return 0;
        if (_previewAtEnd && GetPreviewDurationMs() > 0) return GetPreviewDurationMs();
        if (_previewIsPlaying)
        {
            try
            {
                UpdatePlayerPositionNow();
                var positionMs = Math.Max(0L, (long)Math.Round(GetPlayerPositionTimeSpan().TotalMilliseconds));
                var durationMs = GetPreviewDurationMs();
                if (durationMs > 0) positionMs = Math.Min(durationMs, positionMs);
                if (TestFrameTableReady())
                {
                    var frameIndex = FindFrameIndexAtOrBefore(GetPreviewPositionUs());
                    if (frameIndex >= 0) positionMs = GetFrameFloorMs(frameIndex);
                }
                _previewLogicalPositionMs = positionMs;
                return positionMs;
            }
            catch { /* 取れなければ論理位置を返す */ }
        }
        var logical = Math.Max(0L, _previewLogicalPositionMs);
        var dur = GetPreviewDurationMs();
        if (dur > 0) logical = Math.Min(Math.Max(0L, dur - 1), logical);
        return logical;
    }

    // シークのあと、実際に表示されたフレームへ位置を合わせるための待ち受けを登録する。
    // 合わせ直すためのシークはせず、mpv の着地を読み戻して表示と位置をそちらへ合わせる。
    private void ArmPreviewLandingSnap(int targetIndex, int stepFromIndex = -1, int stepDirection = 0, bool keepStepContext = false)
    {
        if (!keepStepContext)
        {
            _previewSnapStepFromIndex = stepFromIndex;
            _previewSnapStepDirection = stepDirection;
            _previewSnapEscapeIndex = -1;
            _previewSnapEscapeCount = 0;
        }
        if (!TestFrameTableReady() || targetIndex < 0)
        {
            _previewSnapTargetIndex = -1;
            return;
        }
        _previewSnapTargetIndex = targetIndex;
        _previewSnapArmTime = DateTime.Now;
        _previewSnapDeadline = DateTime.Now.AddMilliseconds(5000);
    }

    // 3 つのシークバーの位置を指定した再生位置へ合わせる。
    private void UpdatePreviewSeekBarValues(long positionMs)
    {
        var durationMs = GetPreviewDurationMs();
        if (durationMs <= 0) return;
        var sv = (int)Math.Round(positionMs / (double)durationMs * 10000.0);
        sv = Math.Clamp(sv, 0, 10000);
        if (_seekTrackBar != null) _seekTrackBar.Value = sv;
        if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = sv;
        if (_clipSeekTrackBar != null) _clipSeekTrackBar.Value = sv;
    }

    // 待ち受け中はプレビューのタイマーから定期的に呼ばれ、シークが終わっていたら
    // 実際に表示されているフレームを読み戻し、狙いとずれていれば位置をそちらへ合わせる。
    private void UpdatePreviewLandingSnap()
    {
        if (_previewSnapTargetIndex < 0) return;
        if (!_previewReady || !TestPlayerAvailable() || _previewIsPlaying || _previewAtEnd)
        {
            _previewSnapTargetIndex = -1;
            return;
        }
        var now = DateTime.Now;
        if (now > _previewSnapDeadline)
        {
            _previewSnapTargetIndex = -1;
            return;
        }
        // 連続したシークの途中では判定しない (最後のシークから少し待つ)。
        if ((now - _previewSnapArmTime).TotalMilliseconds < 250) return;
        try
        {
            // シークの実行中は time-pos が狙いの値を返すため、終わるまで着地を判定しない。
            var reply = SendMpvCommand(new object[] { "get_property", "seeking" }, true, 150);
            if (reply is not { } r || !ReplyBoolData(r, out var seeking) || seeking) return;
            UpdatePlayerPositionNow();
            var landedIndex = FindFrameIndexAtOrBefore(GetPreviewPositionUs());
            if (landedIndex < 0)
            {
                _previewSnapTargetIndex = -1;
                return;
            }
            var targetIndex = _previewSnapTargetIndex;
            _previewSnapTargetIndex = -1;
            var stepFromIndex = _previewSnapStepFromIndex;
            var stepDirection = _previewSnapStepDirection;
            var escapeCount = _previewSnapEscapeCount;
            if (landedIndex == targetIndex)
            {
                _previewSnapStepFromIndex = -1;
                _previewSnapStepDirection = 0;
                _previewSnapEscapeIndex = -1;
                _previewSnapEscapeCount = 0;
                if (escapeCount > 0)
                {
                    // キーフレームへの試行の末に狙いへ着地した。試行中は凍結していた表示を、
                    // ここで着地したフレームへ合わせる。
                    var keyLandedMs = GetFrameFloorMs(landedIndex);
                    _previewLogicalPositionMs = keyLandedMs;
                    _previewPendingSeekMs = keyLandedMs;
                    UpdatePreviewSeekBarValues(keyLandedMs);
                }
                return;
            }
            // フレーム移動で表示が動かなかった (着地が移動元と同じか、移動と逆側) ときは、
            // 表示できる前後のキーフレームへ移動する。キーフレームへのシーク自体が行き過ぎて
            // 着地が動かないこともあるため、着地が動くまで 1 つずつ先のキーフレームを試す。
            if (stepFromIndex >= 0 && stepDirection != 0 &&
                ((stepDirection < 0 && landedIndex >= stepFromIndex) || (stepDirection > 0 && landedIndex <= stepFromIndex)))
            {
                var baseIndex = _previewSnapEscapeIndex >= 0 ? _previewSnapEscapeIndex : stepFromIndex;
                var keyIndex = stepDirection < 0 ? FindPreviousKeyframeIndex(baseIndex) : FindNextKeyframeIndex(baseIndex);
                if (keyIndex >= 0 && _previewSnapEscapeCount < 6)
                {
                    _previewSnapEscapeIndex = keyIndex;
                    _previewSnapEscapeCount++;
                    // 試している間は位置表示を動かさない。論理位置とシークバーは移動の狙いのまま
                    // 保持し、着地が確定したときにだけ、まとめて実際のフレームへ合わせる。
                    SetPlayerPosition(GetFrameSeekTimeSpan(keyIndex));
                    WriteLog($"フレーム移動で表示が動かなかったため、{(stepDirection < 0 ? "前の" : "次の")}キーフレームへ移動しました ({stepFromIndex}f → {keyIndex}f)。");
                    ArmPreviewLandingSnap(keyIndex, keepStepContext: true);
                    return;
                }
            }
            _previewSnapStepFromIndex = -1;
            _previewSnapStepDirection = 0;
            _previewSnapEscapeIndex = -1;
            _previewSnapEscapeCount = 0;
            var landedMs = GetFrameFloorMs(landedIndex);
            _previewLogicalPositionMs = landedMs;
            _previewPendingSeekMs = landedMs;
            UpdatePreviewSeekBarValues(landedMs);
            WriteLog($"プレビューの位置を表示されているフレームに合わせました (狙い {targetIndex}f / 表示 {landedIndex}f)。");
        }
        catch { /* 読み戻しに失敗しても表示はそのままにする */ }
    }

    private void UpdatePreviewLabelsForSeek(SeekTrackBar trackBar)
    {
        if (!_previewReady || trackBar == null) return;
        var durationMs = GetPreviewDurationMs();
        if (durationMs <= 0) return;
        var fraction = (double)trackBar.Value / trackBar.Maximum;
        var targetMs = Math.Clamp((long)Math.Round(durationMs * fraction), 0L, durationMs);
        // 先頭フレーム以下は、ファイルの先頭 (0) として表示する (シーク確定時と同じ扱い)。
        if (TestFrameTableReady() && FindFrameIndexAtOrBefore(targetMs * 1000L) <= 0) targetMs = 0;
        _previewLogicalPositionMs = targetMs;
        var frameInfo = GetPreviewFrameInfoText(targetMs);
        var labelText = $"{TimeText.FormatClockFromMs(targetMs)} / {TimeText.FormatClockFromMs(durationMs)}" + (frameInfo.Length > 0 ? $"  {frameInfo}" : "");
        _previewPositionLabel.Text = labelText;
        if (_popupPositionLabel != null) _popupPositionLabel.Text = labelText;
        if (_clipPositionLabel != null) _clipPositionLabel.Text = labelText;
        UpdateKeyframeFlankLabels(targetMs);
    }

    private void SetPreviewPositionFromTrackBar(SeekTrackBar trackBar)
    {
        if (!_previewReady || !TestPlayerAvailable() || trackBar == null) return;
        _previewWarmupUntil = DateTime.MinValue;
        var durationMs = GetPreviewDurationMs();
        if (durationMs <= 0) return;
        try
        {
            var fraction = (double)trackBar.Value / trackBar.Maximum;
            var targetMs = Math.Clamp((long)Math.Round(durationMs * fraction), 0L, durationMs);
            var seekSpan = TimeSpan.FromMilliseconds(targetMs);
            var snapIndex = -1;
            if (targetMs < durationMs && TestFrameTableReady())
            {
                // フレームの位置にぴったり合わせ、表示時刻をそのフレーム自身の時刻にする。
                // ただし先頭フレームは、読み込み直後の状態に合わせ、ファイルの先頭 (0) として扱う
                // (表示されるフレームは先頭フレームのまま)。
                var frameIndex = FindFrameIndexAtOrBefore(targetMs * 1000L);
                if (frameIndex < 0) frameIndex = 0;
                targetMs = GetFrameFloorMs(frameIndex);
                seekSpan = GetFrameSeekTimeSpan(frameIndex);
                snapIndex = frameIndex;
            }
            _previewLogicalPositionMs = targetMs;
            _previewPendingSeekMs = targetMs;
            _previewSeekPendingUntil = DateTime.Now.AddMilliseconds(1200);
            if (targetMs >= durationMs)
            {
                InvokePlayerPause();
                _previewIsPlaying = false;
                _previewAtEnd = true;
            }
            else
            {
                _previewAtEnd = false;
            }
            SetPlayerPosition(seekSpan);
            if (snapIndex >= 0) ArmPreviewLandingSnap(snapIndex);
            UpdatePreviewPlaybackButtons();
            UpdatePreviewPositionLabel();
        }
        catch (Exception ex)
        {
            WriteLog($"シークエラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 指定したフレームへ移動するダイアログを出す。フレーム番号は位置表示と同じ 0 始まりで、
    /// 現在のフレームを初期値にする。移動はシークバーの操作と同じ経路で行う。
    /// </summary>
    private void ShowGoToFrameDialog()
    {
        if (!_previewReady || !TestFrameTableReady()) return;
        var lastIndex = _frameTimesUs!.Length - 1;
        var currentIndex = FindFrameIndexAtOrBefore(GetPreviewPositionUs());
        if (currentIndex < 0) currentIndex = 0;

        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = "指定したフレームへ移動",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 100),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        Theme.ApplyDarkTitleBar(dialog);

        dialog.Controls.Add(Theme.MakeLabel($"フレーム番号 (0 ～ {lastIndex})", 16, 12, 328, 24));
        var frameBox = Theme.MakeTextBox(16, 42, 140, 28);
        frameBox.Text = currentIndex.ToString();
        frameBox.SelectAll();
        dialog.Controls.Add(frameBox);

        var targetIndex = -1;
        var moveButton = Theme.MakeButton("移動", 164, 41, 76, 30);
        moveButton.Click += (_, _) =>
        {
            if (!int.TryParse(frameBox.Text.Trim(), out var index))
            {
                frameBox.SelectAll();
                frameBox.Focus();
                return;
            }
            targetIndex = Math.Clamp(index, 0, lastIndex);
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(moveButton);
        var cancelButton = Theme.MakeButton("キャンセル", 248, 41, 96, 30);
        cancelButton.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = moveButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => frameBox.Focus();

        if (dialog.ShowDialog(this) == DialogResult.OK && targetIndex >= 0)
        {
            GoToPreviewFrame(targetIndex);
        }
    }

    /// <summary>指定したフレームへプレビューを移動する。再生中なら一時停止してから移動する。</summary>
    private void GoToPreviewFrame(int frameIndex)
    {
        if (!_previewReady || !TestFrameTableReady()) return;
        frameIndex = Math.Clamp(frameIndex, 0, _frameTimesUs!.Length - 1);
        if (_previewIsPlaying)
        {
            try { InvokePlayerPause(); } catch { }
            _previewIsPlaying = false;
        }
        SeekPreviewToMs(GetFrameFloorMs(frameIndex));
    }

    /// <summary>
    /// 指定した位置 (ms) へプレビューをシークする。シークバーの表示も追随させる。
    /// 開始・終了欄の値はフレームの位置をミリ秒へ切り捨てたものなので、合わせ先は
    /// その値以後で最初のフレームにする (その値以前のフレームに合わせると、
    /// 欄に表示されたフレームより 1 つ前のフレームへ飛んでしまう)。
    /// </summary>
    private void SeekPreviewToMs(long requestedMs)
    {
        if (!_previewReady || !TestPlayerAvailable()) return;
        _previewWarmupUntil = DateTime.MinValue;
        var durationMs = GetPreviewDurationMs();
        if (durationMs <= 0) return;
        try
        {
            var targetMs = Math.Clamp(requestedMs, 0L, durationMs);
            var seekSpan = TimeSpan.FromMilliseconds(targetMs);
            var snapIndex = -1;
            if (targetMs < durationMs && TestFrameTableReady())
            {
                // フレームの位置にぴったり合わせ、表示時刻をそのフレーム自身の時刻にする。
                // ただし先頭フレームは、読み込み直後の状態に合わせ、ファイルの先頭 (0) として扱う
                // (表示されるフレームは先頭フレームのまま)。
                var frameIndex = FindFrameIndexAtOrAfter(targetMs * 1000L);
                if (frameIndex < 0) frameIndex = 0;
                targetMs = GetFrameFloorMs(frameIndex);
                seekSpan = GetFrameSeekTimeSpan(frameIndex);
                snapIndex = frameIndex;
            }
            _previewLogicalPositionMs = targetMs;
            _previewPendingSeekMs = targetMs;
            _previewSeekPendingUntil = DateTime.Now.AddMilliseconds(1200);
            _previewSeeking = false;
            _popupPreviewSeeking = false;
            if (targetMs >= durationMs)
            {
                InvokePlayerPause();
                _previewIsPlaying = false;
                _previewAtEnd = true;
            }
            else
            {
                _previewAtEnd = false;
            }
            SetPlayerPosition(seekSpan);
            if (snapIndex >= 0) ArmPreviewLandingSnap(snapIndex);
            var sliderValue = (int)Math.Round(targetMs / (double)durationMs * 10000.0);
            sliderValue = Math.Clamp(sliderValue, 0, 10000);
            if (_seekTrackBar != null) _seekTrackBar.Value = sliderValue;
            if (_popupSeekTrackBar != null) _popupSeekTrackBar.Value = sliderValue;
            if (_clipSeekTrackBar != null) _clipSeekTrackBar.Value = sliderValue;
            UpdatePreviewPlaybackButtons();
            UpdatePreviewPositionLabel();
        }
        catch (Exception ex)
        {
            WriteLog($"シークエラー: {ex.Message}");
        }
    }

    // シークバー上の操作: 矢印キーとマウスホイールで 1 フレームずつ移動する。
    // (ホイールの方向は、上が戻る・下が進む)
    private void HandleSeekTrackBarKeyDown(KeyEventArgs e)
    {
        var direction = e.KeyCode switch
        {
            // 上下キーは左右とは逆向き (↑ で戻り、↓ で進む)。上下は 5 フレーム、左右は 1 フレーム。
            Keys.Right => 1,
            Keys.Down => 5,
            Keys.Left => -1,
            Keys.Up => -5,
            _ => 0,
        };
        if (direction == 0) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (_previewReady) MovePreviewFrame(direction);
    }

    private void HandleSeekTrackBarKeyUp(SeekTrackBar trackBar, KeyEventArgs e)
    {
        // 矢印キーは KeyDown で 1 フレーム移動済み。ここで再シークすると 1 フレーム戻ってしまうため行わない。
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down) return;
        SetPreviewPositionFromTrackBar(trackBar);
    }

    private void HandleSeekTrackBarMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        _seekWheelRemainder += e.Delta;
        while (_seekWheelRemainder >= 120)
        {
            _seekWheelRemainder -= 120;
            if (_previewReady) MovePreviewFrame(-1);
        }
        while (_seekWheelRemainder <= -120)
        {
            _seekWheelRemainder += 120;
            if (_previewReady) MovePreviewFrame(1);
        }
    }

    private void UpdatePreviewPositionLabel()
    {
        if (!TestPlayerAvailable() || !_previewReady) return;
        if (_previewSeeking && _seekTrackBar != null)
        {
            UpdatePreviewLabelsForSeek(_seekTrackBar);
            return;
        }
        if (_popupPreviewSeeking && _popupSeekTrackBar != null)
        {
            UpdatePreviewLabelsForSeek(_popupSeekTrackBar);
            return;
        }
        if (_clipPreviewSeeking && _clipSeekTrackBar != null)
        {
            UpdatePreviewLabelsForSeek(_clipSeekTrackBar);
            return;
        }
        try
        {
            var durationMs = GetPreviewDurationMs();
            long positionMs;
            if (_previewAtEnd && durationMs > 0)
            {
                positionMs = durationMs;
            }
            else if (_previewIsPlaying)
            {
                var actualMs = Math.Max(0L, (long)Math.Round(GetPlayerPositionTimeSpan().TotalMilliseconds));
                // デコーダーが位置を報告するまで、要求したシーク時刻を表示し続ける (古い時刻のちらつき防止)。
                if (_previewPendingSeekMs >= 0)
                {
                    var frameTolerance = Math.Max(80L, (long)Math.Ceiling(2000.0 / Math.Max(1.0, _previewFrameRate)));
                    if (Math.Abs(actualMs - _previewPendingSeekMs) <= frameTolerance)
                    {
                        _previewPendingSeekMs = -1;
                        _previewLogicalPositionMs = actualMs;
                    }
                    else if (DateTime.Now < _previewSeekPendingUntil)
                    {
                        actualMs = _previewPendingSeekMs;
                    }
                    else
                    {
                        _previewPendingSeekMs = -1;
                        _previewLogicalPositionMs = actualMs;
                    }
                }
                else
                {
                    _previewLogicalPositionMs = actualMs;
                }
                positionMs = _previewLogicalPositionMs;
                if (durationMs > 0) positionMs = Math.Min(durationMs, positionMs);
            }
            else
            {
                positionMs = Math.Max(0L, _previewLogicalPositionMs);
                if (_previewPendingSeekMs >= 0 && DateTime.Now < _previewSeekPendingUntil)
                {
                    positionMs = _previewPendingSeekMs;
                }
                else if (_previewPendingSeekMs >= 0)
                {
                    // 論理位置を保持する (一時停止中はデコーダーが位置を更新しないことがある)。
                    _previewPendingSeekMs = -1;
                }
                if (durationMs > 0) positionMs = Math.Min(durationMs, positionMs);
            }
            var durationText = durationMs > 0 ? TimeText.FormatClockFromMs(durationMs) : "--:--:--.---";
            var frameInfo = GetPreviewFrameInfoText(positionMs);
            var labelText = $"{TimeText.FormatClockFromMs(positionMs)} / {durationText}" + (frameInfo.Length > 0 ? $"  {frameInfo}" : "");
            _previewPositionLabel.Text = labelText;
            if (_popupPositionLabel != null) _popupPositionLabel.Text = labelText;
            if (_clipPositionLabel != null) _clipPositionLabel.Text = labelText;
            if (durationMs > 0)
            {
                var sliderValue = Math.Clamp((int)Math.Round(positionMs / (double)durationMs * 10000.0), 0, 10000);
                if (_seekTrackBar != null && _seekTrackBar.Value != sliderValue) _seekTrackBar.Value = sliderValue;
                if (_popupSeekTrackBar != null && _popupSeekTrackBar.Value != sliderValue) _popupSeekTrackBar.Value = sliderValue;
                if (_clipSeekTrackBar != null && _clipSeekTrackBar.Value != sliderValue) _clipSeekTrackBar.Value = sliderValue;
            }
            UpdateKeyframeFlankLabels(positionMs);
        }
        catch { /* ラベル更新の失敗は無視する */ }
    }

    // ------------------------------------------------------------------
    // 音量
    // ------------------------------------------------------------------

    private void UpdatePreviewMuteButtons()
    {
        var hint = _previewMuted ? "ミュート解除" : "ミュート";
        foreach (var button in new[] { _volumeMuteButton, _popupMuteButton })
        {
            if (button == null) continue;
            button.Muted = _previewMuted;
            button.BackColor = _previewMuted ? Color.FromArgb(104, 49, 52) : Color.FromArgb(58, 54, 72);
            _volumeToolTip.SetToolTip(button, hint);
        }
    }

    /// <summary>音量バーの ↑ / ↓ を 5 ずつにする (← / → は SeekTrackBar 標準の 1 ずつ)。</summary>
    private void HandleVolumeTrackBarKeyDown(SeekTrackBar trackBar, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
        var target = Math.Clamp(trackBar.Value + (e.KeyCode == Keys.Up ? 5 : -5), trackBar.Minimum, trackBar.Maximum);
        if (target != trackBar.Value)
        {
            trackBar.Value = target;
            SetPreviewVolumeFromTrackBar(trackBar);
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void SetPreviewVolumeFromTrackBar(SeekTrackBar trackBar)
    {
        var volumeValue = Math.Clamp(trackBar.Value, 0, 100);
        _volumeSliderValue = volumeValue;
        if (volumeValue > 0)
        {
            _volumeBeforeMute = volumeValue;
            _previewMuted = false;
            SetPlayerVolume(volumeValue / 100.0);
        }
        else
        {
            // ミュート後にアイコンから元に戻せるよう、直前の 0 以外の音量を保持する。
            _previewMuted = true;
            SetPlayerVolume(0.0);
        }
        if (_volumeTrackBar != null && _volumeTrackBar.Value != volumeValue) _volumeTrackBar.Value = volumeValue;
        if (_popupVolumeTrackBar != null && _popupVolumeTrackBar.Value != volumeValue) _popupVolumeTrackBar.Value = volumeValue;
        if (_volumeValueLabel != null) _volumeValueLabel.Text = $"{volumeValue}%";
        if (_popupVolumeValueLabel != null) _popupVolumeValueLabel.Text = $"{volumeValue}%";
        UpdatePreviewMuteButtons();
    }

    private void TogglePreviewMute()
    {
        if (_previewMuted || GetPlayerVolume() <= 0.0001)
        {
            var restoreValue = Math.Clamp(_volumeBeforeMute, 1, 100);
            _previewMuted = false;
            _volumeBeforeMute = restoreValue;
            _volumeSliderValue = restoreValue;
            SetPlayerVolume(restoreValue / 100.0);
            if (_volumeTrackBar != null) _volumeTrackBar.Value = restoreValue;
            if (_popupVolumeTrackBar != null) _popupVolumeTrackBar.Value = restoreValue;
            if (_volumeValueLabel != null) _volumeValueLabel.Text = $"{restoreValue}%";
            if (_popupVolumeValueLabel != null) _popupVolumeValueLabel.Text = $"{restoreValue}%";
        }
        else
        {
            var currentValue = (int)Math.Round(GetPlayerVolume() * 100);
            if (currentValue > 0) _volumeBeforeMute = currentValue;
            _previewMuted = true;
            SetPlayerVolume(0.0);
        }
        UpdatePreviewMuteButtons();
        SaveSettingsFromUi();
    }

    /// <summary>「新規」や読み込み直しでプレビューを初期状態へ戻す。</summary>
    private void ResetPreviewState()
    {
        if (_previewPopupForm is { IsDisposed: false })
        {
            try { _previewPopupForm.Close(); } catch { }
        }
        try { StopMpvPlayer(); } catch { }
        _previewReady = false;
        _previewIsPlaying = false;
        _previewAtEnd = false;
        _previewLogicalPositionMs = 0;
        _previewPendingSeekMs = -1;
        _previewSeekPendingUntil = DateTime.MinValue;
        _previewDurationMs = 0;
        _previewSeeking = false;
        _popupPreviewSeeking = false;
        _clipPreviewSeeking = false;
        UpdatePreviewPlaybackButtons();
        if (_seekTrackBar != null) { try { _seekTrackBar.Value = 0; } catch { } }
        if (_previewPositionLabel != null) _previewPositionLabel.Text = "--:--:--.--- / --:--:--.---";
    }
}
