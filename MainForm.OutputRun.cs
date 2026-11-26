using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// FFmpeg ジョブの実行 (計画・進捗・キャンセル・出力検証)、
/// 引数ビルダー、編集情報メタデータ、「カット以外の編集」。
/// </summary>
internal sealed partial class MainForm
{
    private sealed class JobStep
    {
        public string Description = "";
        public string[] Arguments = Array.Empty<string>();
        public string VerifyPath = "";
        public long ExpectedDurationMs;
        public string VerificationKind = "";
        public long SourceDurationMs;
        // 番組情報の移植: 無劣化カットの TS 出力に対して、元ファイルの番組情報を後処理で合流させる。
        public string ProgramInfoSourcePath = "";
        public long ProgramInfoStartMs;
        public long ProgramInfoEndMs;
        // ステップの実行中だけ必要な一時ファイル (チャプターの ffmetadata など)。
        // ステップ開始時に書き出し、終了時に消す。
        public List<(string Path, string Content)> TempFileContents = new();
    }

    private bool _jobRunning;
    private List<JobStep> _currentPlan = new();
    private int _planIndex;
    private Process? _currentProcess;
    private Task<string>? _currentErrorReadTask;
    private bool _cancelRequested;
    private string _finalOutput = "";
    private string _cleanupFolder = "";
    private string _lastProcessCommand = "";
    private string _currentStepDescription = "";
    private string _lastProcessErrorTail = "";
    private System.Windows.Forms.Timer? _jobTimer;
    private DateTime _jobStartedAt;
    private long _jobTotalExpectedMs;
    private long _jobCompletedExpectedMs;
    private long _currentStepProgressMs;
    private double _smoothedRemainingMs = -1;
    private Form? _outputLogPopup;
    private TextBox? _outputLogPopupBox;
    private bool _outputLogCapture;
    // ポップアップを閉じても再表示できるよう、その出力の間のログは別に保持する。
    private readonly List<string> _outputLogLines = new();
    private bool _outputLogPopupSession;

    // コマンドライン表示のためにプランを組み立てるときの取り込み先。
    // この間は、確認ダイアログを出さず、ログも書かず、StartPlan で実行せずにステップだけを受け取る。
    private bool _previewingCommands;
    private bool _previewAsterCut;
    private List<JobStep>? _capturedCommandSteps;
    private string _capturedCommandError = "";

    private string GetFFmpegPath()
    {
        var p = Environment.ExpandEnvironmentVariables(_ffmpegPath.Trim().Trim('"'));
        if (p.Length == 0 || !File.Exists(p))
        {
            throw new InvalidOperationException("ffmpeg.exe が見つかりません。「ツール」メニューの「オプション」でパスを設定してください。");
        }
        return Path.GetFullPath(p);
    }

    private bool TestTrimSupport() => _trimSupported;

    // ------------------------------------------------------------------
    // プロセス引数と出力先の確認
    // ------------------------------------------------------------------

    private static string QuoteProcessArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string ConvertToProcessArgumentLine(string[] arguments) =>
        string.Join(" ", arguments.Select(QuoteProcessArgument));

    private string? ConfirmOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("出力先を指定してください。");
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        if (_inputPath.Length == 0) throw new InvalidOperationException("入力動画が設定されていません。");
        var inputFull = Path.GetFullPath(_inputPath);
        var samePath = full.Equals(inputFull, StringComparison.OrdinalIgnoreCase);
        if (samePath)
        {
            MessageBox.Show($"警告: 出力先が入力動画と同じファイルです。上書きを防ぐため処理を中止します。\r\n\r\n入力・出力:\r\n{full}", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            throw new InvalidOperationException("出力先が入力動画と同じパスのため、処理を中止しました。");
        }
        var parent = Path.GetDirectoryName(full) ?? "";
        if (!Directory.Exists(parent)) throw new InvalidOperationException("出力先フォルダーが存在しません。");
        if (File.Exists(full))
        {
            var result = MessageBox.Show($"次のファイルを上書きしますか？\r\n{full}", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return null;
        }
        WriteLog($"出力先確認: {full}");
        return full;
    }

    // ------------------------------------------------------------------
    // ジョブの実行
    // ------------------------------------------------------------------

    private void StartPlan(List<JobStep> steps, string outputPath, string tempFolder = "", bool alreadyConfirmed = false)
    {
        if (_jobRunning) return;
        try
        {
            if (_inputPath.Length == 0 || !File.Exists(_inputPath))
            {
                throw new InvalidOperationException("先に入力動画を読み込んでください。");
            }
            _ = GetFFmpegPath();
            if (_previewingCommands)
            {
                _capturedCommandSteps = steps;
                return;
            }
            // 抽出・分割は、呼び出し側で全ファイルの上書き確認まで済ませてから渡される。
            var confirmedPath = alreadyConfirmed ? outputPath : ConfirmOutputPath(outputPath);
            if (confirmedPath == null)
            {
                if (tempFolder.Length > 0 && Directory.Exists(tempFolder))
                {
                    try { Directory.Delete(tempFolder, true); } catch { }
                }
                return;
            }
            _finalOutput = confirmedPath;
            _cleanupFolder = tempFolder;
            _currentPlan = steps;
            _planIndex = 0;
            _cancelRequested = false;
            _jobRunning = true;
            _cancelButton.Enabled = true;
            StartJobDisplay(steps);
            OpenOutputLogPopup();
            if (_outputRunButton != null) _outputRunButton.Enabled = false;
            if (_encodeRunButton != null) _encodeRunButton.Enabled = false;
            SetStatus("FFmpeg 実行中...");
            WriteLog($"処理開始: {confirmedPath}");
            WriteLog($"処理ステップ数: {_currentPlan.Count}");
            WriteLog($"FFmpeg: {GetFFmpegPath()}");
            WriteLog($"ffprobe: {GetFfprobePath()}");
            CapturePendingEditHistory(confirmedPath);
            _jobTimer ??= CreateJobTimer();
            _jobTimer.Start();
            StartCurrentPlanStep();
        }
        catch (Exception ex)
        {
            if (tempFolder.Length > 0 && Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, true); } catch { }
            }
            if (_outputLogCapture) FinishOutputLogPopup(false);
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("処理を開始できませんでした");
        }
    }

    private System.Windows.Forms.Timer CreateJobTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += async (_, _) => await OnJobTimerTickAsync();
        return timer;
    }

    private void StartCurrentPlanStep()
    {
        if (_planIndex >= _currentPlan.Count)
        {
            FinishPlan(true, "すべての処理が完了しました。");
            return;
        }
        var step = _currentPlan[_planIndex];
        try
        {
            foreach (var (tempPath, tempContent) in step.TempFileContents)
            {
                File.WriteAllText(tempPath, tempContent, new UTF8Encoding(false));
            }
            var exe = GetFFmpegPath();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = ConvertToProcessArgumentLine(step.Arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
            };
            _lastProcessCommand = "\"" + exe + "\" " + psi.Arguments;
            _currentStepDescription = step.Description;
            var stepIndex = _planIndex;
            Interlocked.Exchange(ref _currentStepProgressMs, 0L);
            _lastProcessErrorTail = "";
            WriteLog("コマンド: " + _lastProcessCommand);
            var process = new Process { StartInfo = psi };
            WriteLog($"[{_planIndex + 1}/{_currentPlan.Count}] {step.Description}");
            if (!process.Start()) throw new InvalidOperationException("FFmpeg プロセスを起動できませんでした。");
            _currentErrorReadTask = ReadStepErrorStreamAsync(process.StandardError, stepIndex);
            _currentProcess = process;
        }
        catch (Exception ex)
        {
            DeleteStepTempFiles(step);
            WriteLog($"起動エラー: {ex.Message}");
            FinishPlan(false, "FFmpeg の起動に失敗しました。");
        }
    }

    private static void DeleteStepTempFiles(JobStep step)
    {
        foreach (var (tempPath, _) in step.TempFileContents)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private async Task OnJobTimerTickAsync()
    {
        if (_jobRunning) UpdateJobTimeDisplay();
        var process = _currentProcess;
        if (process == null) return;
        bool exited;
        try { exited = process.HasExited; } catch { exited = true; }
        if (!exited) return;

        var code = 1;
        var stderrText = "";
        try { code = process.ExitCode; } catch { }
        if (_currentErrorReadTask != null)
        {
            try { stderrText = await _currentErrorReadTask; } catch { }
        }
        if (!string.IsNullOrWhiteSpace(stderrText))
        {
            var errorLines = stderrText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            _lastProcessErrorTail = string.Join("\r\n", errorLines.TakeLast(14));
            WriteLogRange(errorLines.Select(line => line.TrimEnd()).ToArray());
        }
        else
        {
            _lastProcessErrorTail = "(FFmpeg の標準エラー出力は空でした)";
        }
        try { process.Dispose(); } catch { }
        _currentProcess = null;
        _currentErrorReadTask = null;
        if (_planIndex < _currentPlan.Count) DeleteStepTempFiles(_currentPlan[_planIndex]);
        if (_cancelRequested)
        {
            FinishPlan(false, "処理はユーザーによってキャンセルされました。");
            return;
        }
        if (code != 0)
        {
            WriteLog($"FFmpeg 終了コード: {code}");
            WriteLog($"失敗した処理: {_currentStepDescription}");
            FinishPlan(false, $"FFmpeg がエラー終了しました（終了コード {code}）。");
            return;
        }
        var completedStep = _currentPlan[_planIndex];
        _jobCompletedExpectedMs += Math.Max(0, completedStep.ExpectedDurationMs);
        if (completedStep.ProgramInfoSourcePath.Length > 0)
        {
            try
            {
                PreserveProgramInfo(completedStep.ProgramInfoSourcePath, completedStep.VerifyPath, completedStep.ProgramInfoStartMs, completedStep.ProgramInfoEndMs);
            }
            catch (Exception ex)
            {
                WriteLog($"番組情報の移植に失敗しました (出力はそのまま使います): {ex.Message}");
            }
        }
        if (completedStep.VerifyPath.Length > 0)
        {
            try
            {
                var verified = await Probe.InvokeAsync(GetFfprobePath(), completedStep.VerifyPath);
                var actualMs = GetProbeDurationMs(verified);
                var expectedMs = completedStep.ExpectedDurationMs;
                WriteLog($"検証 ({completedStep.VerificationKind}): ファイル={completedStep.VerifyPath}; 期待時間={TimeText.FormatClockFromMs(expectedMs)}; 実測時間={TimeText.FormatClockFromMs(actualMs)}");
                if (actualMs <= 0) throw new InvalidOperationException("ffprobe から有効な出力時間を取得できませんでした。");
                var toleranceMs = (long)Math.Max(1200, Math.Min(3000, expectedMs * 0.05));
                var durationDifferenceMs = Math.Abs(actualMs - expectedMs);
                if (durationDifferenceMs > toleranceMs)
                {
                    var why = $"出力時間が指定範囲と一致しないため、この段階で停止しました。範囲のカットが反映されていない可能性があります。期待={TimeText.FormatClockFromMs(expectedMs)}, 実測={TimeText.FormatClockFromMs(actualMs)}, 差={durationDifferenceMs}ms, 許容差={toleranceMs}ms";
                    WriteLog($"検証失敗: {why}");
                    FinishPlan(false, why);
                    return;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"出力検証エラー: {ex.Message}");
                FinishPlan(false, $"出力ファイルの検証に失敗しました: {ex.Message}");
                return;
            }
        }
        WriteLog($"ステップ成功: {_currentStepDescription}; exit code=0");
        _planIndex++;
        if (_planIndex < _currentPlan.Count) StartCurrentPlanStep();
        else FinishPlan(true, "すべての処理が完了し、出力時間の検証も完了しました。");
    }

    private void FinishPlan(bool success, string message)
    {
        _jobTimer?.Stop();
        if (_currentProcess != null)
        {
            try { _currentProcess.Dispose(); } catch { }
            _currentProcess = null;
        }
        _currentErrorReadTask = null;
        _jobRunning = false;
        StopJobDisplay();
        _cancelButton.Enabled = false;
        if (_outputRunButton != null) _outputRunButton.Enabled = _probe != null;
        if (_encodeRunButton != null) _encodeRunButton.Enabled = _probe != null;
        if (_cleanupFolder.Length > 0 && Directory.Exists(_cleanupFolder))
        {
            try { Directory.Delete(_cleanupFolder, true); } catch { }
        }
        _cleanupFolder = "";
        if (success) SavePendingEditHistory();
        else _pendingEditHistoryEntry = null;
        if (success)
        {
            WriteLog(message);
            SetStatus("完了");
        }
        else
        {
            WriteLog(message);
            SetStatus("失敗またはキャンセル");
        }
        FinishOutputLogPopup(success);
    }

    private static readonly Regex FfmpegTimePattern = new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    // FFmpeg の標準エラー出力を読み切りながら、ステータス行の time= から現在のステップの処理済み時間を取り出す (終了予測の計算に使う)。
    private async Task<string> ReadStepErrorStreamAsync(StreamReader reader, int stepIndex)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var tail = "";
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            var chunk = new string(buffer, 0, read);
            builder.Append(chunk);
            tail += chunk;
            if (tail.Length > 512) tail = tail[^512..];
            var matches = FfmpegTimePattern.Matches(tail);
            if (matches.Count > 0)
            {
                var match = matches[^1];
                var ms = (long)(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600000
                    + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60000
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 1000);
                if (_jobRunning && _planIndex == stepIndex)
                {
                    Interlocked.Exchange(ref _currentStepProgressMs, ms);
                }
            }
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // 出力中の下部バー表示 (アニメーション・経過時間・終了予測)
    // ------------------------------------------------------------------

    private void StartJobDisplay(List<JobStep> steps)
    {
        _jobStartedAt = DateTime.Now;
        _jobTotalExpectedMs = steps.Sum(step => Math.Max(0, step.ExpectedDurationMs));
        _jobCompletedExpectedMs = 0;
        Interlocked.Exchange(ref _currentStepProgressMs, 0L);
        _smoothedRemainingMs = -1;
        _jobAnimation.Visible = true;
        _jobElapsedLabel.Text = "経過 00:00";
        _jobEtaLabel.Text = "終了予測 ―";
        LayoutBottomBar();
    }

    private void StopJobDisplay()
    {
        _jobAnimation.Visible = false;
        _jobElapsedLabel.Text = "";
        _jobEtaLabel.Text = "";
        _smoothedRemainingMs = -1;
        LayoutBottomBar();
    }

    // ------------------------------------------------------------------
    // 出力中の実行ログポップアップ (その出力の開始から終了までのログだけを表示する)
    // ------------------------------------------------------------------

    private void EnsureOutputLogPopup()
    {
        if (_outputLogPopup == null || _outputLogPopup.IsDisposed)
        {
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                BackColor = Color.FromArgb(23, 22, 29),
                ForeColor = Color.FromArgb(203, 198, 216),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                Dock = DockStyle.Fill,
            };
            Theme.ApplyDarkScrollBars(box);
            var popup = new Form
            {
                Icon = AppIcon.Value,
                Text = "動画出力中",
                Size = new Size(940, 550),
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = Theme.PageBack,
                AutoScaleMode = AutoScaleMode.None,
            };
            popup.Controls.Add(box);
            popup.FormClosed += (_, _) =>
            {
                _outputLogPopup = null;
                _outputLogPopupBox = null;
                _outputLogCapture = false;
                try { box.Font.Dispose(); } catch { }
            };
            Theme.ApplyDarkTitleBar(popup);
            _outputLogPopup = popup;
            _outputLogPopupBox = box;
        }
    }

    private void OpenOutputLogPopup()
    {
        EnsureOutputLogPopup();
        _outputLogLines.Clear();
        _outputLogPopupBox!.Clear();
        _outputLogPopup!.Text = "動画出力中";
        // 大きさは記憶しないため、開くたびに既定の大きさへ戻す。
        _outputLogPopup.Size = new Size(940, 550);
        _outputLogCapture = true;
        _outputLogPopupSession = true;
        if (!_outputLogPopup.Visible)
        {
            _outputLogPopup.StartPosition = FormStartPosition.Manual;
            _outputLogPopup.Location = new Point(
                Math.Max(0, Location.X + (Width - _outputLogPopup.Width) / 2),
                Math.Max(0, Location.Y + (Height - _outputLogPopup.Height) / 2));
            _outputLogPopup.Show(this);
        }
        _showOutputLogButton.Visible = true;
        LayoutBottomBar();
    }

    /// <summary>出力中に閉じた (または裏に隠れた) 実行ログポップアップを再表示する。</summary>
    private void ReshowOutputLogPopup()
    {
        if (!_outputLogPopupSession) return;
        if (_outputLogPopup == null || _outputLogPopup.IsDisposed)
        {
            EnsureOutputLogPopup();
            // 作り直した場合は、保持しておいたその出力のログを復元する。
            _outputLogPopupBox!.Text = _outputLogLines.Count == 0 ? "" : string.Join("\r\n", _outputLogLines) + "\r\n";
            _outputLogPopupBox.SelectionStart = _outputLogPopupBox.TextLength;
            _outputLogPopupBox.SelectionLength = 0;
            _outputLogPopupBox.ScrollToCaret();
            _outputLogPopup!.Text = "動画出力中";
            _outputLogPopup.Size = new Size(940, 550);
            _outputLogCapture = true;
        }
        if (!_outputLogPopup!.Visible)
        {
            _outputLogPopup.StartPosition = FormStartPosition.Manual;
            _outputLogPopup.Location = new Point(
                Math.Max(0, Location.X + (Width - _outputLogPopup.Width) / 2),
                Math.Max(0, Location.Y + (Height - _outputLogPopup.Height) / 2));
            _outputLogPopup.Show(this);
        }
        _outputLogPopup.Activate();
    }

    private void AppendOutputLogPopupLine(string line)
    {
        var trimmed = false;
        if (_outputLogPopupSession)
        {
            _outputLogLines.Add(line);
            if (_outputLogLines.Count > 1500)
            {
                _outputLogLines.RemoveRange(0, _outputLogLines.Count - 1000);
                trimmed = true;
            }
        }
        if (!_outputLogCapture) return;
        var box = _outputLogPopupBox;
        if (box == null || box.IsDisposed) return;
        if (trimmed) box.Text = string.Join("\r\n", _outputLogLines) + "\r\n";
        else box.AppendText(line + "\r\n");
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.ScrollToCaret();
    }

    /// <summary>複数の行をまとめてポップアップへ書く。追記とスクロールを 1 回ずつで済ませ、1 行ごとの全文読み直しを避ける。</summary>
    private void AppendOutputLogPopupRange(IReadOnlyList<string> lines)
    {
        var trimmed = false;
        if (_outputLogPopupSession)
        {
            _outputLogLines.AddRange(lines);
            if (_outputLogLines.Count > 1500)
            {
                _outputLogLines.RemoveRange(0, _outputLogLines.Count - 1000);
                trimmed = true;
            }
        }
        if (!_outputLogCapture) return;
        var box = _outputLogPopupBox;
        if (box == null || box.IsDisposed) return;
        if (trimmed) box.Text = string.Join("\r\n", _outputLogLines) + "\r\n";
        else box.AppendText(string.Join("\r\n", lines) + "\r\n");
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.ScrollToCaret();
    }

    private void FinishOutputLogPopup(bool success)
    {
        _outputLogCapture = false;
        _outputLogPopupSession = false;
        _showOutputLogButton.Visible = false;
        LayoutBottomBar();
        var popup = _outputLogPopup;
        if (popup == null || popup.IsDisposed) return;
        if (success && _settings.AutoCloseOutputLogPopup)
        {
            popup.Close();
            return;
        }
        popup.Text = success ? "動画出力完了" : _cancelRequested ? "動画出力をキャンセルしました" : "動画出力に失敗しました";
    }

    private void UpdateJobTimeDisplay()
    {
        var elapsed = DateTime.Now - _jobStartedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var fraction = ComputeJobFraction();
        string etaText;
        if (fraction >= 0.02 && elapsed.TotalSeconds >= 3)
        {
            var remainingMs = elapsed.TotalMilliseconds * (1.0 - fraction) / fraction;
            _smoothedRemainingMs = _smoothedRemainingMs < 0 ? remainingMs : _smoothedRemainingMs * 0.65 + remainingMs * 0.35;
            var finishAt = DateTime.Now.AddMilliseconds(_smoothedRemainingMs);
            etaText = $"終了予測 あと約 {FormatJobClock(TimeSpan.FromMilliseconds(_smoothedRemainingMs))} ({finishAt:HH:mm} 終了)";
        }
        else
        {
            etaText = "終了予測 ―";
        }
        _jobElapsedLabel.Text = $"経過 {FormatJobClock(elapsed)}";
        _jobEtaLabel.Text = etaText;
        if (_outputLogPopup != null && !_outputLogPopup.IsDisposed)
        {
            _outputLogPopup.Text = $"動画出力中 - {_jobElapsedLabel.Text} / {_jobEtaLabel.Text}";
        }
    }

    private double ComputeJobFraction()
    {
        if (_currentPlan.Count == 0) return 0;
        if (_jobTotalExpectedMs > 0)
        {
            var currentExpected = _planIndex < _currentPlan.Count ? Math.Max(0, _currentPlan[_planIndex].ExpectedDurationMs) : 0;
            var current = Math.Min(Interlocked.Read(ref _currentStepProgressMs), currentExpected);
            var done = _jobCompletedExpectedMs + current;
            return Math.Min(0.999, Math.Max(0, (double)done / _jobTotalExpectedMs));
        }
        return Math.Min(0.999, Math.Max(0, (double)_planIndex / _currentPlan.Count));
    }

    private static string FormatJobClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private void CancelCurrentJob()
    {
        if (_jobRunning && _currentProcess != null)
        {
            _cancelRequested = true;
            SetStatus("キャンセル中...");
            try { _currentProcess.Kill(); } catch { }
        }
    }

    private static long GetProbeDurationMs(ProbeResult probeData)
    {
        var durationSeconds = Probe.ParseSeconds(probeData.FormatDuration);
        if (durationSeconds <= 0)
        {
            foreach (var stream in probeData.Streams)
            {
                var streamDuration = Probe.ParseSeconds(stream.Duration);
                if (streamDuration > durationSeconds) durationSeconds = streamDuration;
            }
        }
        if (durationSeconds <= 0) return 0;
        return (long)Math.Round(durationSeconds * 1000.0);
    }

    // ------------------------------------------------------------------
    // 共通の引数ビルダー
    // ------------------------------------------------------------------

    private string GetMapMetadataValue() => _keepMetadataCheckBox != null && !_keepMetadataCheckBox.Checked ? "-1" : "0";

    // ------------------------------------------------------------------
    // 単一区間で保持するデータ (サムネイル・字幕・チャプター)
    // ------------------------------------------------------------------

    private static bool IsTextSubtitleCodec(string codecName)
        => codecName.ToLowerInvariant() is "subrip" or "ass" or "ssa" or "mov_text" or "webvtt" or "text";

    /// <summary>字幕をそのコンテナへ保持するときのコーデック指定を返す。保持できない場合は null。</summary>
    private static string? GetSubtitleOutputCodec(string outputExt, ProbeStream subtitle)
    {
        var isText = IsTextSubtitleCodec(subtitle.CodecName);
        return outputExt switch
        {
            ".mkv" => "copy",
            ".mp4" or ".mov" or ".m4v" => isText ? "mov_text" : null,
            ".webm" => isText ? "webvtt" : null,
            // TS などへは字幕ストリームを保持しない (放送 TS の字幕は番組情報の移植の担当)。
            _ => null,
        };
    }

    /// <summary>字幕ストリームの先頭パケットの位置 (アプリの時刻軸の ms)。分からなければ -1。</summary>
    private long GetSubtitleStreamStartMs(ProbeStream subtitle) => subtitle.FirstPacketAppMs;

    /// <summary>
    /// 字幕ストリームを区間 [startMs, endMs) で切り出す trim BSF を作る。
    /// trim の起点は、そのストリームで最初に届くパケット (ストリームの開始位置) に合わせる。
    /// rebaseToZero の場合は、区間の開始位置が 0 になるよう setts でずらす (再エンコード経路用)。
    /// 無劣化経路ではずらさず、経路側の全体の時刻そろえ (output_ts_offset など) に合わせる。
    /// </summary>
    private string? BuildSubtitleTrimBsf(ProbeStream subtitle, long startMs, long endMs, bool rebaseToZero)
    {
        var zeroMs = GetSubtitleStreamStartMs(subtitle);
        if (zeroMs < 0) return null;
        var relStart = Math.Max(0, startMs - zeroMs);
        var duration = endMs - zeroMs - relStart;
        if (duration <= 0) return null;
        var filter = $"trim=start={relStart}:start_type=msec_pt:start_rel=1:end={duration}:end_type=dur_t_msec:trim_packets=1";
        if (rebaseToZero)
        {
            // ずらし量は秒で指定し、タイムベース単位への換算は setts の TB に任せる
            // (字幕のタイムベースは、変換先のコンテナで変わることがあるため)。
            var formatStartSec = _probe != null ? Probe.ParseSeconds(_probe.FormatStartTime) : 0;
            var shiftSec = (startMs / 1000.0 + formatStartSec).ToString("0.######", CultureInfo.InvariantCulture);
            filter += $",setts=pts=PTS-{shiftSec}/TB:dts=DTS-{shiftSec}/TB";
        }
        return filter;
    }

    /// <summary>ffmetadata の値のエスケープ。</summary>
    private static string EscapeFfmetadataValue(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '))
        {
            if (ch is '\\' or '=' or ';' or '#') builder.Append('\\');
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 区間 [startMs, endMs) に重なるチャプターを、出力の時刻軸へ直した ffmetadata を追加の入力として
    /// 差し込み、その入力番号を返す。対象のチャプターが無ければ -1。一時ファイルの内容は tempFiles へ
    /// 積む (実際の書き出しはステップ開始時に行う)。
    /// baseMs は出力の時刻軸の起点に対応する入力側の位置。再エンコード経路では区間の開始位置、
    /// 無劣化経路では出力の先頭になるフレームの位置 (MP4 系ではカット位置) を渡す。
    /// 区間の開始より前に始まるチャプターは、開始位置を区間の開始にそろえて残す。
    /// </summary>
    private int AddSegmentChaptersInput(List<string> args, long startMs, long endMs, long baseMs, List<(string Path, string Content)> tempFiles)
    {
        if (_probe == null) return -1;
        var lines = new List<string> { ";FFMETADATA1" };
        var count = 0;
        foreach (var chapter in _probe.Chapters)
        {
            if (chapter.EndMs <= startMs || chapter.StartMs >= endMs) continue;
            var start = Math.Max(startMs, chapter.StartMs) - baseMs;
            var end = Math.Min(chapter.EndMs, endMs) - baseMs;
            if (end <= start) continue;
            lines.Add("[CHAPTER]");
            lines.Add("TIMEBASE=1/1000");
            lines.Add($"START={start}");
            lines.Add($"END={end}");
            if (chapter.Title.Length > 0) lines.Add($"title={EscapeFfmetadataValue(chapter.Title)}");
            count++;
        }
        if (count == 0) return -1;
        var path = Path.Combine(Path.GetTempPath(), $"AsterCut_chapters_{Guid.NewGuid():N}.txt");
        tempFiles.Add((path, string.Join("\n", lines) + "\n"));
        var inputIndex = 0;
        var insertAt = args.Count;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-i")
            {
                inputIndex++;
                insertAt = i + 2;
            }
        }
        args.Insert(insertAt, path);
        args.Insert(insertAt, "-i");
        return inputIndex;
    }

    /// <summary>
    /// 保持する字幕・サムネイル・添付ファイルを、再エンコード経路の引数へ加える。
    /// firstVideoOutputOrdinal は、すでに出力へ割り当て済みの映像ストリームの数
    /// (サムネイルのコーデック指定の添え字に使う)。
    /// </summary>
    private void AddKeepDataToEncodeArgs(List<string> args, string outputExt, long startMs, long endMs, bool wholeFile, bool speedChanged, int firstVideoOutputOrdinal)
    {
        var keepSubtitle = IsKeepDataChecked("subtitle");
        var keepThumbnail = IsKeepDataChecked("thumbnail");
        if (keepSubtitle && !speedChanged)
        {
            var subtitles = GetSubtitleStreams();
            var outIndex = 0;
            for (var k = 0; k < subtitles.Count; k++)
            {
                var codec = GetSubtitleOutputCodec(outputExt, subtitles[k]);
                if (codec == null) continue;
                string? bsf = null;
                if (!wholeFile)
                {
                    bsf = BuildSubtitleTrimBsf(subtitles[k], startMs, endMs, rebaseToZero: true);
                    if (bsf == null) continue;
                }
                args.Add("-map");
                args.Add($"0:s:{k}");
                args.Add($"-c:s:{outIndex}");
                args.Add(codec);
                if (bsf != null)
                {
                    args.Add($"-bsf:s:{outIndex}");
                    args.Add(bsf);
                }
                outIndex++;
            }
            if (outIndex > 0) WriteLog($"字幕を保持します ({outIndex} ストリーム)。");
            else if (subtitles.Count > 0) WriteLog("字幕はこのコンテナでは保持できないため、含めません。");
        }
        if (keepThumbnail && outputExt is ".mkv" or ".mp4" or ".mov" or ".m4v")
        {
            var pics = GetAttachedPicStreams();
            for (var i = 0; i < pics.Count; i++)
            {
                args.Add("-map");
                args.Add($"0:{pics[i].Index}");
                args.Add($"-c:v:{firstVideoOutputOrdinal + i}");
                args.Add("copy");
            }
            if (pics.Count > 0) WriteLog($"サムネイルを保持します ({pics.Count} ストリーム)。");
        }
        // 添付ファイル (MKV のフォントなど) は、字幕かサムネイル (カバー画像) を保持するときに一緒に移す。
        if ((keepSubtitle || keepThumbnail) && outputExt == ".mkv" && GetAttachmentStreams().Count > 0)
        {
            args.Add("-map");
            args.Add("0:t");
            args.Add("-c:t");
            args.Add("copy");
        }
    }

    private List<ProbeStream> GetSelectedAudioStreams()
    {
        var selected = new List<ProbeStream>();
        for (var i = 0; i < _audioStreams.Count; i++)
        {
            if (i < _audioList.Items.Count && _audioList.GetItemChecked(i)) selected.Add(_audioStreams[i]);
        }
        return selected;
    }

    private string GetSelectedVideoCrf()
    {
        var raw = (_videoCrfBox?.Text ?? "23.0").Trim();
        if (raw.Length == 0 || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidOperationException("画質 (CRF) には 0 以上の数値を入力してください。小数点には半角の . を使用してください。");
        }
        var isAv1 = GetSelectedVideoFormat() == "AV1";
        var maximum = isAv1 ? 63.0 : 51.0;
        if (value < 0 || value > maximum)
        {
            throw new InvalidOperationException($"画質 (CRF) の範囲は {GetSelectedVideoFormat()} で 0～{maximum.ToString(CultureInfo.InvariantCulture)} です。");
        }
        if (isAv1 && Math.Abs(value - Math.Round(value)) > 0.000001)
        {
            throw new InvalidOperationException("AV1 の CRF は整数で指定してください。");
        }
        return value.ToString("0.################", CultureInfo.InvariantCulture);
    }

    private int GetSelectedAudioBitrateKbps()
    {
        var selected = _audioBitrateCombo?.SelectedItem?.ToString() ?? "";
        var match = Regex.Match(selected, @"^\s*(\d+)\s*kbps\s*$");
        if (!match.Success)
        {
            throw new InvalidOperationException("音質 (bit rate) の選択値が不正です。");
        }
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private List<string> GetOpusEncoderArgs(IReadOnlyList<ProbeStream> audioStreams)
    {
        // 選択したビットレートを使用する。モノラルは FFmpeg libopus の上限 256 kbps を超えないようにする。
        var requestedBitrate = GetSelectedAudioBitrateKbps();
        var result = new List<string> { "-c:a", "libopus", "-vbr", "on" };
        for (var i = 0; i < audioStreams.Count; i++)
        {
            var channels = audioStreams[i].Channels;
            var actualBitrate = channels < 2 && requestedBitrate > 256 ? 256 : requestedBitrate;
            var bitrate = actualBitrate.ToString(CultureInfo.InvariantCulture) + "k";
            result.Add($"-b:a:{i}");
            result.Add(bitrate);
            WriteLog($"Opus 音声出力 {i + 1}: 入力チャンネル数={channels} / 選択={requestedBitrate} kbps / 実適用={bitrate} / VBR=on");
        }
        return result;
    }

    private List<string> GetAacEncoderArgs(IReadOnlyList<ProbeStream> audioStreams)
    {
        // ffmpeg 標準の aac エンコーダーで LC プロファイルを使う。
        var requestedBitrate = GetSelectedAudioBitrateKbps();
        var result = new List<string> { "-c:a", "aac", "-profile:a", "aac_low" };
        for (var i = 0; i < audioStreams.Count; i++)
        {
            var bitrate = requestedBitrate.ToString(CultureInfo.InvariantCulture) + "k";
            result.Add($"-b:a:{i}");
            result.Add(bitrate);
            WriteLog($"AAC 音声出力 {i + 1}: 選択={requestedBitrate} kbps / 実適用={bitrate} / LC");
        }
        return result;
    }

    private List<string> GetFlacEncoderArgs(IReadOnlyList<ProbeStream> audioStreams)
    {
        // ffmpeg 標準の flac エンコーダーで無劣化に変換する。ビットレートの指定はない。
        for (var i = 0; i < audioStreams.Count; i++)
        {
            WriteLog($"FLAC 音声出力 {i + 1}: 無劣化");
        }
        return new List<string> { "-c:a", "flac" };
    }

    /// <summary>再エンコード時の音声エンコーダー引数 (プロファイルの音声フォーマットに従う)。</summary>
    private List<string> GetAudioEncoderArgs(IReadOnlyList<ProbeStream> audioStreams) =>
        GetSelectedAudioFormat() switch
        {
            "AAC" => GetAacEncoderArgs(audioStreams),
            "FLAC" => GetFlacEncoderArgs(audioStreams),
            _ => GetOpusEncoderArgs(audioStreams),
        };

    private string GetAudioEncoderLogText() => GetSelectedAudioFormat() switch
    {
        "AAC" => $"AAC {GetSelectedAudioBitrateKbps()} kbps / LC",
        "FLAC" => "FLAC (無劣化)",
        _ => $"Opus {GetSelectedAudioBitrateKbps()} kbps / VBR",
    };

    private string GetVideoPixFmt() => (GetSelectedVideoFormat(), GetSelectedBitDepth()) switch
    {
        ("HEVC", 12) => "yuv420p12le",
        (_, 10) => "yuv420p10le",
        _ => "yuv420p",
    };

    /// <summary>TS 出力時の GOP (キーフレーム間隔) の上限をフレーム数で返す。TS には索引が無く、間隔が長いとシーク後に映像が出るまで時間がかかるため、約1秒に制限する。対象外の形式やフレームレート不明のときは制限しない。</summary>
    private int GetTsGopLimitFrames(string ext)
    {
        var format = GetSelectedVideoFormat();
        if (ext != ".ts" || (format != "AVC" && format != "HEVC") || _videoStreams.Count == 0) return 0;
        var fps = Probe.ConvertFrameRateToDouble(_videoStreams[0].AvgFrameRate);
        if (fps <= 0) fps = Probe.ConvertFrameRateToDouble(_videoStreams[0].RFrameRate);
        return fps > 0 ? Math.Max(1, (int)Math.Round(fps)) : 0;
    }

    /// <summary>再エンコード時の映像エンコーダー引数。フォーマットごとの固定設定 (プリセット) は従来の実装に合わせる (HEVC は preset medium、AV1 は SVT-AV1 preset 6)。</summary>
    private List<string> GetVideoEncoderArgs(string ext)
    {
        var crf = GetSelectedVideoCrf();
        var args = GetSelectedVideoFormat() switch
        {
            "AVC" => new List<string> { "-c:v", "libx264", "-preset", "medium", "-crf", crf, "-pix_fmt", GetVideoPixFmt() },
            "AV1" => new List<string> { "-c:v", "libsvtav1", "-preset", "6", "-crf", crf, "-pix_fmt", GetVideoPixFmt() },
            _ => new List<string> { "-c:v", "libx265", "-preset", "medium", "-crf", crf, "-pix_fmt", GetVideoPixFmt() },
        };
        var gopLimit = GetTsGopLimitFrames(ext);
        if (gopLimit > 0)
        {
            args.Add("-g");
            args.Add(gopLimit.ToString(CultureInfo.InvariantCulture));
        }
        return args;
    }

    private string GetVideoEncoderLogText(string ext)
    {
        var crf = GetSelectedVideoCrf();
        var text = GetSelectedVideoFormat() switch
        {
            "AVC" => $"AVC libx264 {GetSelectedBitDepth()}bit CRF {crf} / preset medium",
            "AV1" => $"AV1 SVT-AV1 {GetSelectedBitDepth()}bit CRF {crf} / preset 6",
            _ => $"HEVC libx265 {GetSelectedBitDepth()}bit CRF {crf} / preset medium",
        };
        if (GetTsGopLimitFrames(ext) > 0) text += " / GOP 約1秒";
        return text;
    }

    private static string FormatSpeedForFileName(double speed)
    {
        if (Math.Abs(speed - 0.5) < 0.000001) return "0.5";
        if (Math.Abs(speed - 1.0) < 0.000001) return "1.0";
        return speed.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string NormalizeSpeedOption(string value)
    {
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var speedValue)) speedValue = 1.0;
        return speedValue.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string GetAtempoFilterChain(double speed)
    {
        // atempo が受け付ける倍率 (0.5 ～ 2.0) に収まるよう、必要に応じて連結する。
        var remaining = speed;
        var parts = new List<string>();
        while (remaining < 0.5 - 0.000001) { parts.Add("atempo=0.5"); remaining /= 0.5; }
        while (remaining > 2.0 + 0.000001) { parts.Add("atempo=2.0"); remaining /= 2.0; }
        parts.Add("atempo=" + remaining.ToString("0.###", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    // 入力映像がすでに持つ回転（表示マトリクス）を反時計回りの度数で返す。なければ 0。
    private int GetSourceVideoRotation()
    {
        if (_videoStreams.Count == 0) return 0;
        var rotation = _videoStreams[0].DisplayMatrixRotation;
        if (rotation.Length > 0 && double.TryParse(rotation, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (int)(((Math.Round(value) % 360) + 360) % 360);
        }
        return 0;
    }

    // 選択した回転を入力の既存回転に加算した、FFmpeg の -display_rotation 用の角度（反時計回り 0～359）。
    private int GetRotationDisplayAngle(int index)
    {
        var delta = index switch { 1 => 270, 2 => 180, 3 => 90, _ => 0 };
        return (int)(((GetSourceVideoRotation() + delta) % 360 + 360) % 360);
    }

    private List<string> AddRotationInputArgument(List<string> arguments, bool videoReencoded)
    {
        // 回転メタデータを、最初の入力 (-i) の直前へ追加する。
        var rotationIndex = GetRotationIndex();
        if (rotationIndex <= 0) return arguments;
        var inputIndex = arguments.IndexOf("-i");
        if (inputIndex < 0) return arguments;
        var result = new List<string>();
        for (var i = 0; i < inputIndex; i++) result.Add(arguments[i]);
        if (videoReencoded) result.Add("-noautorotate");
        result.Add("-display_rotation");
        result.Add(GetRotationDisplayAngle(rotationIndex).ToString(CultureInfo.InvariantCulture));
        for (var i = inputIndex; i < arguments.Count; i++) result.Add(arguments[i]);
        return result;
    }

    // ------------------------------------------------------------------
    // 編集情報メタデータ (_ASTER_CUT)
    // ------------------------------------------------------------------

    private static string GetEditorCodecDisplayName(string codecName)
    {
        if (string.IsNullOrWhiteSpace(codecName)) return "不明";
        return codecName.ToLowerInvariant() switch
        {
            "hevc" or "h265" => "HEVC",
            "h264" or "avc" => "H.264",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "av1" => "AV1",
            "aac" => "AAC",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "mp3" => "MP3",
            "flac" => "FLAC",
            "ac3" => "AC-3",
            "eac3" => "E-AC-3",
            "alac" => "ALAC",
            "pcm_s16le" => "PCM 16-bit",
            "pcm_s24le" => "PCM 24-bit",
            _ => codecName,
        };
    }

    private static string GetEditorJstTimestamp()
    {
        var now = DateTimeOffset.Now;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            now = TimeZoneInfo.ConvertTime(now, zone);
        }
        catch { }
        return now.ToString("yyyy-MM-dd HH:mm:ss 'JST'", CultureInfo.InvariantCulture);
    }

    private string GetEditorCrfDisplay()
    {
        var value = GetSelectedVideoCrf();
        if (GetSelectedVideoFormat() == "AV1") return value;
        return double.Parse(value, CultureInfo.InvariantCulture).ToString("0.0################", CultureInfo.InvariantCulture);
    }

    private string GetEditorVideoDescription(bool reencoded)
    {
        var video = _videoStreams.Count > 0 ? _videoStreams[0] : null;
        if (reencoded)
        {
            return $"映像: {GetSelectedVideoFormat()} / CRF {GetEditorCrfDisplay()} / {GetSelectedBitDepth()}-bit";
        }
        var codec = GetEditorCodecDisplayName(video?.CodecName ?? "");
        var depth = 0;
        if (video != null)
        {
            _ = int.TryParse(video.BitsPerRawSample, out depth);
            if (depth <= 0 && video.PixFmt.Length > 0)
            {
                var match = Regex.Match(video.PixFmt, @"p(?<depth>\d+)(?:le|be)?$");
                if (match.Success) depth = int.Parse(match.Groups["depth"].Value, CultureInfo.InvariantCulture);
                else if (Regex.IsMatch(video.PixFmt, @"^(yuv|yuva|gbr|rgb|gray)")) depth = 8;
            }
        }
        if (depth > 0) return $"映像: {codec} / {depth}-bit / ストリームコピー";
        return $"映像: {codec} / ストリームコピー";
    }

    private List<string> GetEditorAudioDescriptionLines(IReadOnlyList<ProbeStream> audioStreams, bool reencoded)
    {
        if (audioStreams.Count == 0) return new List<string> { "音声: なし" };
        var lines = new List<string>();
        var requestedBitrate = reencoded ? GetSelectedAudioBitrateKbps() : 0;
        for (var i = 0; i < audioStreams.Count; i++)
        {
            var prefix = audioStreams.Count == 1 ? "音声: " : $"音声 {i + 1}: ";
            if (reencoded)
            {
                if (GetSelectedAudioFormat() == "AAC")
                {
                    lines.Add(prefix + $"AAC / {requestedBitrate} kbps");
                }
                else
                {
                    var channels = audioStreams[i].Channels;
                    var actualBitrate = channels < 2 && requestedBitrate > 256 ? 256 : requestedBitrate;
                    lines.Add(prefix + $"Opus / {actualBitrate} kbps / VBR");
                }
            }
            else
            {
                lines.Add(prefix + GetEditorCodecDisplayName(audioStreams[i].CodecName) + " / ストリームコピー");
            }
        }
        return lines;
    }

    private (int Width, int Height) GetEditorCropDimensions(Segment segment)
    {
        var source = GetVideoDimensions();
        var width = source.Width - segment.Left - segment.Right;
        var height = source.Height - segment.Top - segment.Bottom;
        if (width % 2 != 0) width--;
        if (height % 2 != 0) height--;
        if (width < 2 || height < 2) throw new InvalidOperationException("編集情報の作成時にクリッピング後の解像度が不正と判定されました。");
        return (width, height);
    }

    private List<string> NewEditorMetadataLines(
        string operation, long sourceDurationMs, long editedDurationMs,
        bool videoReencoded, bool audioReencoded,
        IReadOnlyList<ProbeStream>? outputAudioStreams = null,
        string speedText = "",
        int rotationIndex = 0,
        IReadOnlyList<Segment>? segments = null,
        bool useConcatPadding = false)
    {
        outputAudioStreams ??= Array.Empty<ProbeStream>();
        segments ??= Array.Empty<Segment>();
        // フレーム数は ffprobe のフレーム表から数える (プレビューの表示と同じ基準)。
        var sourceFrames = TestFrameTableReady() ? _frameTimesUs!.Length : -1;
        var lines = new List<string>
        {
            "[AsterCut]",
            $"処理: {operation}",
            $"編集日時: {GetEditorJstTimestamp()}",
            $"編集前の再生時間: {TimeText.FormatClockFromMs(sourceDurationMs)}{(sourceFrames >= 0 ? $" ({sourceFrames}f)" : "")}",
        };
        if (!string.IsNullOrWhiteSpace(speedText)) lines.Add($"再生速度: {speedText}x");

        if (rotationIndex > 0)
        {
            lines.Add($"回転: {GetRotationDisplayText(rotationIndex)}（メタデータのみ変更・映像の再エンコードなし）");
            var rotDimensions = GetVideoDimensions();
            var anyClipped = false;
            foreach (var segment in segments) if (segment.HasClipping) anyClipped = true;
            if (!anyClipped)
            {
                lines.Add($"編集前の解像度: {rotDimensions.Width}x{rotDimensions.Height}");
            }
            var sourceRotation = GetSourceVideoRotation();
            var totalRotation = GetRotationDisplayAngle(rotationIndex);
            if (totalRotation % 180 == 90)
            {
                lines.Add($"再生時の表示解像度: {rotDimensions.Height}x{rotDimensions.Width}");
            }
            else
            {
                lines.Add($"再生時の表示解像度: {rotDimensions.Width}x{rotDimensions.Height}");
            }
            lines.Add($"回転メタデータ（反時計回り）: {sourceRotation}° → {totalRotation}°");
        }

        var clipped = new List<(Segment Segment, int Index)>();
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].HasClipping) clipped.Add((segments[i], i + 1));
        }
        (int Width, int Height)? sourceDimensions = null;
        if (clipped.Count > 0)
        {
            sourceDimensions = GetVideoDimensions();
            lines.Add($"編集前の解像度: {sourceDimensions.Value.Width}x{sourceDimensions.Value.Height}");
        }

        if (segments.Count > 0)
        {
            if (segments.Count > 1)
            {
                lines.Add($"区間数: {segments.Count}");
                lines.Add("カット区間:");
                for (var i = 0; i < segments.Count; i++)
                {
                    lines.Add($"  {i + 1}. {TimeText.FormatClockFromMs(segments[i].StartMs)} - {TimeText.FormatClockFromMs(segments[i].EndMs)}");
                }
            }
            else if (Regex.IsMatch(operation, "カット|結合|分割"))
            {
                var segment = segments[0];
                lines.Add($"カット開始位置: {TimeText.FormatClockFromMs(segment.StartMs)}");
                lines.Add($"カット終了位置: {TimeText.FormatClockFromMs(segment.EndMs)}");
            }

            if (clipped.Count > 0)
            {
                lines.Add($"クリッピング範囲数: {clipped.Count}");
                lines.Add("クリッピング範囲:");
                foreach (var (segment, index) in clipped)
                {
                    var cropDimensions = GetEditorCropDimensions(segment);
                    lines.Add($"  {clipped.IndexOf((segment, index)) + 1}. (区間 {index})");
                    lines.Add($"    上: {segment.Top} px");
                    lines.Add($"    下: {segment.Bottom} px");
                    lines.Add($"    左: {segment.Left} px");
                    lines.Add($"    右: {segment.Right} px");
                    lines.Add($"    クロップ後の解像度: {cropDimensions.Width}x{cropDimensions.Height}");
                }
                if (useConcatPadding && segments.Count > 1 && sourceDimensions != null)
                {
                    var first = segments[0];
                    var targetWidth = sourceDimensions.Value.Width - first.Left - first.Right;
                    var targetHeight = sourceDimensions.Value.Height - first.Top - first.Bottom;
                    if (targetWidth % 2 != 0) targetWidth--;
                    if (targetHeight % 2 != 0) targetHeight--;
                    lines.Add($"黒帯追加後の最終解像度: {targetWidth}x{targetHeight}");
                }
                else if (segments.Count == 1)
                {
                    var onlyCrop = GetEditorCropDimensions(segments[0]);
                    lines.Add($"編集後の解像度: {onlyCrop.Width}x{onlyCrop.Height}");
                }
            }
        }

        var editedFrames = -1;
        if (sourceFrames >= 0)
        {
            if (segments.Count == 0)
            {
                // 全体の編集ではフレームは増減しない。
                editedFrames = sourceFrames;
            }
            else
            {
                var sum = 0;
                var complete = true;
                foreach (var segment in segments)
                {
                    var count = GetSegmentFrameCount(segment);
                    if (count < 0) { complete = false; break; }
                    sum += count;
                }
                if (complete) editedFrames = sum;
            }
        }
        lines.Add($"編集後の再生時間: {TimeText.FormatClockFromMs(editedDurationMs)}{(editedFrames >= 0 ? $" ({editedFrames}f)" : "")}");
        lines.Add(GetEditorVideoDescription(videoReencoded));
        foreach (var audioLine in GetEditorAudioDescriptionLines(outputAudioStreams, audioReencoded)) lines.Add(audioLine);
        return lines;
    }

    private static string MergeEditorMetadataHistory(string existingMetadata, IReadOnlyList<string> newMetadataLines)
    {
        var newLines = newMetadataLines.ToList();
        if (string.IsNullOrWhiteSpace(existingMetadata))
        {
            return string.Join("\r\n", newLines);
        }

        // 新しい編集内容から共通ヘッダーを除き、編集内容の本文だけを取得する。
        var newBodyLines = new List<string>(newLines);
        if (newBodyLines.Count > 0 && newBodyLines[0] == "[AsterCut]")
        {
            newBodyLines.RemoveAt(0);
        }

        var existingLines = Regex.Split(existingMetadata, "\r\n|\n|\r").ToList();
        const string historyHeaderPattern = @"^\[AsterCut 総編集回数:[ \t]*(\d+)\]$";

        // 新形式: 総編集回数をヘッダー内で更新し、今回の編集ブロックを末尾に追加する。
        if (existingLines.Count > 0)
        {
            var headerMatch = Regex.Match(existingLines[0], historyHeaderPattern);
            if (headerMatch.Success)
            {
                var currentEditCount = int.Parse(headerMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var existingBodyLines = existingLines.Skip(1)
                    .Where(line => !Regex.IsMatch(line, @"^総編集回数:[ \t]*\d+[ \t]*$")).ToList();
                foreach (Match entryMatch in Regex.Matches(string.Join("\r\n", existingBodyLines), @"(?m)^\[(\d+)回目の編集\][ \t]*$"))
                {
                    var entryNumber = int.Parse(entryMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (entryNumber > currentEditCount) currentEditCount = entryNumber;
                }
                var nextEditNumber = currentEditCount + 1;
                var existingBodyText = string.Join("\r\n", existingBodyLines).TrimEnd('\r', '\n');
                var newEntryText = string.Join("\r\n", new[] { $"[{nextEditNumber}回目の編集]" }.Concat(newBodyLines));
                var header = $"[AsterCut 総編集回数: {nextEditNumber}]";
                if (existingBodyText.Length == 0) return header + "\r\n" + newEntryText;
                return header + "\r\n" + existingBodyText + "\r\n\r\n" + newEntryText;
            }
        }


        // 従来形式のタグは、再編集時に初めて総編集回数と「1回目の編集」を付けて新形式へ移行する。
        var plainBodyLines = new List<string>(existingLines);
        if (plainBodyLines.Count > 0 && plainBodyLines[0] == "[AsterCut]") plainBodyLines.RemoveAt(0);
        var plainParts = new List<string>
        {
            "[AsterCut 総編集回数: 2]",
            "[1回目の編集]",
        };
        plainParts.AddRange(plainBodyLines);
        plainParts.Add("");
        plainParts.Add("[2回目の編集]");
        plainParts.AddRange(newBodyLines);
        return string.Join("\r\n", plainParts);
    }

    /// <summary>引数が出力へサムネイル (attached pic) のコピーを含むか。</summary>
    private bool OutputArgsCarryAttachedPic(List<string> args)
    {
        var pics = GetAttachedPicStreams();
        if (pics.Count == 0) return false;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] != "-map") continue;
            var map = args[i + 1];
            if (map == "0")
            {
                // 全ストリームのコピーでは、サムネイルを保持する選択のときだけ含まれる
                // (保持しない場合は負の map で除かれている)。
                if (IsKeepDataChecked("thumbnail")) return true;
            }
            else
            {
                foreach (var pic in pics)
                {
                    if (map == $"0:{pic.Index}") return true;
                }
            }
        }
        return false;
    }

    private List<string> AddEditorMetadataArgument(List<string> arguments, string outputPath, IReadOnlyList<string> metadataLines)
    {
        // 独自タグの書き込みは専用チェックボックスで制御する。「メタデータを残す」とは独立。
        // コマンドライン表示のプレビュー中は、ポップアップ側のチェックで切り替える。
        var writeMetadata = _previewingCommands ? _previewAsterCut : (_writeEditorMetadataCheckBox != null && _writeEditorMetadataCheckBox.Checked);
        if (!writeMetadata) return arguments;

        // 独自タグは Matroska (MKV) と MP4 / MOV / M4V 出力にのみ追加する。
        var outputExt = Path.GetExtension(outputPath).ToLowerInvariant();
        var isMkv = outputExt == ".mkv";
        var isMp4Family = outputExt is ".mp4" or ".mov" or ".m4v";
        if ((!isMkv && !isMp4Family) || metadataLines.Count == 0) return arguments;
        if (arguments.Count == 0 || !arguments[^1].Equals(outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("編集情報を付加できません。FFmpeg コマンドの末尾が出力ファイルパスではありません。");
        }

        // 入力動画に既存の独自タグがあれば、初回編集時の内容を残して再編集履歴を追記する。
        var existingMetadata = GetSourceEditorMetadataText();
        string metadataValue;
        if (!string.IsNullOrWhiteSpace(existingMetadata) && existingMetadata != "なし" && existingMetadata != "—")
        {
            metadataValue = MergeEditorMetadataHistory(existingMetadata, metadataLines);
        }
        else
        {
            metadataValue = string.Join("\r\n", metadataLines);
        }
        var result = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i == arguments.Count - 1)
            {
                result.Add("-metadata");
                result.Add("_ASTER_CUT=" + metadataValue);
            }
            result.Add(arguments[i]);
        }

        if (isMp4Family && OutputArgsCarryAttachedPic(result))
        {
            // use_metadata_tags を付けると、コピーしたサムネイル (attached pic) が MP4 へ
            // 書き込まれずに落ちてしまう (ffmpeg の mov マクサの挙動)。サムネイルは入力の
            // 中身そのものなので、独自タグより優先し、この出力では独自タグを書き込まない。
            WriteLog("サムネイルを保持するため、この出力では独自タグ (_ASTER_CUT) を書き込めません。");
            return result;
        }
        if (isMp4Family)
        {
            // MP4 / MOV / M4V は、use_metadata_tags を指定しないと任意キーのタグが書き込まれない。
            var movflagsIndex = -1;
            for (var i = 0; i < result.Count - 1; i++)
            {
                if (result[i] == "-movflags") movflagsIndex = i + 1;
            }
            if (movflagsIndex >= 0)
            {
                if (!Regex.IsMatch(result[movflagsIndex], "use_metadata_tags"))
                {
                    result[movflagsIndex] += "+use_metadata_tags";
                }
            }
            else
            {
                result.Insert(result.Count - 1, "+use_metadata_tags");
                result.Insert(result.Count - 2, "-movflags");
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 出力の開始
    // ------------------------------------------------------------------

    private void StartSelectedOutput(bool forceEncode)
    {
        if (_jobRunning) return;
        if (_probe == null)
        {
            MessageBox.Show("先に入力動画を読み込んでください。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var hasSegments = _segments.Count > 0;
        WriteLog(new string('=', 60));
        WriteLog($"ユーザーが出力を開始: mode={(hasSegments ? "grouped" : "non-cut")}");
        WriteLog($"出力方式: {(forceEncode ? "エンコードして出力 (必ず再エンコード)" : "動画を出力 (可能な限り無劣化)")}");
        WriteLog($"メタデータ: {(GetMapMetadataValue() == "0" ? "残す (-map_metadata 0)" : "残さない (-map_metadata -1)")}（チャプター・サムネイル・字幕はこの設定の対象外）");
        WriteLog($"編集情報メタデータ: {(_writeEditorMetadataCheckBox != null && _writeEditorMetadataCheckBox.Checked ? "書き込む (_ASTER_CUT)" : "書き込まない")}（MKV / MP4 / MOV / M4V のみ）");
        if (hasSegments) StartGroupedCut(forceEncode);
        else StartNonCutEdit(forceEncode);
    }

    /// <summary>
    /// MP4 系コンテナに Opus が入る出力の前に、格納が標準化されていない旨を確認する。
    /// 「次回以降表示しない」を選ぶと設定に保存し、次回からは確認しない。キャンセル時は false を返す。
    /// </summary>
    private bool ConfirmOpusInMp4(bool audioReencoded)
    {
        if (_previewingCommands) return true;
        if (_settings.SuppressOpusInMp4Warning) return true;

        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = AppTitle,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 196),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };

        var message = audioReencoded
            ? "音声を Opus に再エンコードして、MP4 系のコンテナに出力します。"
            : "元の Opus 音声を、MP4 系のコンテナにそのまま格納します。";
        message += "\r\nOpus を MP4 に格納する方式は標準化されていないため (格納方法の文書はドラフトのままです)、"
            + "再生環境によっては音声が再生できないことがあります。"
            + "\r\nそのまま出力しますか？";
        var label = Theme.MakeLabel(message, 16, 14, 488, 78);
        dialog.Controls.Add(label);

        var dontShowCheck = new CheckBox
        {
            Text = "次回以降、このポップアップを表示しない",
            Location = new Point(16, 104),
            Size = new Size(400, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
        };
        dialog.Controls.Add(dontShowCheck);

        var okButton = Theme.MakeButton("出力する", 296, 148, 96, 32);
        okButton.DialogResult = DialogResult.OK;
        dialog.Controls.Add(okButton);
        var cancelButton = Theme.MakeButton("キャンセル", 400, 148, 104, 32);
        cancelButton.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        Theme.ApplyDarkTitleBar(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        if (dontShowCheck.Checked)
        {
            _settings.SuppressOpusInMp4Warning = true;
            _settings.Save();
        }
        return true;
    }

    private void StartNonCutEdit(bool forceEncode)
    {
        try
        {
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            if (_videoStreams.Count == 0) throw new InvalidOperationException("入力動画に映像ストリームがありません。");
            if (_durationMs <= 0) throw new InvalidOperationException("入力動画の再生時間を取得できないため、全体編集を実行できません。");

            var selectedAudios = GetSelectedAudioStreams();
            var speedValue = GetSelectedSpeed();
            var speedChanged = Math.Abs(speedValue - 1.0) >= 0.000001;
            var speedInvariant = speedValue.ToString("0.###", CultureInfo.InvariantCulture);
            var speedText = speedValue.ToString("0.00", CultureInfo.InvariantCulture);
            var rotationIndex = GetRotationIndex();
            var audioChanged = selectedAudios.Count < _audioStreams.Count;

            var output = GetWholeOutputPath(forceEncode);
            if (Path.GetFullPath(output).Equals(Path.GetFullPath(_inputPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("出力ファイルが入力動画と同じパスになります。出力先のフォルダーを変更してください。");
            }
            _defaultOutputPath = output;
            var ext = Path.GetExtension(output).ToLowerInvariant();

            // 音声を保持した速度変更では映像・音声ともフィルターで再エンコードする。
            // 音声を保持しない速度変更は setts で映像のタイムスタンプだけを縮尺し、ストリームコピーする。
            // 「エンコードして出力」では、変更がなくても映像・音声とも再エンコードする。
            var videoReencoded = forceEncode || (speedChanged && selectedAudios.Count > 0);
            var audioReencoded = videoReencoded;
            // MP4 系コンテナに Opus が入る出力 (Opus への再エンコード、または元の Opus 音声のコピー) では、
            // 格納が標準化されていない旨を先に確認する。
            var willContainOpus = audioReencoded
                ? GetSelectedAudioFormat() == "Opus"
                : selectedAudios.Any(a => string.Equals(a.CodecName, "opus", StringComparison.OrdinalIgnoreCase));
            if (ext is ".mp4" or ".mov" or ".m4v")
            {
                if (willContainOpus && !ConfirmOpusInMp4(audioReencoded))
                {
                    WriteLog("Opus in MP4 の確認でユーザーがキャンセルしました。全体編集を中止します。");
                    return;
                }
            }
            var editedDurationMs = (long)Math.Round(_durationMs / speedValue);

            WriteLog($"全体編集の入力ファイル: {_inputPath}");
            WriteLog($"再生速度={speedText}x / 回転={(rotationIndex > 0 ? GetRotationDisplayText(rotationIndex) : "なし")} / 音声={selectedAudios.Count}/{_audioStreams.Count} トラック");
            WriteLog($"全体編集方式: 映像再エンコード={videoReencoded}; 音声再エンコード={audioReencoded}; 音声トラック変更={audioChanged}");
            WriteLog($"出力予定ファイル名: {output}");
            if (WholeOutputIsStreamCopy(forceEncode) && GetWholeOutputExtension(forceEncode) != GetContainerExtension())
            {
                WriteLog($"ストリームコピーは入力と同じコンテナ ({GetWholeOutputExtension(forceEncode)}) で出力します。");
            }

            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "verbose", "-probesize", "100M", "-analyzeduration", "100M", "-i", _inputPath };
            var description = "";
            if (videoReencoded)
            {
                if (speedChanged)
                {
                    var filterParts = new List<string>();
                    var videoLabels = new List<string>();
                    var audioLabels = new List<string>();

                    // 速度変更時は全映像ストリームを setpts で縮尺する。時刻の原点は STARTPTS にしない。
                    // 音声のチャンネル構成変更などでフィルターグラフが再初期化されると、再初期化後の
                    // 最初のフレームで STARTPTS が取り直され、時刻軸が 0 へ戻って出力が壊れるため。
                    // グラフ内の時刻は入力の先頭を 0 としたものなので、そのまま縮尺すればよい。
                    for (var videoOrdinal = 0; videoOrdinal < _videoStreams.Count; videoOrdinal++)
                    {
                        var videoLabel = $"vout{videoOrdinal}";
                        var videoStreamIndex = _videoStreams[videoOrdinal].Index;
                        filterParts.Add($"[0:{videoStreamIndex}]setpts=PTS/{speedInvariant}[{videoLabel}]");
                        videoLabels.Add($"[{videoLabel}]");
                    }

                    for (var audioOrdinal = 0; audioOrdinal < selectedAudios.Count; audioOrdinal++)
                    {
                        var audioRelativeIndex = -1;
                        for (var i = 0; i < _audioStreams.Count; i++)
                        {
                            if (_audioStreams[i].Index == selectedAudios[audioOrdinal].Index) { audioRelativeIndex = i; break; }
                        }
                        if (audioRelativeIndex < 0) throw new InvalidOperationException("選択した音声トラックのインデックスを取得できません。");
                        var audioLabel = $"aout{audioOrdinal}";
                        var tempoFilter = GetAtempoFilterChain(speedValue);
                        filterParts.Add($"[0:a:{audioRelativeIndex}]{tempoFilter}[{audioLabel}]");
                        audioLabels.Add($"[{audioLabel}]");
                    }

                    args.Add("-filter_complex");
                    args.Add(string.Join(";", filterParts));
                    foreach (var label in videoLabels) { args.Add("-map"); args.Add(label); }
                    foreach (var label in audioLabels) { args.Add("-map"); args.Add(label); }
                }
                else
                {
                    // 速度変更がない場合はフィルターを通さず、そのままマッピングする。
                    // フィルターグラフは音声のチャンネル構成変更などで再初期化されることがあり、
                    // その際に setpts / asetpts の時刻軸が壊れて、再生の不安定な出力になる入力があるため。
                    foreach (var video in _videoStreams) { args.Add("-map"); args.Add($"0:{video.Index}"); }
                    for (var audioOrdinal = 0; audioOrdinal < selectedAudios.Count; audioOrdinal++)
                    {
                        var audioRelativeIndex = -1;
                        for (var i = 0; i < _audioStreams.Count; i++)
                        {
                            if (_audioStreams[i].Index == selectedAudios[audioOrdinal].Index) { audioRelativeIndex = i; break; }
                        }
                        if (audioRelativeIndex < 0) throw new InvalidOperationException("選択した音声トラックのインデックスを取得できません。");
                        args.Add("-map");
                        args.Add($"0:a:{audioRelativeIndex}");
                    }
                }

                args.AddRange(GetVideoEncoderArgs(ext));
                WriteLog($"映像エンコーダー: {GetVideoEncoderLogText(ext)}");
                args.AddRange(GetAudioEncoderArgs(selectedAudios));
                AddKeepDataToEncodeArgs(args, ext, 0, _durationMs, wholeFile: true, speedChanged, _videoStreams.Count);
                description = speedChanged
                    ? "動画全体の再生速度を変更して映像・選択音声を再エンコード"
                    : "動画全体を映像・選択音声とも再エンコード";
            }
            else
            {
                if (speedChanged)
                {
                    // 音声を保持しない速度変更では、全映像ストリームのタイムスタンプを縮尺してストリームコピーする。
                    foreach (var video in _videoStreams) { args.Add("-map"); args.Add($"0:{video.Index}"); }
                    args.AddRange(new[] { "-c", "copy" });
                    var settsFilter = $"setts=pts=(PTS-STARTPTS)/{speedInvariant}:dts=(DTS-STARTDTS)/{speedInvariant}:duration=DURATION/{speedInvariant}";
                    args.AddRange(new[] { "-bsf:v", settsFilter, "-avoid_negative_ts", "make_zero" });
                    WriteLog($"映像 BSF: {settsFilter}");
                    description = "動画全体の再生速度をタイムスタンプ変更で調整（映像ストリームコピー）";
                }
                else
                {
                    // 速度変更がない場合は音声以外の全ストリームを維持し、未選択の音声だけを除外する。
                    // データストリームは、Matroska 等のコンテナに格納できず
                    // ヘッダー書き込みでエラーになるため、TS へ出力する場合を除いて除外する (字幕は維持する)。
                    args.AddRange(new[] { "-map", "0" });
                    if (Path.GetExtension(output).ToLowerInvariant() is not (".ts" or ".m2ts"))
                    {
                        args.AddRange(new[] { "-map", "-0:d" });
                    }
                    for (var audioIndex = 0; audioIndex < _audioStreams.Count; audioIndex++)
                    {
                        if (audioIndex >= _audioList.Items.Count || !_audioList.GetItemChecked(audioIndex))
                        {
                            args.Add("-map");
                            args.Add($"-0:a:{audioIndex}");
                        }
                    }
                    // 「単一区間で保持するデータ」で外れているものは、全体コピーでも除く。
                    if (!IsKeepDataChecked("subtitle"))
                    {
                        args.AddRange(new[] { "-map", "-0:s" });
                        // 添付ファイルは字幕と一緒に除く。ただしサムネイル (カバー画像) を保持する場合は残す。
                        var keepCover = IsKeepDataChecked("thumbnail")
                            && GetAttachmentStreams().Exists(s => s.Tag("filename").StartsWith("cover.", StringComparison.OrdinalIgnoreCase));
                        if (!keepCover) args.AddRange(new[] { "-map", "-0:t" });
                    }
                    if (!IsKeepDataChecked("thumbnail"))
                    {
                        foreach (var pic in GetAttachedPicStreams())
                        {
                            args.Add("-map");
                            args.Add($"-0:{pic.Index}");
                        }
                    }
                    if (!IsKeepDataChecked("databroadcast") && Path.GetExtension(output).ToLowerInvariant() is (".ts" or ".m2ts"))
                    {
                        foreach (var stream in _probe!.Streams)
                        {
                            if (string.Equals(stream.CodecTag, "0x000d", StringComparison.OrdinalIgnoreCase))
                            {
                                args.Add("-map");
                                args.Add($"-0:{stream.Index}");
                            }
                        }
                    }
                    args.AddRange(new[] { "-c", "copy" });
                    description = rotationIndex == 0 && !audioChanged
                        ? "動画全体をそのままコピー（全ストリームコピー）"
                        : "動画全体の回転メタデータ・音声トラックを変更（その他のストリームはコピー）";
                }
            }

            // WebM で映像をストリームコピーする場合は、コンテナが対応する VP8 / VP9 / AV1 だけを許可する。
            if (ext == ".webm" && !videoReencoded)
            {
                var unsupported = _videoStreams.Where(v => v.CodecName.ToLowerInvariant() is not ("vp8" or "vp9" or "av1")).ToList();
                if (unsupported.Count > 0)
                {
                    var names = string.Join(", ", unsupported.Select(v => v.CodecName.Length > 0 ? v.CodecName : "unknown").Distinct());
                    throw new InvalidOperationException($"WebM 出力では映像を無劣化コピーできないコーデックが選択されています: {names}。別の出力形式を指定してください。");
                }
            }

            // WebM で音声をストリームコピーする場合は、コンテナが対応する Opus / Vorbis だけを許可する。
            if (ext == ".webm" && !audioReencoded && selectedAudios.Count > 0)
            {
                var unsupported = selectedAudios.Where(a => a.CodecName.ToLowerInvariant() is not ("opus" or "vorbis")).ToList();
                if (unsupported.Count > 0)
                {
                    var names = string.Join(", ", unsupported.Select(a => a.CodecName.Length > 0 ? a.CodecName : "unknown").Distinct());
                    throw new InvalidOperationException($"WebM 出力では音声を無劣化コピーできないコーデックが選択されています: {names}。Opus / Vorbis の音声を選択するか、別の出力形式を指定してください。");
                }
            }

            args.AddRange(new[] { "-map_metadata", GetMapMetadataValue(), "-map_chapters", speedChanged || !IsKeepDataChecked("chapter") ? "-1" : "0" });
            if (ext is ".mp4" or ".mov" or ".m4v") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            args = AddRotationInputArgument(args, videoReencoded);

            var operationParts = new List<string>();
            if (speedChanged) operationParts.Add("再生速度変更");
            if (rotationIndex > 0) operationParts.Add("回転");
            if (audioChanged) operationParts.Add("音声トラック整理");
            if (operationParts.Count == 0) operationParts.Add(forceEncode ? "全体再エンコード" : "全体コピー");
            var editorOperation = string.Join(" + ", operationParts);
            var editorLines = NewEditorMetadataLines(editorOperation, _durationMs, editedDurationMs,
                videoReencoded, audioReencoded, selectedAudios,
                speedChanged ? speedText : "", rotationIndex);
            args = AddEditorMetadataArgument(args, output, editorLines);

            WriteLog($"実行方式: {description}");
            WriteLog("予定ステップ数: 1（単一 FFmpeg プロセス）");
            var step = new JobStep
            {
                Description = description,
                Arguments = args.ToArray(),
                VerifyPath = output,
                ExpectedDurationMs = editedDurationMs,
                VerificationKind = videoReencoded ? "non-cut-reencoded-output" : "non-cut-streamcopy-output",
                SourceDurationMs = _durationMs,
                ProgramInfoSourcePath = videoReencoded && !speedChanged && IsKeepDataChecked("programinfo")
                    && string.Equals(Path.GetExtension(output), ".ts", StringComparison.OrdinalIgnoreCase)
                    && IsTsSourceContainer()
                    ? _inputPath
                    : "",
                ProgramInfoStartMs = 0,
                ProgramInfoEndMs = _durationMs,
            };
            StartPlan(new List<JobStep> { step }, output);
        }
        catch (Exception ex)
        {
            if (_previewingCommands)
            {
                _capturedCommandError = ex.Message;
                return;
            }
            WriteLog($"カット以外の編集を開始できませんでした: {ex.Message}");
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------
    // 区間抽出の引数ビルダー
    // ------------------------------------------------------------------

    private readonly Dictionary<(string Path, long StartMs, long EndMs), long> _losslessVideoLengthCache = new();
    private readonly Dictionary<(string Path, long StartMs, long EndMs), (long VideoStartMs, long VideoLengthMs, long ContentStartMs, long StartPacketIndex, long EndPacketIndex, long FrameDurationMs)?> _losslessVideoRangeCache = new();
    private readonly Dictionary<string, long> _losslessBsfZeroCache = new();
    private readonly object _losslessProbeLock = new();

    /// <summary>
    /// 無劣化カットの映像の長さを、終了位置以前の最後の I / P フレームに合わせた長さ (ms) で返す。
    /// 放送 TS は GOP 先頭の B フレームが次の GOP の I フレームを参照する (オープン GOP) ことがあり、
    /// 指定どおりの位置で切ると、参照先を失った末尾の B フレームがデコードで崩れるため。
    /// 合わせる必要がない、または調べられなければ元の長さを返す。
    /// </summary>
    private long AdjustLosslessVideoLengthMs(long startMs, long endMs)
    {
        var lengthMs = endMs - startMs;
        if (_probe == null || _videoStreams.Count == 0 || _inputPath.Length == 0) return lengthMs;
        var key = (_inputPath, startMs, endMs);
        lock (_losslessProbeLock) { if (_losslessVideoLengthCache.TryGetValue(key, out var cached)) return cached; }
        var result = lengthMs;
        try
        {
            var ffprobe = GetFfprobePath();
            if (ffprobe.Length > 0)
            {
                var formatStartSec = Probe.ParseSeconds(_probe.FormatStartTime);
                var videoFirstAbsSec = Probe.ParseSeconds(_videoStreams[0].StartTime);
                var bsfZeroAppUs = GetLosslessBsfZeroAppUs(ffprobe, formatStartSec, videoFirstAbsSec);
                var bsfZeroAbsSec = formatStartSec + bsfZeroAppUs / 1000000.0;
                // trim (start_rel) は ffmpeg が実際に渡す先頭パケットを起点に範囲を数えるため、実際に書き出される
                // 先頭パケットの位置と、末尾のアンカーの位置の両方を実測して長さを決める。
                var firstExportedMs = ProbeFirstExportedVideoMs(ffprobe, startMs, formatStartSec, bsfZeroAbsSec);
                // アンカーは、指定した終了位置以前で最後のものを探す。trim の長さは書き出された
                // 先頭パケットから数えられるため、範囲の終わりをアプリの時刻軸の開始位置から数えると、
                // 映像が指定した終了位置より後ろまで残って音声だけが先に終わってしまうので、
                // 上限はあくまで指定した終了位置 (アプリの時刻軸) そのものにする。
                var (anchorUs, anchorEndUs) = firstExportedMs >= 0
                    ? ProbeLastAnchorRangeUs(ffprobe, endMs, formatStartSec)
                    : (-1, -1);
                var lastAnchorMs = anchorUs >= 0 ? (long)Math.Round(anchorUs / 1000.0) : -1;
                if (firstExportedMs >= 0 && lastAnchorMs > firstExportedMs)
                {
                    var videoFirstAppUs = bsfZeroAppUs;
                    long adjusted;
                    var endAligned = false;
                    if (anchorEndUs > anchorUs && anchorEndUs / 1000 <= endMs + 100)
                    {
                        // 窓の終わりをアンカーの表示終了 (= 次のフレームの位置) に合わせる。
                        // trim は範囲の終わりにかかったパケットの duration を範囲内に切り詰めるため、
                        // アンカーの途中で窓が終わると、その duration が短く記録されて、編集リストの
                        // 終端もその分だけ前へずれてしまい、最後のフレームの表示が途中で切れる。
                        // trim の終了判定は境界ちょうどのパケットを含まないので、次のフレームを
                        // 取り込むことはない (ミリ秒への切り捨てで最大 1 ミリ秒手前に寄る)。
                        adjusted = (anchorEndUs - videoFirstAppUs) / 1000 - startMs;
                        endAligned = true;
                    }
                    else
                    {
                        // アンカーまでの長さは両端の時刻をミリ秒へ丸めてから引くため、最大で 1 ミリ秒ほど
                        // 短くなり得る。trim の終了判定は境界ちょうどのパケットを含まないので、そのままの
                        // 長さを渡すとアンカー自身が範囲から外れてしまう。2 ミリ秒の余裕を加えてアンカーを
                        // 確実に含める (次のフレームはどんなフレームレートでも 3 ミリ秒以上先にあるため、
                        // 余分に含めてしまうことはない)。
                        adjusted = lastAnchorMs - firstExportedMs + 2;
                    }
                    if (adjusted > 0 && (endAligned ? adjusted <= lengthMs + 100 : adjusted < lengthMs))
                    {
                        result = adjusted;
                        WriteLog($"映像の終了位置を末尾の参照フレームに合わせて調整しました: 映像の長さ {TimeText.FormatClockFromMs(lengthMs)} → {TimeText.FormatClockFromMs(adjusted)} (末尾の B フレームの参照切れを防ぐため)。");
                    }
                }
                else if (firstExportedMs < 0 || lastAnchorMs < 0)
                {
                    WriteLog("映像の終了位置の調整に必要なフレーム情報を取得できませんでした。指定どおりの位置で切り取ります。");
                }
            }
        }
        catch { result = lengthMs; }
        lock (_losslessProbeLock) { _losslessVideoLengthCache[key] = result; }
        return result;
    }

    /// <summary>
    /// 無劣化カットの映像の開始位置 (trim の start_rel 起点で数えた ms) と長さを、開始位置以前の
    /// 最後の I フレームと、終了位置以前の最後の I / P フレームに合わせた値で返す。
    /// trim (start_rel) は ffmpeg が実際に渡す先頭パケットを起点に範囲を数えるため、アプリの時刻軸の開始位置を
    /// そのまま渡すと、そのパケットがコンテナの先頭より後ろにある素材では映像の範囲全体が
    /// その分だけ後ろへずれる。さらに、開始位置が GOP の途中だと、先頭のフレームが参照する
    /// I フレームが範囲から外れて、先頭の GOP がまるごとデコードできなくなる。
    /// 開始位置を I フレームに合わせ、長さは実際に書き出される先頭パケットから末尾の
    /// I / P フレームまでで測ることで、開始側と終了側のどちらの参照切れも防ぐ。
    /// 調べられなければ null を返す (呼び出し側は従来の方法を使う)。
    /// </summary>
    private (long VideoStartMs, long VideoLengthMs, long ContentStartMs, long StartPacketIndex, long EndPacketIndex, long FrameDurationMs)? AdjustLosslessVideoRangeMs(long startMs, long endMs)
    {
        if (_probe == null || _videoStreams.Count == 0 || _inputPath.Length == 0) return null;
        var key = (_inputPath, startMs, endMs);
        lock (_losslessProbeLock) { if (_losslessVideoRangeCache.TryGetValue(key, out var cached)) return cached; }
        (long VideoStartMs, long VideoLengthMs, long ContentStartMs, long StartPacketIndex, long EndPacketIndex, long FrameDurationMs)? result = null;
        try
        {
            var ffprobe = GetFfprobePath();
            if (ffprobe.Length > 0)
            {
                var formatStartSec = Probe.ParseSeconds(_probe.FormatStartTime);
                var videoFirstAbsSec = Probe.ParseSeconds(_videoStreams[0].StartTime);
                var videoFirstAppMs = (long)Math.Round((videoFirstAbsSec - formatStartSec) * 1000.0);
                var bsfZeroAppUs = GetLosslessBsfZeroAppUs(ffprobe, formatStartSec, videoFirstAbsSec);
                var bsfZeroAppMs = (long)Math.Round(bsfZeroAppUs / 1000.0);
                var bsfZeroAbsSec = formatStartSec + bsfZeroAppUs / 1000000.0;
                var (anchorAppMs, nextKeyAppMs) = ProbeKeyFramesAroundMs(ffprobe, startMs, formatStartSec);
                long videoStartMs;
                if (anchorAppMs >= 0)
                {
                    videoStartMs = anchorAppMs - bsfZeroAppMs;
                }
                else if (nextKeyAppMs >= 0)
                {
                    // 開始位置が最初の I フレームより前にある (ファイルの先頭付近)。
                    // 先頭のパケットからすべて含める。
                    var minAppMs = ProbeMinVideoPacketMs(ffprobe, formatStartSec, videoFirstAbsSec);
                    if (minAppMs < 0)
                    {
                        lock (_losslessProbeLock) { _losslessVideoRangeCache[key] = null; }
                        return null;
                    }
                    videoStartMs = minAppMs - bsfZeroAppMs - 2;
                }
                else
                {
                    // キーフレームのパケットに表示時刻が付いていない素材 (AVI など) では、
                    // 時刻による探査ではキーフレームが 1 つも見つからない。その場合は
                    // パケットの並び順 (インデックス) で範囲を指定する方法に切り替える。
                    result = ProbeLosslessPacketIndexRangeMs(ffprobe, startMs, endMs, formatStartSec, videoFirstAppMs);
                    if (!result.HasValue)
                    {
                        lock (_losslessProbeLock) { _losslessVideoRangeCache[key] = null; }
                        return null;
                    }
                    lock (_losslessProbeLock) { _losslessVideoRangeCache[key] = result; }
                    return result;
                }
                var firstExportedMs = ProbeFirstExportedVideoMs(ffprobe, videoStartMs, formatStartSec, bsfZeroAbsSec);
                var (anchorUs, anchorEndUs) = firstExportedMs >= 0
                    ? ProbeLastAnchorRangeUs(ffprobe, endMs, formatStartSec)
                    : (-1, -1);
                var lastAnchorMs = anchorUs >= 0 ? (long)Math.Round(anchorUs / 1000.0) : -1;
                if (firstExportedMs >= 0 && lastAnchorMs > firstExportedMs)
                {
                    long length;
                    if (anchorEndUs > anchorUs && anchorEndUs / 1000 <= endMs + 100)
                    {
                        // 窓の終わりをアンカーの表示終了 (= 次のフレームの位置) に合わせる。
                        // 理由は AdjustLosslessVideoLengthMs の説明と同じ (アンカーの duration が
                        // 切り詰められて、編集リストの終端が前へずれるのを防ぐ)。
                        length = (anchorEndUs - bsfZeroAppUs) / 1000 - videoStartMs;
                    }
                    else
                    {
                        // 末尾のアンカーまでの長さに 2 ミリ秒の余裕を加える理由は
                        // AdjustLosslessVideoLengthMs の説明と同じ (丸めでアンカーが外れるのを防ぐ)。
                        length = lastAnchorMs - firstExportedMs + 2;
                    }
                    if (length > 0)
                    {
                        // ContentStartMs は、実際に書き出される先頭フレームの位置 (映像の先頭
                        // パケット起点)。番組情報の移植が、映像の内容に合わせた配置に使う。
                        result = (videoStartMs, length, firstExportedMs - videoFirstAppMs, -1, -1, 0);
                        if (firstExportedMs != startMs)
                        {
                            WriteLog($"映像の開始位置を先行する I フレームに合わせて調整しました: 開始 {TimeText.FormatClockFromMs(startMs)} → {TimeText.FormatClockFromMs(firstExportedMs)} (先頭のフレームの参照切れを防ぐため)。");
                        }
                        if (lastAnchorMs < endMs)
                        {
                            WriteLog($"映像の終了位置を末尾の参照フレームに合わせて調整しました: 終了 {TimeText.FormatClockFromMs(endMs)} → {TimeText.FormatClockFromMs(lastAnchorMs)} (末尾の B フレームの参照切れを防ぐため)。");
                        }
                    }
                }
            }
        }
        catch { result = null; }
        lock (_losslessProbeLock) { _losslessVideoRangeCache[key] = result; }
        return result;
    }

    /// <summary>
    /// 指定位置以前の最後のキーフレームと、以後最初のキーフレームの位置 (アプリの時刻軸の ms) を返す。
    /// どちらも無ければ -1。前後 30 秒の範囲のパケットだけを調べる (デコードはしない)。
    /// </summary>
    private (long LastKeyMs, long NextKeyMs) ProbeKeyFramesAroundMs(string ffprobe, long positionMs, double formatStartSec)
    {
        var args = new[]
        {
            "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
        };
        long lastKeyMs = -1;
        long nextKeyMs = -1;
        var fromSec = formatStartSec + Math.Max(0.0, positionMs / 1000.0 - 30.0);
        var toSec = formatStartSec + positionMs / 1000.0 + 30.0;
        foreach (var line in RunFfprobeRange(ffprobe, fromSec, toSec, args))
        {
            var parts = line.Split(',');
            string? flags = null;
            var ptsSec = double.NaN;
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) ptsSec = value;
                else if (trimmed.Length > 0) flags = trimmed;
            }
            if (flags == null || double.IsNaN(ptsSec)) continue;
            if (!flags.Contains('K')) continue;
            var appMs = (long)Math.Round((ptsSec - formatStartSec) * 1000.0);
            if (appMs <= positionMs)
            {
                if (appMs > lastKeyMs) lastKeyMs = appMs;
            }
            else if (nextKeyMs < 0 || appMs < nextKeyMs)
            {
                nextKeyMs = appMs;
            }
        }
        return (lastKeyMs, nextKeyMs);
    }

    /// <summary>
    /// パケットに表示時刻が付いていない素材 (AVI の MPEG-4 など) 用の範囲調整。
    /// そういう素材では、キーフレームなどの要のパケットに限って表示時刻が付いておらず、
    /// 時刻で探す探査 (ProbeKeyFramesAroundMs) ではキーフレームが見つからないうえ、
    /// trim に時刻で範囲を渡しても、表示時刻の無いパケットは範囲の判定から漏れて
    /// 書き出されず、先頭の GOP が丸ごと欠けた再生不能な出力になる。
    /// そこで、パケットの並び順 (デコード順のインデックス) で範囲を指定する。
    /// 開始は指定位置以前の最後のキーフレーム、終了は指定位置以前の最後の I / P フレームの
    /// 次のパケットまで。キーフレームの表示位置は、先頭付近で実測した
    /// 「表示順とデコード順のずれ (リード)」を使って、デコード順の位置から求める。
    /// 調べられなければ null を返す (呼び出し側は従来の方法を使う)。
    /// </summary>
    private (long VideoStartMs, long VideoLengthMs, long ContentStartMs, long StartPacketIndex, long EndPacketIndex, long FrameDurationMs)?
        ProbeLosslessPacketIndexRangeMs(string ffprobe, long startMs, long endMs, double formatStartSec, long videoFirstAppMs)
    {
        var streamIndex = _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture);
        // 1) パケットを先頭から開始位置の少し先まで数え、キーフレームのデコード順の
        //    位置と、dts の間隔からフレーム間隔を求める。
        var packetArgs = new[]
        {
            "-select_streams", streamIndex,
            "-show_entries", "packet=dts_time,flags",
            "-of", "csv=p=0",
        };
        var keyIndices = new List<long>();
        var dtsDeltas = new List<double>();
        double? prevDts = null;
        long packetCount = 0;
        foreach (var line in RunFfprobeRange(ffprobe, formatStartSec, formatStartSec + startMs / 1000.0 + 3.0, packetArgs))
        {
            var parts = line.Split(',');
            string? flags = null;
            var dtsSec = double.NaN;
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) dtsSec = value;
                else if (trimmed.Length > 0) flags = trimmed;
            }
            if (!double.IsNaN(dtsSec))
            {
                if (prevDts.HasValue && dtsSec > prevDts.Value) dtsDeltas.Add(dtsSec - prevDts.Value);
                prevDts = dtsSec;
            }
            if (flags != null && flags.Contains('K')) keyIndices.Add(packetCount);
            packetCount++;
        }
        // 探査が途中で打ち切られていたら、数えた位置は信用できない。
        if (packetCount < 10 || packetCount >= 19999 || keyIndices.Count < 2 || dtsDeltas.Count == 0) return null;
        if (!prevDts.HasValue || prevDts.Value < formatStartSec + startMs / 1000.0 - 0.5) return null;
        dtsDeltas.Sort();
        var frameDurSec = dtsDeltas[dtsDeltas.Count / 2];
        if (frameDurSec <= 0.0005 || frameDurSec > 1.0) return null;
        var frameDurMs = frameDurSec * 1000.0;
        // 2) 先頭付近のフレームを調べ、I フレームの表示順の位置 (序数) を数える。
        //    キーフレームの表示順とデコード順のずれ (リード) を、2 つ目以降の
        //    キーフレームで実測する (最初のキーフレームはどちらも 0 で自明)。
        var frameArgs = new[]
        {
            "-select_streams", streamIndex,
            "-show_entries", "frame=best_effort_timestamp_time,pict_type",
            "-of", "csv=p=0",
        };
        var headKeyOrdinals = new List<long>();
        long headFrames = 0;
        foreach (var line in RunFfprobeRange(ffprobe, formatStartSec, formatStartSec + 12.0, frameArgs))
        {
            var pict = ParseFrameProbeLine(line, out _);
            if (pict == "I") headKeyOrdinals.Add(headFrames);
            headFrames++;
        }
        if (headKeyOrdinals.Count < 2 || headFrames < 10) return null;
        var leads = new List<long>();
        for (var k = 1; k < Math.Min(headKeyOrdinals.Count, keyIndices.Count); k++)
        {
            leads.Add(headKeyOrdinals[k] - keyIndices[k]);
        }
        if (leads.Count == 0) return null;
        leads.Sort();
        var lead = leads[leads.Count / 2];
        if (lead < 0 || lead > 100) return null;
        // 3) 開始キーフレーム: 表示位置が指定位置以前の最後のキーフレーム。
        long startIndex = -1;
        long startKeyAppMs = -1;
        foreach (var keyIndex in keyIndices)
        {
            var appMs = videoFirstAppMs + (long)Math.Round((keyIndex + lead) * frameDurMs);
            if (appMs > startMs) break;
            startIndex = keyIndex;
            startKeyAppMs = appMs;
        }
        if (startIndex < 0)
        {
            // 開始位置が最初のキーフレームより前にある: 先頭から含める。
            startIndex = 0;
            startKeyAppMs = videoFirstAppMs;
        }
        // 4) 終了アンカー: 終了位置以前の最後の I / P フレーム (表示順の序数)。
        //    best_effort の時刻は表示位置より 1 フレーム分遅れて付くことがあるため、
        //    序数への換算で 1 フレーム分差し引く。
        long anchorOrdinal = -1;
        long anchorAppMs = -1;
        var fromSec = formatStartSec + Math.Max(0.0, endMs / 1000.0 - 3.0);
        var toSec = formatStartSec + endMs / 1000.0 + 1.5;
        foreach (var line in RunFfprobeRange(ffprobe, fromSec, toSec, frameArgs))
        {
            var pict = ParseFrameProbeLine(line, out var timeSec);
            if (pict != "I" && pict != "P") continue;
            if (double.IsNaN(timeSec)) continue;
            var ordinal = (long)Math.Round(((timeSec - formatStartSec) * 1000.0 - videoFirstAppMs) / frameDurMs) - 1;
            var appMs = videoFirstAppMs + (long)Math.Round(ordinal * frameDurMs);
            if (appMs > endMs) continue;
            if (ordinal > anchorOrdinal)
            {
                anchorOrdinal = ordinal;
                anchorAppMs = appMs;
            }
        }
        if (anchorOrdinal < 0) return null;
        var endIndex = anchorOrdinal + 1;
        if (startIndex >= endIndex) return null;
        if (startKeyAppMs != startMs)
        {
            WriteLog($"映像の開始位置を先行する I フレームに合わせて調整しました: 開始 {TimeText.FormatClockFromMs(startMs)} → {TimeText.FormatClockFromMs(startKeyAppMs)} (先頭のフレームの参照切れを防ぐため)。");
        }
        if (anchorAppMs < endMs)
        {
            WriteLog($"映像の終了位置を末尾の参照フレームに合わせて調整しました: 終了 {TimeText.FormatClockFromMs(endMs)} → {TimeText.FormatClockFromMs(anchorAppMs)} (末尾の B フレームの参照切れを防ぐため)。");
        }
        return (startKeyAppMs - videoFirstAppMs, anchorAppMs - startKeyAppMs, startKeyAppMs - videoFirstAppMs, startIndex, endIndex, (long)Math.Round(frameDurMs));
    }

    /// <summary>フレーム探査の 1 行 (時刻, フレーム種別) を読み取る。時刻が無ければ NaN。</summary>
    private static string ParseFrameProbeLine(string line, out double timeSec)
    {
        timeSec = double.NaN;
        var pict = "";
        foreach (var part in line.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) timeSec = value;
            else pict = trimmed;
        }
        return pict;
    }

    /// <summary>
    /// 映像の先頭のキーフレームパケットの位置 (アプリの時刻軸の µs)。無ければ -1。
    /// </summary>
    private long ProbeFirstKeyframeVideoUs(string ffprobe, double formatStartSec)
    {
        var args = new[]
        {
            "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time,flags",
            "-of", "csv=p=0",
        };
        foreach (var line in RunFfprobeRange(ffprobe, formatStartSec, formatStartSec + 30.0, args))
        {
            var parts = line.Split(',');
            string? flags = null;
            var ptsSec = double.NaN;
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) ptsSec = value;
                else if (trimmed.Length > 0) flags = trimmed;
            }
            if (flags == null || double.IsNaN(ptsSec)) continue;
            if (!flags.Contains('K')) continue;
            return (long)Math.Round((ptsSec - formatStartSec) * 1000000.0);
        }
        return -1;
    }

    /// <summary>
    /// trim (start_rel) の起点になる位置 (アプリの時刻軸の µs)。
    /// 起点は ffmpeg が実際にビットストリームフィルターへ渡す先頭パケットである。
    /// 先頭が GOP の途中から始まる素材では、それより前のパケットはパーサーが
    /// 組み立てられず渡されないため、起点はストリームの先頭パケットではなく
    /// 先頭のキーフレームになる。調べられなければストリームの先頭パケットの位置。
    /// </summary>
    private long GetLosslessBsfZeroAppUs(string ffprobe, double formatStartSec, double videoFirstAbsSec)
    {
        // 起点は入力ファイルだけで決まるため、区間ごとに調べ直さず使い回す。
        lock (_losslessProbeLock) { if (_losslessBsfZeroCache.TryGetValue(_inputPath, out var cached)) return cached; }
        var firstKeyUs = ProbeFirstKeyframeVideoUs(ffprobe, formatStartSec);
        if (firstKeyUs < 0) return (long)Math.Round((videoFirstAbsSec - formatStartSec) * 1000000.0);
        lock (_losslessProbeLock) { _losslessBsfZeroCache[_inputPath] = firstKeyUs; }
        return firstKeyUs;
    }

    /// <summary>映像の先頭付近にあるパケットのうち、最も早い表示時刻 (アプリの時刻軸の ms)。無ければ -1。</summary>
    private long ProbeMinVideoPacketMs(string ffprobe, double formatStartSec, double videoFirstAbsSec)
    {
        var args = new[]
        {
            "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
        };
        long minMs = -1;
        foreach (var line in RunFfprobeRange(ffprobe, formatStartSec, videoFirstAbsSec + 1.0, args))
        {
            if (!double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ptsSec)) continue;
            var appMs = (long)Math.Round((ptsSec - formatStartSec) * 1000.0);
            if (minMs < 0 || appMs < minMs) minMs = appMs;
        }
        return minMs;
    }

    /// <summary>ffprobe を読み取り区間つきで実行し、標準出力の行を順に返す。区間はファイルのタイムスタンプ (秒) で指定する。</summary>
    private IEnumerable<string> RunFfprobeRange(string ffprobe, double fromSec, double toSec, string[] extraArgs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            WorkingDirectory = Path.GetDirectoryName(ffprobe) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-read_intervals");
        startInfo.ArgumentList.Add(fromSec.ToString("0.###", CultureInfo.InvariantCulture) + "%" + toSec.ToString("0.###", CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(_inputPath);
        foreach (var arg in extraArgs) startInfo.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var deadline = Environment.TickCount64 + 15000;
        string? line;
        var lines = 0;
        try
        {
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                if (++lines > 20000 || Environment.TickCount64 > deadline) break;
                yield return line;
            }
        }
        finally
        {
            // 列挙が途中で終わった場合も、ffprobe を残さない。
            try { if (!process.HasExited) process.Kill(); } catch { /* 終了済みなら何もしない */ }
            try { process.WaitForExit(3000); } catch { /* 同上 */ }
        }
    }

    /// <summary>
    /// trim が実際に書き出す先頭の映像パケットの位置 (アプリの時刻軸の ms)。
    /// start_rel の起点は ffmpeg が実際に渡す先頭パケットなので、開始境界はその位置 + startMs になる。
    /// </summary>
    private long ProbeFirstExportedVideoMs(string ffprobe, long startMs, double formatStartSec, double zeroAbsSec)
    {
        var boundAbsSec = zeroAbsSec + startMs / 1000.0;
        var args = new[]
        {
            "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
        };
        foreach (var line in RunFfprobeRange(ffprobe, Math.Max(zeroAbsSec, boundAbsSec - 0.05), boundAbsSec + 1.0, args))
        {
            if (!double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ptsSec)) continue;
            if (ptsSec >= boundAbsSec - 0.0005)
            {
                return (long)Math.Round((ptsSec - formatStartSec) * 1000.0);
            }
        }
        return -1;
    }

    /// <summary>
    /// 指定位置以前の最後の I / P フレームの位置と、そのフレームの表示終了位置
    /// (= 次のフレームの表示開始位置) を、アプリの時刻軸の µs で返す。
    /// 表示終了位置が分からなければ -1 (アンカーがファイルの最終フレームで、
    /// その duration も読めなかった場合)。
    /// </summary>
    private (long AnchorUs, long AnchorEndUs) ProbeLastAnchorRangeUs(string ffprobe, long limitMs, double formatStartSec)
    {
        var streamIndex = _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture);
        var frameArgs = new[]
        {
            "-select_streams", streamIndex,
            "-show_entries", "frame=pts_time,pict_type",
            "-of", "csv=p=0",
        };
        long anchorUs = -1;
        var limitUs = limitMs * 1000;
        var fromSec = formatStartSec + Math.Max(0.0, limitMs / 1000.0 - 3.0);
        var toSec = formatStartSec + limitMs / 1000.0 + 1.5;
        foreach (var line in RunFfprobeRange(ffprobe, fromSec, toSec, frameArgs))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            // csv の列順に依存しないよう、数値として読める方を時刻、1 文字の方を種別として扱う。
            string? typeText = null;
            var ptsSec = double.NaN;
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 1 && (trimmed == "I" || trimmed == "P" || trimmed == "B")) typeText = trimmed;
                else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) ptsSec = value;
            }
            if (typeText == null || double.IsNaN(ptsSec)) continue;
            if (typeText != "I" && typeText != "P") continue;
            var appUs = (long)Math.Round((ptsSec - formatStartSec) * 1000000.0);
            if (appUs > limitUs) break; // I / P フレームの時刻はデコード順に単調なので、ここで打ち切れる
            if (appUs > anchorUs) anchorUs = appUs;
        }
        if (anchorUs < 0) return (-1, -1);
        // アンカーの次に表示されるフレームの位置 (= アンカーの表示終了位置) と、
        // アンカー自身の duration (次のフレームが無い場合の予備) をパケットから調べる。
        var packetArgs = new[]
        {
            "-select_streams", streamIndex,
            "-show_entries", "packet=pts_time,duration_time",
            "-of", "csv=p=0",
        };
        long nextUs = -1;
        long anchorDurationUs = -1;
        var anchorAbsSec = formatStartSec + anchorUs / 1000000.0;
        foreach (var line in RunFfprobeRange(ffprobe, Math.Max(0.0, anchorAbsSec - 1.0), anchorAbsSec + 2.0, packetArgs))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            // 2 つの数値のうち、窓の中心に近い方が pts_time、もう一方が duration_time。
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var first)) continue;
            if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) continue;
            var ptsSec = Math.Abs(first - anchorAbsSec) <= Math.Abs(second - anchorAbsSec) ? first : second;
            var durationSec = ptsSec == first ? second : first;
            var appUs = (long)Math.Round((ptsSec - formatStartSec) * 1000000.0);
            if (Math.Abs(appUs - anchorUs) <= 2)
            {
                if (durationSec > 0 && durationSec <= 0.5) anchorDurationUs = (long)Math.Round(durationSec * 1000000.0);
            }
            else if (appUs > anchorUs && (nextUs < 0 || appUs < nextUs))
            {
                nextUs = appUs;
            }
        }
        var anchorEndUs = nextUs >= 0 ? nextUs : anchorDurationUs > 0 ? anchorUs + anchorDurationUs : -1;
        return (anchorUs, anchorEndUs);
    }

    /// <summary>
    /// 指定位置以後で最初の映像フレームの位置 (アプリの時刻軸の µs)。無ければ -1。
    /// 指定位置がフレームの時刻をミリ秒へ切り捨てた値でも、そのフレーム自身を拾えるよう、
    /// 0.5 ミリ秒手前からを候補にする (前のフレームはどんなフレームレートでも 8 ミリ秒以上
    /// 手前にあるため、誤って拾うことはない)。
    /// </summary>
    private long ProbeFirstFrameAtOrAfterUs(string ffprobe, long positionMs, double formatStartSec)
    {
        var args = new[]
        {
            "-select_streams", _videoStreams[0].Index.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "packet=pts_time",
            "-of", "csv=p=0",
        };
        var thresholdUs = positionMs * 1000 - 500;
        long bestUs = -1;
        var centerSec = formatStartSec + positionMs / 1000.0;
        foreach (var line in RunFfprobeRange(ffprobe, Math.Max(0.0, centerSec - 1.0), centerSec + 2.0, args))
        {
            if (!double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ptsSec)) continue;
            var appUs = (long)Math.Round((ptsSec - formatStartSec) * 1000000.0);
            if (appUs >= thresholdUs && (bestUs < 0 || appUs < bestUs)) bestUs = appUs;
        }
        return bestUs;
    }

    private readonly Dictionary<(string Path, long StartMs, long EndMs, int StreamIndex), long> _losslessAudioLengthCache = new();

    /// <summary>
    /// 無劣化カットの音声の長さを、終了位置以前の最後の音声フレームの表示終了位置に合わせた
    /// 長さ (ms) で返す。trim は範囲の終わりにかかったパケットの duration を範囲内に
    /// 切り詰めるため、指定どおりの長さでは最後のフレームの duration が短く記録されて、
    /// 編集リストの終端がその分だけ前へずれてしまう。境界ちょうどのパケットは範囲に
    /// 含まれないため、合わせても次のフレームを取り込むことはない。
    /// 調べられなければ元の長さを返す。
    /// </summary>
    private long AdjustLosslessAudioLengthMs(long startMs, long endMs, ProbeStream audio)
    {
        var lengthMs = endMs - startMs;
        if (_probe == null || _inputPath.Length == 0) return lengthMs;
        var key = (_inputPath, startMs, endMs, audio.Index);
        if (_losslessAudioLengthCache.TryGetValue(key, out var cached)) return cached;
        var result = lengthMs;
        try
        {
            var ffprobe = GetFfprobePath();
            if (ffprobe.Length > 0)
            {
                var formatStartSec = Probe.ParseSeconds(_probe.FormatStartTime);
                var formatStartUs = (long)Math.Round(formatStartSec * 1000000.0);
                var args = new[]
                {
                    "-select_streams", audio.Index.ToString(CultureInfo.InvariantCulture),
                    "-show_entries", "packet=pts_time,duration_time",
                    "-of", "csv=p=0",
                };
                // trim (start_rel) の起点になる、先頭パケットの表示時刻 (ファイルのタイムスタンプ軸)。
                long firstAbsUs = -1;
                foreach (var line in RunFfprobeRange(ffprobe, formatStartSec, formatStartSec + 2.0, args))
                {
                    var parts = line.Split(',');
                    if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var firstSec)) continue;
                    var secondSec = double.NaN;
                    if (parts.Length >= 2) double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out secondSec);
                    // 先頭付近では pts_time は formatStartSec の近く、duration_time は小さい。
                    var ptsSec = !double.IsNaN(secondSec) && Math.Abs(secondSec - formatStartSec) < Math.Abs(firstSec - formatStartSec) ? secondSec : firstSec;
                    var absUs = (long)Math.Round(ptsSec * 1000000.0);
                    if (firstAbsUs < 0 || absUs < firstAbsUs) firstAbsUs = absUs;
                }
                if (firstAbsUs >= 0)
                {
                    // 終了位置 (ファイルのタイムスタンプ軸) 以前の最後のフレームと、その表示終了位置。
                    var boundAbsUs = formatStartUs + endMs * 1000;
                    long lastAbsUs = -1;
                    long lastDurationUs = -1;
                    long nextAbsUs = -1;
                    var endCenterSec = formatStartSec + endMs / 1000.0;
                    foreach (var line in RunFfprobeRange(ffprobe, Math.Max(0.0, endCenterSec - 3.0), endCenterSec + 1.5, args))
                    {
                        var parts = line.Split(',');
                        if (parts.Length < 2) continue;
                        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var first)) continue;
                        if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) continue;
                        var ptsSec = Math.Abs(first - endCenterSec) <= Math.Abs(second - endCenterSec) ? first : second;
                        var durationSec = ptsSec == first ? second : first;
                        var absUs = (long)Math.Round(ptsSec * 1000000.0);
                        if (absUs < boundAbsUs)
                        {
                            if (absUs > lastAbsUs)
                            {
                                lastAbsUs = absUs;
                                lastDurationUs = durationSec > 0 && durationSec <= 0.5 ? (long)Math.Round(durationSec * 1000000.0) : -1;
                            }
                        }
                        else if (nextAbsUs < 0 || absUs < nextAbsUs)
                        {
                            nextAbsUs = absUs;
                        }
                    }
                    var lastEndAbsUs = nextAbsUs >= 0 ? nextAbsUs : lastDurationUs > 0 ? lastAbsUs + lastDurationUs : -1;
                    if (lastAbsUs >= 0 && lastEndAbsUs > lastAbsUs)
                    {
                        var adjusted = (lastEndAbsUs - firstAbsUs) / 1000 - startMs;
                        if (adjusted >= lengthMs && adjusted <= lengthMs + 50) result = adjusted;
                    }
                }
            }
        }
        catch { result = lengthMs; }
        _losslessAudioLengthCache[key] = result;
        return result;
    }

    private readonly Dictionary<(string Path, long StartMs, long EndMs, int StreamIndex), (long StartMs, long LengthMs)> _losslessAudioPacketIndexRangeCache = new();

    /// <summary>
    /// パケットの並び順で範囲を指定する経路 (AVI など) 用の、音声の開始位置と長さ。
    /// 映像はキーフレームの表示位置から始まるので、音声も同じ位置から、
    /// 末尾のアンカーの表示終了までに合わせる (理由は NewLosslessCutArguments の説明)。
    /// 長さは AdjustLosslessAudioLengthMs で最後のフレームの表示終了位置に合わせる。
    /// </summary>
    private (long StartMs, long LengthMs) AdjustLosslessAudioPacketIndexRangeMs(long startMs, long endMs, ProbeStream audio, long contentStartMs, long videoLengthMs, long frameDurationMs)
    {
        var key = (_inputPath, startMs, endMs, audio.Index);
        if (_losslessAudioPacketIndexRangeCache.TryGetValue(key, out var cached)) return cached;
        var formatStartSec = _probe == null ? 0.0 : Probe.ParseSeconds(_probe.FormatStartTime);
        var videoFirstAppMs = (long)Math.Round((Probe.ParseSeconds(_videoStreams[0].StartTime) - formatStartSec) * 1000.0);
        var audioStartMs = videoFirstAppMs + contentStartMs;
        var audioEndMs = audioStartMs + videoLengthMs + frameDurationMs;
        var audioLengthMs = AdjustLosslessAudioLengthMs(audioStartMs, audioEndMs, audio);
        if (audioStartMs != startMs)
        {
            WriteLog($"音声の開始位置を映像の開始位置に合わせて調整しました: 開始 {TimeText.FormatClockFromMs(startMs)} → {TimeText.FormatClockFromMs(audioStartMs)} (映像と音声の内容の開始位置をそろえるため)。");
        }
        var result = (audioStartMs, audioLengthMs);
        _losslessAudioPacketIndexRangeCache[key] = result;
        return result;
    }

    private (List<string> Arguments, string BsfFilter, long VideoStartMs, long VideoLengthMs, long ContentStartMs, List<(string Path, string Content)> TempFiles) NewLosslessCutArguments(
        long startMs, long endMs, IReadOnlyList<ProbeStream> audios, string output, double speed = 1.0)
    {
        // 1 区間を trim ビットストリームフィルターで無劣化 (ストリームコピー) 抽出する。
        // 再生速度を変える場合 (音声を保持しないときだけ呼び出される) は、setts でタイムスタンプも縮尺する。
        var ext = Path.GetExtension(output).ToLowerInvariant();
        var lengthMs = endMs - startMs;
        var startText = startMs.ToString(CultureInfo.InvariantCulture);
        var lengthText = lengthMs.ToString(CultureInfo.InvariantCulture);
        var speedChanged = Math.Abs(speed - 1.0) >= 0.000001;
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "verbose", "-probesize", "100M", "-analyzeduration", "100M", "-i", _inputPath };
        // 映像は主映像 (最初の映像ストリーム) だけを出力する (ワンセグなどの副映像は含めない)。
        if (_videoStreams.Count > 0) { args.Add("-map"); args.Add($"0:{_videoStreams[0].Index}"); }
        foreach (var audio in audios) { args.Add("-map"); args.Add($"0:{audio.Index}"); }
        // 保持するデータ (速度変更時は除く): サムネイル・字幕・添付ファイルも一緒に切り出す。
        var tempFiles = new List<(string Path, string Content)>();
        var keptPics = new List<ProbeStream>();
        var subtitleBsfArgs = new List<string>();
        if (!speedChanged)
        {
            if (IsKeepDataChecked("thumbnail")) keptPics = GetAttachedPicStreams();
            foreach (var pic in keptPics) { args.Add("-map"); args.Add($"0:{pic.Index}"); }
            if (IsKeepDataChecked("subtitle"))
            {
                var subtitles = GetSubtitleStreams();
                var outIndex = 0;
                for (var k = 0; k < subtitles.Count; k++)
                {
                    var bsf = BuildSubtitleTrimBsf(subtitles[k], startMs, endMs, rebaseToZero: false);
                    if (bsf == null) continue;
                    args.Add("-map");
                    args.Add($"0:s:{k}");
                    subtitleBsfArgs.Add($"-bsf:s:{outIndex}");
                    subtitleBsfArgs.Add(bsf);
                    outIndex++;
                }
            }
            if ((IsKeepDataChecked("subtitle") || IsKeepDataChecked("thumbnail")) && ext == ".mkv" && GetAttachmentStreams().Count > 0)
            {
                args.Add("-map");
                args.Add("0:t");
            }
        }
        args.AddRange(new[] { "-c", "copy" });
        string bsfFilter;
        var audioBsfFilter = "";
        var videoStartMs = startMs;
        var videoLengthMs = lengthMs;
        var contentStartMs = startMs;
        if (speedChanged)
        {
            var speedInvariant = speed.ToString("0.###", CultureInfo.InvariantCulture);
            var settsPart = $"setts=pts=(PTS-STARTPTS)/{speedInvariant}:dts=(DTS-STARTDTS)/{speedInvariant}:duration=DURATION/{speedInvariant}";
            bsfFilter = $"trim=start={startText}:start_type=msec_pt:start_rel=1:end={lengthText}:end_type=dur_t_msec:trim_packets=1,{settsPart}";
            audioBsfFilter = bsfFilter;
        }
        else
        {
            // 通常の無劣化カットでは setts を使わない。setts でタイムスタンプを引き直すと
            // フレーム間隔がわずかに崩れて再生がカクつくうえ、映像と音声の開始位置のずれも消えてしまう。
            // trim で範囲だけを抽出し、開始位置のそろえ方はコンテナごとに変える (間隔とずれは元のまま保たれる)。
            // 映像だけは、参照切れを防ぐため、開始位置を先行する I フレームに、終了位置を
            // 最後の I / P フレームに合わせる。
            var range = AdjustLosslessVideoRangeMs(startMs, endMs);
            long startPacketIndex = -1;
            long endPacketIndex = -1;
            if (range.HasValue)
            {
                videoStartMs = range.Value.VideoStartMs;
                videoLengthMs = range.Value.VideoLengthMs;
                contentStartMs = range.Value.ContentStartMs;
                startPacketIndex = range.Value.StartPacketIndex;
                endPacketIndex = range.Value.EndPacketIndex;
            }
            else
            {
                videoLengthMs = AdjustLosslessVideoLengthMs(startMs, endMs);
            }
            if (startPacketIndex >= 0 && endPacketIndex > startPacketIndex)
            {
                // パケットに表示時刻が付いていない素材 (AVI など) 用の範囲指定。
                // 時刻では trim に範囲を伝えられないため、パケットの並び順で指定する。
                bsfFilter = $"trim=start={startPacketIndex}:start_type=pkt_index:end={endPacketIndex}:end_type=pkt_index";
            }
            else
            {
                var videoStartText = videoStartMs.ToString(CultureInfo.InvariantCulture);
                var videoLengthText = videoLengthMs.ToString(CultureInfo.InvariantCulture);
                bsfFilter = $"trim=start={videoStartText}:start_type=msec_pt:start_rel=1:end={videoLengthText}:end_type=dur_t_msec:trim_packets=1";
            }
            // 音声の長さも、最後のフレームの表示終了位置に合わせて延ばす (duration の切り詰めで
            // 編集リストの終端が前へずれるのを防ぐ)。
            var audioStartMs = startMs;
            var audioLengthMs = audios.Count > 0 ? AdjustLosslessAudioLengthMs(startMs, endMs, audios[0]) : lengthMs;
            if (startPacketIndex >= 0 && endPacketIndex > startPacketIndex && audios.Count > 0 && range.HasValue)
            {
                // パケットの並び順で範囲を指定する経路 (AVI など) では、映像はキーフレームから
                // 始まるのに音声は指定位置から始まるため、出力の中で音声だけが映像より
                // 先の内容になってしまう。さらに、音声の開始時刻が映像と大きく離れていると、
                // AVI の muxer が隙間を埋める空の音声チャンクを大量に書き込んでしまい、
                // プレーヤーによっては再生が引っかかる。そこで音声も映像に合わせる。
                (audioStartMs, audioLengthMs) = AdjustLosslessAudioPacketIndexRangeMs(startMs, endMs, audios[0], contentStartMs, videoLengthMs, range.Value.FrameDurationMs);
            }
            var audioStartText = audioStartMs.ToString(CultureInfo.InvariantCulture);
            var audioLengthText = audioLengthMs.ToString(CultureInfo.InvariantCulture);
            audioBsfFilter = $"trim=start={audioStartText}:start_type=msec_pt:start_rel=1:end={audioLengthText}:end_type=dur_t_msec:trim_packets=1";
        }
        // サムネイルを映像ストリームとして一緒に持つ場合は、trim を主映像だけに効かせる。
        args.AddRange(new[] { keptPics.Count > 0 ? "-bsf:v:0" : "-bsf:v", bsfFilter });
        if (audios.Count > 0) args.AddRange(new[] { "-bsf:a", audioBsfFilter });
        if (subtitleBsfArgs.Count > 0) args.AddRange(subtitleBsfArgs);
        // 無劣化経路の出力の時刻軸は、MP4 系ではカット位置が、それ以外では出力の先頭になる
        // フレーム (キーフレーム) の位置が起点になる。チャプターはその軸へ合わせて直す。
        var chaptersBaseMs = ext is ".mp4" or ".mov" or ".m4v" ? startMs : videoStartMs;
        var chaptersIndex = !speedChanged && IsKeepDataChecked("chapter")
            ? AddSegmentChaptersInput(args, startMs, endMs, chaptersBaseMs, tempFiles)
            : -1;
        args.AddRange(new[] { "-map_metadata", GetMapMetadataValue(), "-map_chapters", chaptersIndex >= 0 ? chaptersIndex.ToString(CultureInfo.InvariantCulture) : "-1" });
        if (ext is ".mp4" or ".mov" or ".m4v")
        {
            // MP4 系で -avoid_negative_ts / -start_at_zero を使うと、DTS が 0 になるよう全体が
            // ずれて先頭フレームの表示開始が後ろに下がり、編集リストに空の編集が入る。
            // プレーヤーによっては、その間ずっと先頭のフレームが表示されて同じ絵が続いて見える。
            // 代わりに、カット位置が 0 になるよう全体をずらす。キーフレームからカット位置までの
            // フレームは復号のためにファイルに残り、表示は編集リスト (media_time) でカット位置の
            // フレームから始まる。音声も同じずらしで、カット位置にそろって始まる。
            // 速度変更時 (setts) はタイムスタンプが 0 起点に引き直されるため、ずらしは不要。
            if (!speedChanged && _videoStreams.Count > 0)
            {
                // ずらしの起点は、カット位置以後で最初のフレームの正確な時刻 (µs) にする。
                // 指定位置 (ms) のままでは、編集リストの media_time がそのフレームの CTS と
                // 最大で 1 フレーム弱ずれて、プレーヤーによってはカット位置より前のフレームが
                // 表示側に入ってしまう。フレームの時刻に合わせれば media_time はそのフレームの
                // CTS と一致し、どのプレーヤーでも表示はカット位置のフレームから始まる。
                var formatStartSec = Probe.ParseSeconds(_probe?.FormatStartTime ?? "");
                var ffprobe = GetFfprobePath();
                var cutFrameUs = ffprobe.Length > 0 ? ProbeFirstFrameAtOrAfterUs(ffprobe, startMs, formatStartSec) : -1;
                var cutOffsetSec = cutFrameUs >= 0
                    ? cutFrameUs / 1000000.0
                    : Probe.ParseSeconds(_videoStreams[0].StartTime) - formatStartSec + startMs / 1000.0;
                args.AddRange(new[] { "-output_ts_offset", (-cutOffsetSec).ToString("0.######", CultureInfo.InvariantCulture) });
            }
            args.AddRange(new[] { "-movflags", "+faststart" });
        }
        else
        {
            args.AddRange(new[] { "-avoid_negative_ts", "make_zero" });
            if (!speedChanged) args.Add("-start_at_zero");
        }
        args.Add(output);
        args = AddRotationInputArgument(args, false);
        return (args, bsfFilter, videoStartMs, videoLengthMs, contentStartMs, tempFiles);
    }

    /// <summary>
    /// 再エンコードの区間終了境界 (ミリ秒) を求める。終了位置は、その位置に表示されるフレームの
    /// 位置をミリ秒に切り捨てた値で記録されるため、trim の終了境界にそのまま使うと、そのフレームが
    /// 境界の外側になって落ちる。フレーム表が使える場合は、そのフレームの表示終了 (ミリ秒未満切り捨て)
    /// まで延ばして、そのフレームを含める。次のフレームはその表示開始以降に始まるので、延ばしても入らない。
    /// </summary>
    private long GetReencodeEndMs(long endMs)
    {
        if (!TestFrameTableReady()) return endMs;
        var index = FindFrameIndexAtOrAfter(endMs * 1000L);
        if (index < 0) return endMs;
        var times = _frameTimesUs!;
        var displayEndUs = index + 1 < times.Length
            ? times[index + 1]
            : times[index] + (index > 0 ? times[index] - times[index - 1] : 1000L);
        var extendedMs = (displayEndUs - 1) / 1000L;
        return extendedMs > endMs ? extendedMs : endMs;
    }

    private (List<string> Arguments, string FilterComplex, List<(string Path, string Content)> TempFiles) NewReencodedClippedSegmentArguments(
        long startMs, long endMs, Segment segment, int[] audioRelativeIndices, string output, double speed = 1.0)
    {
        // 1 区間の映像を (必要なら crop して) 再エンコードし、選択音声も atrim / asetpts を通して再エンコードする。
        var ext = Path.GetExtension(output).ToLowerInvariant();
        var startSec = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var endSec = (GetReencodeEndMs(endMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var speedChanged = Math.Abs(speed - 1.0) >= 0.000001;
        var speedInvariant = speed.ToString("0.###", CultureInfo.InvariantCulture);
        var filterParts = new List<string>();
        // 時刻の原点は STARTPTS ではなく区間の開始時刻を使う。グラフの再初期化で STARTPTS が取り直されると時刻軸が戻ってしまうため (全体編集側のコメント参照)。
        var videoChain = $"[0:v:0]trim=start={startSec}:end={endSec},setpts=PTS-{startSec}/TB";
        var cropFilter = GetSegmentCropFilter(segment);
        if (cropFilter.Length > 0) videoChain += "," + cropFilter;
        if (speedChanged) videoChain += ",setpts=PTS/" + speedInvariant;
        videoChain += "[vout]";
        filterParts.Add(videoChain);
        var atempoSuffix = speedChanged ? "," + GetAtempoFilterChain(speed) : "";
        for (var audioOrdinal = 0; audioOrdinal < audioRelativeIndices.Length; audioOrdinal++)
        {
            var audioRel = audioRelativeIndices[audioOrdinal];
            filterParts.Add($"[0:a:{audioRel}]atrim=start={startSec}:end={endSec},asetpts=PTS-{startSec}/TB{atempoSuffix}[aout{audioOrdinal}]");
        }
        var filterComplex = string.Join(";", filterParts);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "verbose", "-probesize", "100M", "-analyzeduration", "100M", "-i", _inputPath, "-filter_complex", filterComplex, "-map", "[vout]" };
        for (var audioOrdinal = 0; audioOrdinal < audioRelativeIndices.Length; audioOrdinal++)
        {
            args.Add("-map");
            args.Add($"[aout{audioOrdinal}]");
        }
        var audioStreamsForEncoding = new List<ProbeStream>();
        foreach (var audioRel in audioRelativeIndices) audioStreamsForEncoding.Add(_audioStreams[audioRel]);
        args.AddRange(GetVideoEncoderArgs(ext));
        if (audioStreamsForEncoding.Count > 0) args.AddRange(GetAudioEncoderArgs(audioStreamsForEncoding));
        AddKeepDataToEncodeArgs(args, ext, startMs, endMs, wholeFile: false, speedChanged, firstVideoOutputOrdinal: 1);
        var tempFiles = new List<(string Path, string Content)>();
        var chaptersIndex = !speedChanged && IsKeepDataChecked("chapter")
            ? AddSegmentChaptersInput(args, startMs, endMs, startMs, tempFiles)
            : -1;
        args.AddRange(new[] { "-map_metadata", GetMapMetadataValue(), "-map_chapters", chaptersIndex >= 0 ? chaptersIndex.ToString(CultureInfo.InvariantCulture) : "-1" });
        if (ext is ".mp4" or ".mov" or ".m4v") args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(output);
        args = AddRotationInputArgument(args, true);
        return (args, filterComplex, tempFiles);
    }

    // ------------------------------------------------------------------
    // 1 グループ分の結合ステップ
    // ------------------------------------------------------------------

    /// <summary>1 グループ分のステップを作る (グループ内の区間を結合する。1 区間だけの場合はその区間の抽出)。</summary>
    private JobStep BuildCutJoinStep(List<Segment> segments, string output)
    {
        var hasClipping = false;
        foreach (var registeredSegment in segments)
        {
            if (registeredSegment.HasClipping) { hasClipping = true; break; }
        }
        if (segments.Count == 1 && !hasClipping && !TestTrimSupport())
        {
            throw new InvalidOperationException("単一区間の無劣化カットには、指定コミット由来の trim と setts ビットストリームフィルターを含む FFmpeg が必要です。複数区間の結合は再エンコード方式を使うため、このフィルターを必要としません。");
        }
        if (_videoStreams.Count == 0) throw new InvalidOperationException("入力動画に映像ストリームがありません。");

        long selectedDurationMs = 0;
        foreach (var segment in segments)
        {
            var segLength = segment.EndMs - segment.StartMs;
            if (segLength <= 0 || segment.StartMs < 0) throw new InvalidOperationException("カット区間の開始・終了時刻が不正です。");
            if (_durationMs > 0 && segment.EndMs > _durationMs) throw new InvalidOperationException("カット区間が入力動画の長さを超えています。");
            selectedDurationMs += segLength;
        }
        var segmentCount = segments.Count;
        var speedValue = GetSelectedSpeed();
        var speedChanged = Math.Abs(speedValue - 1.0) >= 0.000001;
        var speedInvariant = speedValue.ToString("0.###", CultureInfo.InvariantCulture);
        var speedText = speedValue.ToString("0.00", CultureInfo.InvariantCulture);
        var modeDescription = segmentCount == 1 && hasClipping
            ? "単一区間: 映像は crop フィルターで再エンコード、音声は trim / setts BSF でストリームコピー"
            : hasClipping
                ? "複数区間のクリッピング: crop / concat フィルターで映像・音声を再エンコード"
                : segmentCount == 1
                    ? "単一区間: 指定 trim BSF による無劣化ストリームコピー"
                    : "複数区間: デコード + concat フィルター + 再エンコード（継ぎ目の停止防止を優先）";
        WriteLog($"カット方式: {modeDescription}");
        WriteLog($"登録区間数={segmentCount}; 合計選択時間={TimeText.FormatClockFromMs(selectedDurationMs)}; 入力動画時間={TimeText.FormatClockFromMs(_durationMs)}");
        WriteLog($"入力ファイル: {_inputPath}");
        if (_durationMs > 0 && selectedDurationMs >= _durationMs - 100)
        {
            WriteLog("警告: 選択区間の合計が入力動画の長さとほぼ同じです。開始・終了時刻が意図どおりか確認してください。");
        }

        var selectedCutAudios = new List<ProbeStream>();
        var selectedAudioRelativeIndices = new List<int>();
        for (var audioIndex = 0; audioIndex < _audioStreams.Count; audioIndex++)
        {
            if (audioIndex < _audioList.Items.Count && _audioList.GetItemChecked(audioIndex))
            {
                selectedCutAudios.Add(_audioStreams[audioIndex]);
                selectedAudioRelativeIndices.Add(audioIndex);
            }
        }
        WriteLog($"カット出力ストリーム: primary_video={_videoStreams[0].Index}; selected_audio={selectedCutAudios.Count}/{_audioStreams.Count}");
        // 再生速度を変えて音声を保持する場合は、音声を atempo で再エンコードするため、フィルター方式で処理する。
        var isLosslessPath = segmentCount == 1 && !hasClipping && (!speedChanged || selectedCutAudios.Count == 0);
        var isSingleClipPath = segmentCount == 1 && hasClipping && !speedChanged;
        var rotationIndexForLog = GetRotationIndex();
        WriteLog($"再生速度={speedText}x / 回転={(rotationIndexForLog > 0 ? GetRotationDisplayText(rotationIndexForLog) + "（回転メタデータのみ）" : "なし")}");
        if (isSingleClipPath && selectedCutAudios.Count > 0 && !TestTrimSupport())
        {
            throw new InvalidOperationException("単一区間のクリッピングで音声を無劣化コピーするには、trim と setts ビットストリームフィルターを含む FFmpeg が必要です。");
        }
        if (isSingleClipPath)
        {
            WriteLog("単一区間のクリッピング: 映像のみ crop フィルターで再エンコードし、選択音声は trim / setts BSF を適用してストリームコピーします。");
        }
        else if (hasClipping)
        {
            WriteLog("複数区間のクリッピング: crop / concat フィルターを使用するため映像・音声を再エンコードします。再エンコード設定は出力タブのプロファイルに従います。");
        }
        else if (segmentCount > 1)
        {
            WriteLog("重要: 複数区間は任意のフレーム位置から継ぎ目を滑らかにするため再エンコードします。再エンコード設定は出力タブのプロファイルに従います。単一区間は無劣化のままです。");
        }

        var ext = Path.GetExtension(output).ToLowerInvariant();
        WriteLog($"出力予定ファイル名: {output}");

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "verbose", "-probesize", "100M", "-analyzeduration", "100M", "-i", _inputPath };
        var stepTempFiles = new List<(string Path, string Content)>();
        var description = "";
        if (isLosslessPath)
        {
            var segment = segments[0];
            var startMs = segment.StartMs;
            var endMs = segment.EndMs;
            var lengthMs = endMs - startMs;
            WriteLog($"単一区間: start={startMs} ms ({TimeText.FormatClockFromMs(startMs)}); end={endMs} ms ({TimeText.FormatClockFromMs(endMs)}); length={lengthMs} ms ({TimeText.FormatClockFromMs(lengthMs)})");
            var cut = NewLosslessCutArguments(startMs, endMs, selectedCutAudios, output, speedValue);
            args = cut.Arguments;
            stepTempFiles = cut.TempFiles;
            description = "単一区間を trim BSF で無劣化抽出";
            WriteLog($"映像 BSF: {cut.BsfFilter}");
            if (selectedCutAudios.Count > 0) WriteLog($"音声 BSF: {cut.BsfFilter}");
        }
        else if (isSingleClipPath)
        {
            // 単一区間のクリッピングでは映像だけを crop 再エンコードし、音声は BSF で区間抽出してコピーする。
            var segment = segments[0];
            var startMs = segment.StartMs;
            var endMs = segment.EndMs;
            var lengthMs = GetReencodeEndMs(endMs) - startMs;
            var startSec = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var endSec = (GetReencodeEndMs(endMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var lengthText = lengthMs.ToString(CultureInfo.InvariantCulture);
            var videoChain = $"[0:v:0]trim=start={startSec}:end={endSec},setpts=PTS-{startSec}/TB";
            videoChain += "," + GetSegmentCropFilter(segment) + "[vout]";
            args.AddRange(new[] { "-filter_complex", videoChain, "-map", "[vout]" });
            foreach (var audioRel in selectedAudioRelativeIndices) { args.Add("-map"); args.Add($"0:a:{audioRel}"); }
            var audioBsfFilter = $"trim=start={startMs}:start_type=msec_pt:start_rel=1:end={lengthText}:end_type=dur_t_msec:trim_packets=1,setts=pts=PTS-STARTPTS:dts=DTS-STARTDTS:duration=DURATION";
            if (ext == ".webm")
            {
                var unsupportedWebmAudio = selectedCutAudios.Where(a => a.CodecName.ToLowerInvariant() is not ("opus" or "vorbis")).ToList();
                if (unsupportedWebmAudio.Count > 0)
                {
                    var unsupportedNames = string.Join(", ", unsupportedWebmAudio.Select(a => a.CodecName.Length > 0 ? a.CodecName : "unknown").Distinct());
                    throw new InvalidOperationException($"WebM 出力では音声を無劣化コピーできないコーデックが選択されています: {unsupportedNames}。Opus / Vorbis の音声を選択するか、別の出力形式を指定してください。");
                }
            }
            args.AddRange(GetVideoEncoderArgs(ext));
            if (selectedAudioRelativeIndices.Count > 0)
            {
                args.AddRange(new[] { "-c:a", "copy", "-bsf:a", audioBsfFilter });
                WriteLog($"音声エンコーダー: stream copy / BSF: {audioBsfFilter}");
            }
            AddKeepDataToEncodeArgs(args, ext, startMs, endMs, wholeFile: false, speedChanged: false, firstVideoOutputOrdinal: 1);
            var chaptersIndex = IsKeepDataChecked("chapter")
                ? AddSegmentChaptersInput(args, startMs, endMs, startMs, stepTempFiles)
                : -1;
            args.AddRange(new[] { "-map_metadata", GetMapMetadataValue(), "-map_chapters", chaptersIndex >= 0 ? chaptersIndex.ToString(CultureInfo.InvariantCulture) : "-1", "-avoid_negative_ts", "make_zero" });
            if (ext is ".mp4" or ".mov" or ".m4v") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            description = "単一区間の映像に crop フィルターを適用して再エンコードし、音声は無劣化コピー";
            WriteLog($"単一区間のクリッピング: start={TimeText.FormatClockFromMs(startMs)}; end={TimeText.FormatClockFromMs(endMs)}; length={TimeText.FormatClockFromMs(lengthMs)}");
            WriteLog($"映像フィルター: {videoChain}");
        }
        else
        {
            var filterParts = new List<string>();
            var segmentVideoLabels = new List<string>();
            var segmentAudioLabels = new List<string[]>();
            var concatTargetWidth = 0;
            var concatTargetHeight = 0;
            if (hasClipping && segmentCount > 1)
            {
                var sourceDimensions = GetVideoDimensions();
                var firstCrop = segments[0];
                concatTargetWidth = sourceDimensions.Width - firstCrop.Left - firstCrop.Right;
                concatTargetHeight = sourceDimensions.Height - firstCrop.Top - firstCrop.Bottom;
                if (concatTargetWidth % 2 != 0) concatTargetWidth--;
                if (concatTargetHeight % 2 != 0) concatTargetHeight--;
                if (concatTargetWidth < 2 || concatTargetHeight < 2) throw new InvalidOperationException("先頭区間のクリッピング後の解像度が小さすぎます。");
            }
            for (var i = 0; i < segmentCount; i++)
            {
                var segment = segments[i];
                var startMs = segment.StartMs;
                var endMs = segment.EndMs;
                var startSec = (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
                var endSec = (GetReencodeEndMs(endMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
                var videoLabel = $"vseg{i}";
                segmentVideoLabels.Add($"[{videoLabel}]");
                var videoChain = $"[0:v:0]trim=start={startSec}:end={endSec},setpts=PTS-{startSec}/TB";
                var segmentCropFilter = GetSegmentCropFilter(segment);
                if (segmentCropFilter.Length > 0) videoChain += "," + segmentCropFilter;
                if (speedChanged) videoChain += ",setpts=PTS/" + speedInvariant;
                if (hasClipping && segmentCount > 1)
                {
                    videoChain += $",scale={concatTargetWidth}:{concatTargetHeight}:force_original_aspect_ratio=decrease,pad={concatTargetWidth}:{concatTargetHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1";
                }
                filterParts.Add(videoChain + $"[{videoLabel}]");
                var thisSegmentAudioLabels = new List<string>();
                for (var audioOrdinal = 0; audioOrdinal < selectedAudioRelativeIndices.Count; audioOrdinal++)
                {
                    var audioRel = selectedAudioRelativeIndices[audioOrdinal];
                    var audioLabel = $"aseg{i}_{audioOrdinal}";
                    thisSegmentAudioLabels.Add($"[{audioLabel}]");
                    var audioTempoSuffix = speedChanged ? "," + GetAtempoFilterChain(speedValue) : "";
                    filterParts.Add($"[0:a:{audioRel}]atrim=start={startSec}:end={endSec},asetpts=PTS-{startSec}/TB{audioTempoSuffix}[{audioLabel}]");
                }
                segmentAudioLabels.Add(thisSegmentAudioLabels.ToArray());
                WriteLog($"再エンコード区間 {i + 1}/{segmentCount}: start={TimeText.FormatClockFromMs(startMs)}; end={TimeText.FormatClockFromMs(endMs)}; duration={TimeText.FormatClockFromMs(endMs - startMs)}");
            }
            var concatInputs = new List<string>();
            for (var i = 0; i < segmentCount; i++)
            {
                concatInputs.Add(segmentVideoLabels[i]);
                foreach (var audioLabel in segmentAudioLabels[i]) concatInputs.Add(audioLabel);
            }
            var concatOutputs = new List<string> { "[vout]" };
            for (var audioOrdinal = 0; audioOrdinal < selectedAudioRelativeIndices.Count; audioOrdinal++) concatOutputs.Add($"[aout{audioOrdinal}]");
            var concatNode = string.Concat(concatInputs) + $"concat=n={segmentCount}:v=1:a={selectedAudioRelativeIndices.Count}" + string.Concat(concatOutputs);
            filterParts.Add(concatNode);
            var filterComplex = string.Join(";", filterParts);
            WriteLog($"filter_complex: {filterComplex}");
            args.AddRange(new[] { "-filter_complex", filterComplex, "-map", "[vout]" });
            for (var audioOrdinal = 0; audioOrdinal < selectedAudioRelativeIndices.Count; audioOrdinal++)
            {
                args.Add("-map");
                args.Add($"[aout{audioOrdinal}]");
            }
            var audioStreamsForEncoding = new List<ProbeStream>();
            foreach (var audioRel in selectedAudioRelativeIndices) audioStreamsForEncoding.Add(_audioStreams[audioRel]);
            args.AddRange(GetVideoEncoderArgs(ext));
            if (audioStreamsForEncoding.Count > 0) args.AddRange(GetAudioEncoderArgs(audioStreamsForEncoding));
            WriteLog(audioStreamsForEncoding.Count > 0
                ? $"エンコーダー: {GetVideoEncoderLogText(ext)} / {GetAudioEncoderLogText()}"
                : $"エンコーダー: {GetVideoEncoderLogText(ext)}");
            args.AddRange(new[] { "-map_metadata", GetMapMetadataValue(), "-map_chapters", "-1" });
            if (ext is ".mp4" or ".mov" or ".m4v") args.AddRange(new[] { "-movflags", "+faststart" });
            args.Add(output);
            if (hasClipping)
            {
                description = $"クリッピングを区間単位で適用し、concat フィルターで再エンコード（{segmentCount} 区間）";
            }
            else if (segmentCount == 1)
            {
                description = "単一区間に crop フィルターを適用して再エンコード";
            }
            else
            {
                description = $"複数区間を concat フィルターで再エンコードして結合（{segmentCount} 区間）";
            }
        }

        string editorOperation;
        if (segmentCount > 1)
        {
            editorOperation = hasClipping ? "クリッピング + カット" : "結合";
        }
        else if (hasClipping)
        {
            var onlySegment = segments[0];
            editorOperation = onlySegment.StartMs > 0 || onlySegment.EndMs < _durationMs - 1 ? "クリッピング + カット" : "クリッピング";
        }
        else
        {
            editorOperation = "カット";
        }
        if (speedChanged) editorOperation += " + 再生速度変更";
        if (GetRotationIndex() > 0) editorOperation += " + 回転";
        var videoReencoded = !isLosslessPath;
        var audioReencoded = !isLosslessPath && !isSingleClipPath;
        var editedDurationMs = (long)Math.Round(selectedDurationMs / speedValue);
        if (!isLosslessPath) args = AddRotationInputArgument(args, true);
        var speedMetaText = speedChanged ? speedText : "";
        var editorLines = NewEditorMetadataLines(editorOperation, _durationMs, editedDurationMs,
            videoReencoded, audioReencoded, selectedCutAudios,
            speedMetaText, GetRotationIndex(), segments, hasClipping && segmentCount > 1);
        args = AddEditorMetadataArgument(args, output, editorLines);

        WriteLog($"実行方式: {description}");
        WriteLog("予定ステップ数: 1（単一 FFmpeg プロセス）");
        var step = new JobStep
        {
            Description = description,
            Arguments = args.ToArray(),
            VerifyPath = output,
            ExpectedDurationMs = editedDurationMs,
            VerificationKind = isLosslessPath ? "single-cut-output" : "reencoded-concat-output",
            SourceDurationMs = _durationMs,
            ProgramInfoSourcePath = segmentCount == 1 && !speedChanged && IsKeepDataChecked("programinfo")
                && string.Equals(Path.GetExtension(output), ".ts", StringComparison.OrdinalIgnoreCase)
                && IsTsSourceContainer()
                ? _inputPath
                : "",
            ProgramInfoStartMs = segments[0].StartMs,
            ProgramInfoEndMs = segments[0].EndMs,
            TempFileContents = stepTempFiles,
        };
        return step;
    }

    // ------------------------------------------------------------------
    // 区間の出力 (グループ内は結合、グループ間は分割)
    // ------------------------------------------------------------------

    private void StartGroupedCut(bool forceEncode)
    {
        try
        {
            if (_probe == null) throw new InvalidOperationException("先に入力動画を読み込んでください。");
            if (_segments.Count == 0) throw new InvalidOperationException("出力する区間が登録されていません。「動画編集」タブの「区間を追加」で、出力したい範囲を1つ以上登録してください。");
            var groups = BuildSegmentGroups();
            var needsLosslessSplit = false;
            if (!forceEncode)
            {
                foreach (var group in groups)
                {
                    if (group.Count == 1 && !group[0].HasClipping) { needsLosslessSplit = true; break; }
                }
            }
            if (needsLosslessSplit && !TestTrimSupport())
            {
                throw new InvalidOperationException("クリッピングなし区間の無劣化抽出には、指定コミット由来の trim と setts ビットストリームフィルターを含む FFmpeg が必要です。");
            }
            if (_videoStreams.Count == 0) throw new InvalidOperationException("入力動画に映像ストリームがありません。");

            long totalMs = 0;
            foreach (var segment in _segments)
            {
                var segLength = segment.EndMs - segment.StartMs;
                if (segLength <= 0 || segment.StartMs < 0) throw new InvalidOperationException("カット区間の開始・終了時刻が不正です。");
                if (_durationMs > 0 && segment.EndMs > _durationMs) throw new InvalidOperationException("カット区間が入力動画の長さを超えています。");
                totalMs += segLength;
            }

            var outputDir = GetOutputDirectory();
            var stamp = "";
            if (_appendTimestampCheckBox != null && _appendTimestampCheckBox.Checked)
            {
                stamp = "_" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            }

            var selectedAudios = new List<ProbeStream>();
            var selectedAudioRelativeIndices = new List<int>();
            for (var audioIndex = 0; audioIndex < _audioStreams.Count; audioIndex++)
            {
                if (audioIndex < _audioList.Items.Count && _audioList.GetItemChecked(audioIndex))
                {
                    selectedAudios.Add(_audioStreams[audioIndex]);
                    selectedAudioRelativeIndices.Add(audioIndex);
                }
            }

            var speedValue = GetSelectedSpeed();
            var speedChanged = Math.Abs(speedValue - 1.0) >= 0.000001;
            var speedMetaText = speedChanged ? speedValue.ToString("0.00", CultureInfo.InvariantCulture) : "";
            var rotationIndexForLog = GetRotationIndex();
            WriteLog($"再生速度={speedValue.ToString("0.00", CultureInfo.InvariantCulture)}x / 回転={(rotationIndexForLog > 0 ? GetRotationDisplayText(rotationIndexForLog) + "（回転メタデータのみ）" : "なし")}");

            var inputFull = Path.GetFullPath(_inputPath);
            // 同じ範囲のグループが複数あっても出力できるよう、名前が重なるグループには連番を付ける。
            var fileNames = GetGroupOutputFileNames(stamp, forceEncode);
            var outputs = new List<string>();
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var path = Path.Combine(outputDir, fileNames[gi]);
                if (path.Equals(inputFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("出力ファイルが入力動画と同じパスになります。出力先のフォルダーを変更してください。");
                outputs.Add(path);
            }
            var existing = outputs.Where(File.Exists).ToList();
            if (existing.Count > 0 && !_previewingCommands)
            {
                var shown = string.Join("\r\n", existing.Take(8).Select(Path.GetFileName));
                var rest = existing.Count > 8 ? $"\r\n…ほか {existing.Count - 8} ファイル" : "";
                var answer = MessageBox.Show($"次の {existing.Count} ファイルが既に存在します。上書きしますか？\r\n\r\n{shown}{rest}", AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    WriteLog("出力をユーザーがキャンセルしました（既存ファイルの上書きを拒否）。");
                    return;
                }
            }

            // MP4 系コンテナに Opus が入る出力 (Opus への再エンコード、または元の Opus 音声のコピー) では、
            // 格納が標準化されていない旨を先に確認する。判定はグループごとの実際の出力コンテナで行う。
            // 音声が再エンコードされるのは、複数区間の結合か、1 区間でも速度変更と音声保持が重なるときで、
            // それ以外の 1 区間グループは音声をコピーする (クリッピングのある 1 区間も音声はコピーする)。
            var opusFromEncode = false;
            var opusFromCopy = false;
            foreach (var g in groups)
            {
                var groupExt = GetGroupOutputExtension(g, forceEncode);
                if (groupExt is not (".mp4" or ".mov" or ".m4v")) continue;
                var audioReencoded = forceEncode || g.Count > 1 || (g.Count == 1 && !g[0].HasClipping && speedChanged && selectedAudios.Count > 0);
                if (audioReencoded)
                {
                    if (GetSelectedAudioFormat() == "Opus" && selectedAudios.Count > 0) opusFromEncode = true;
                }
                else if (selectedAudios.Any(a => string.Equals(a.CodecName, "opus", StringComparison.OrdinalIgnoreCase)))
                {
                    opusFromCopy = true;
                }
            }
            if ((opusFromEncode || opusFromCopy) && !ConfirmOpusInMp4(opusFromEncode))
            {
                WriteLog("Opus in MP4 の確認でユーザーがキャンセルしました。出力を中止します。");
                return;
            }

            if (groups.Any(g => GroupIsStreamCopy(g, forceEncode)) && GetSourceContainerExtension() != GetContainerExtension())
            {
                WriteLog($"ストリームコピーのグループは入力と同じコンテナ ({GetSourceContainerExtension()}) で出力します。");
            }

            var clippedCount = _segments.Count(s => s.HasClipping);
            WriteLog($"出力方式: {_segments.Count} 区間を {groups.Count} ファイルに分けて出力します (グループ内は結合、クリッピングのある区間は {clippedCount} 件)。");
            WriteLog($"登録区間数={_segments.Count}; 合計選択時間={TimeText.FormatClockFromMs(totalMs)}; 入力動画時間={TimeText.FormatClockFromMs(_durationMs)}");
            WriteLog($"入力ファイル: {_inputPath}");
            WriteLog($"出力先フォルダー: {outputDir}");
            WriteLog($"カット出力ストリーム: primary_video={_videoStreams[0].Index}; selected_audio={selectedAudios.Count}/{_audioStreams.Count}");

            var steps = new List<JobStep>();
            for (var gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                if (group.Count == 1 && (!group[0].HasClipping || forceEncode))
                {
                    steps.Add(BuildSplitSegmentStep(group[0], outputs[gi], _segments.IndexOf(group[0]), selectedAudios, selectedAudioRelativeIndices, speedValue, speedChanged, speedMetaText, forceEncode));
                }
                else
                {
                    steps.Add(BuildCutJoinStep(group, outputs[gi]));
                }
            }
            if (_segments[0].HasClipping)
            {
                WriteLog($"区間 1 のクリッピングフィルター: {GetSegmentCropFilter(_segments[0])}");
            }
            else if (!forceEncode && groups[0].Count == 1)
            {
                WriteLog($"映像 BSF の例 (区間 1): {NewLosslessCutArguments(_segments[0].StartMs, _segments[0].EndMs, selectedAudios, outputs[0], speedValue).BsfFilter}");
            }

            var summaryLines = new List<string> { outputDir };
            foreach (var path in outputs.Take(10)) summaryLines.Add("  " + Path.GetFileName(path));
            if (outputs.Count > 10) summaryLines.Add($"  …ほか {outputs.Count - 10} ファイル");
            StartPlan(steps, string.Join("\r\n", summaryLines), "", alreadyConfirmed: true);
        }
        catch (Exception ex)
        {
            if (_previewingCommands)
            {
                _capturedCommandError = ex.Message;
                return;
            }
            WriteLog($"出力を開始できませんでした: {ex.Message}");
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>クリッピングのない 1 区間だけのグループの抽出ステップを作る。</summary>
    private JobStep BuildSplitSegmentStep(Segment segment, string outputPath, int segmentIndex, List<ProbeStream> selectedAudios, List<int> selectedAudioRelativeIndices, double speedValue, bool speedChanged, string speedMetaText, bool forceEncode = false)
    {
        var startMs = segment.StartMs;
        var endMs = segment.EndMs;
        // 再生速度を変えて音声を保持する区間は、フィルター方式で再エンコードする。
        // 「エンコードして出力」では、1 区間は常にフィルター方式で再エンコードする。
        var segmentVideoReencoded = forceEncode || (speedChanged && selectedAudios.Count > 0);
        List<string> cutArguments;
        var tempFiles = new List<(string Path, string Content)>();
        var losslessContentStartMs = startMs;
        var losslessVideoLengthMs = endMs - startMs;
        if (segmentVideoReencoded)
        {
            var encoded = NewReencodedClippedSegmentArguments(startMs, endMs, segment, selectedAudioRelativeIndices.ToArray(), outputPath, speedValue);
            cutArguments = encoded.Arguments;
            tempFiles = encoded.TempFiles;
        }
        else
        {
            var cut = NewLosslessCutArguments(startMs, endMs, selectedAudios, outputPath, speedValue);
            cutArguments = cut.Arguments;
            tempFiles = cut.TempFiles;
            losslessContentStartMs = cut.ContentStartMs;
            losslessVideoLengthMs = cut.VideoLengthMs;
        }
        var splitOperation = "カット";
        if (speedChanged) splitOperation += " + 再生速度変更";
        if (GetRotationIndex() > 0) splitOperation += " + 回転";
        var splitEditorLines = NewEditorMetadataLines(splitOperation, _durationMs,
            (long)Math.Round((endMs - startMs) / speedValue),
            segmentVideoReencoded, segmentVideoReencoded, selectedAudios,
            speedMetaText, GetRotationIndex(), new List<Segment> { segment });
        cutArguments = AddEditorMetadataArgument(cutArguments, outputPath, splitEditorLines);
        WriteLog($"区間 {segmentIndex + 1}/{_segments.Count}: start={TimeText.FormatClockFromMs(startMs)}; end={TimeText.FormatClockFromMs(endMs)}; length={TimeText.FormatClockFromMs(endMs - startMs)}; 出力={outputPath}");
        return new JobStep
        {
            Description = segmentVideoReencoded
                ? $"区間 {segmentIndex + 1}/{_segments.Count} を再エンコード: {TimeText.FormatClockFromMs(startMs)} → {TimeText.FormatClockFromMs(endMs)}"
                : $"区間 {segmentIndex + 1}/{_segments.Count} を無劣化で抽出: {TimeText.FormatClockFromMs(startMs)} → {TimeText.FormatClockFromMs(endMs)}",
            Arguments = cutArguments.ToArray(),
            VerifyPath = outputPath,
            ExpectedDurationMs = !segmentVideoReencoded && !speedChanged
                ? Math.Max((long)Math.Round((endMs - startMs) / speedValue), losslessVideoLengthMs)
                : (long)Math.Round((endMs - startMs) / speedValue),
            VerificationKind = "single-cut-output",
            SourceDurationMs = _durationMs,
            ProgramInfoSourcePath = !speedChanged && IsKeepDataChecked("programinfo")
                && string.Equals(Path.GetExtension(outputPath), ".ts", StringComparison.OrdinalIgnoreCase)
                && IsTsSourceContainer()
                ? _inputPath
                : "",
            // 番組情報の移植は映像の内容に合わせて配置するため、開始位置は実際に書き出される
            // 先頭フレームの位置 (映像の先頭パケット起点) を渡す。
            ProgramInfoStartMs = !segmentVideoReencoded && !speedChanged ? losslessContentStartMs : startMs,
            ProgramInfoEndMs = endMs,
            TempFileContents = tempFiles,
        };
    }
}
