using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>
/// ffmpeg / ffprobe / ffplay の自動更新
/// (FFmpeg-Builds の最新リリースを取得し、検証して入れ替える)。
/// </summary>
internal sealed partial class MainForm
{
    private sealed class FfmpegBuildInfo
    {
        public DateTimeOffset PublishedAt;
        public string ZipUrl = "";
        public long ZipSize;
        public string Sha256 = "";
    }

    private sealed class FfmpegUpdateTarget
    {
        public string Name = "";
        public string Dest = "";
    }

    private sealed class FfmpegUpdateSync
    {
        public readonly ConcurrentQueue<string> LogQueue = new();
        public volatile string Phase = "idle";
        public volatile int Progress;
        private long _downloadedBytes;
        private long _totalBytes;
        public long DownloadedBytes
        {
            get => System.Threading.Volatile.Read(ref _downloadedBytes);
            set => System.Threading.Volatile.Write(ref _downloadedBytes, value);
        }
        public long TotalBytes
        {
            get => System.Threading.Volatile.Read(ref _totalBytes);
            set => System.Threading.Volatile.Write(ref _totalBytes, value);
        }
        public volatile bool Cancel;
        public volatile bool Done;
        public volatile bool Success;
        public volatile string Error = "";
    }

    private FfmpegBuildInfo? _ffmpegUpdateInfo;
    private FfmpegUpdateSync? _ffmpegUpdateSync;

    private static string GetToolVersionLine(string path)
    {
        // 指定した exe を -version で実行し、標準出力の 1 行目を返す。失敗した場合は空文字。
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return "";
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(); } catch { }
                return "";
            }
            _ = stderrTask;
            var versionLine = (stdoutTask.Result.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0]).Trim();
            // 「Copyright (c) ...」以降は表示に不要なので取り除く。
            return Regex.Replace(versionLine, @"\s+Copyright.*$", "");
        }
        catch { return ""; }
    }

    private static async Task<FfmpegBuildInfo> GetLatestFfmpegBuildInfoAsync()
    {
        // FFmpeg-Builds (GitHub) の最新リリースを取得し、win64-gpl (静的リンク版) の ZIP 情報と SHA-256 を返す。
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AsterCut");
        var releaseJson = await client.GetStringAsync("https://api.github.com/repos/FFmpeg-Builds/FFmpeg-Builds/releases/latest").ConfigureAwait(false);
        // published_at は自動変換に任せず、生の JSON 文字列から読む (時差でずれるのを避ける)。
        var publishedMatch = Regex.Match(releaseJson, "\"published_at\"\\s*:\\s*\"([^\"]+)\"");
        if (!publishedMatch.Success) throw new InvalidOperationException("最新リリースの公開日時を取得できませんでした。");
        var publishedAt = DateTimeOffset.Parse(publishedMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        string zipName = "";
        string zipUrl = "";
        long zipSize = 0;
        string checksumUrl = "";
        using (var doc = JsonDocument.Parse(releaseJson))
        {
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                // master の win64-gpl (静的リンク版) を探す。現在の名前は ffmpeg-N-(番号)-g(ハッシュ)-win64-gpl.zip の形で、
                // 以前の名前 ffmpeg-master-latest-win64-gpl.zip も引き続き対象にする (バージョン付きの名前を優先する)。
                if (Regex.IsMatch(name, @"^ffmpeg-N-\d+-g[0-9a-fA-F]+-win64-gpl\.zip$") || (name == "ffmpeg-master-latest-win64-gpl.zip" && zipName.Length == 0))
                {
                    zipName = name;
                    zipUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    zipSize = asset.GetProperty("size").GetInt64();
                }
                else if (name == "checksums.sha256")
                {
                    checksumUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                }
            }
        }
        if (zipUrl.Length == 0) throw new InvalidOperationException("最新リリースに win64-gpl (静的リンク版) の ffmpeg が見つかりませんでした。");
        var sha256 = "";
        if (checksumUrl.Length > 0)
        {
            try
            {
                var checksumText = await client.GetStringAsync(checksumUrl).ConfigureAwait(false);
                var checksumMatch = Regex.Match(checksumText, @"(?m)^([0-9a-fA-F]{64})\s+\*?" + Regex.Escape(zipName));
                if (checksumMatch.Success) sha256 = checksumMatch.Groups[1].Value.ToLowerInvariant();
            }
            catch { }
        }
        return new FfmpegBuildInfo { PublishedAt = publishedAt, ZipUrl = zipUrl, ZipSize = zipSize, Sha256 = sha256 };
    }

    // ffmpeg / ffprobe 更新の実処理 (UI スレッドとは別のタスクで実行し、UI には触れない)。
    // sync に進捗・フェーズ・ログを書き、ダイアログ側のタイマーが読み取る。
    private static async Task RunFfmpegUpdateAsync(FfmpegUpdateSync sync, string zipUrl, long zipSize, string sha256, string stagingDir, List<FfmpegUpdateTarget> targets)
    {
        void Log(string message) => sync.LogQueue.Enqueue(message);
        try
        {
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);
            var zipPath = Path.Combine(stagingDir, "ffmpeg-update.zip");

            // ダウンロード (ストリームを読みながら進捗を報告する)
            sync.Phase = "download";
            Log("ダウンロードを開始します。");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AsterCut");
                using var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var totalBytes = zipSize;
                if (response.Content.Headers.ContentLength is long contentLength) totalBytes = contentLength;
                await using var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var destStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[1048576];
                long downloaded = 0;
                while (true)
                {
                    if (sync.Cancel) throw new InvalidOperationException("キャンセルされました。");
                    var readCount = await sourceStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (readCount <= 0) break;
                    await destStream.WriteAsync(buffer, 0, readCount).ConfigureAwait(false);
                    downloaded += readCount;
                    sync.DownloadedBytes = downloaded;
                    sync.TotalBytes = totalBytes;
                    if (totalBytes > 0) sync.Progress = (int)Math.Min(100, downloaded * 100 / totalBytes);
                }
            }
            if (sync.Cancel) throw new InvalidOperationException("キャンセルされました。");
            Log("ダウンロードが完了しました。");

            // SHA-256 検証 (checksums.sha256 が取得できた場合のみ)
            if (!string.IsNullOrWhiteSpace(sha256))
            {
                sync.Phase = "verify";
                Log("SHA-256 ハッシュを検証しています。");
                string actualHash;
                await using (var hashStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using var sha = SHA256.Create();
                    actualHash = Convert.ToHexString(await sha.ComputeHashAsync(hashStream).ConfigureAwait(false)).ToLowerInvariant();
                }
                if (actualHash != sha256) throw new InvalidOperationException($"ハッシュが一致しませんでした (期待: {sha256} / 実際: {actualHash})。");
                Log("ハッシュ検証 OK。");
            }

            // 解凍
            sync.Phase = "extract";
            Log("ZIP を解凍しています。");
            var extractDir = Path.Combine(stagingDir, "extract");
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // 解凍した exe の場所と起動を確認する (起動できないビルドは入れ替えない)
            sync.Phase = "verify";
            var stagedPaths = new Dictionary<string, string>();
            foreach (var target in targets)
            {
                var found = Directory.GetFiles(extractDir, target.Name, SearchOption.AllDirectories);
                if (found.Length == 0) throw new InvalidOperationException($"解凍したファイルに {target.Name} が見つかりませんでした。");
                stagedPaths[target.Name] = found[0];
            }
            var newVersionLine = GetToolVersionLine(stagedPaths["ffmpeg.exe"]);
            if (string.IsNullOrWhiteSpace(newVersionLine)) throw new InvalidOperationException("新しい ffmpeg.exe の起動確認に失敗しました。");
            Log("新しいバージョン: " + newVersionLine);

            // 入れ替え (バックアップは作らず、そのまま上書きする)
            sync.Phase = "replace";
            foreach (var target in targets)
            {
                File.Copy(stagedPaths[target.Name], target.Dest, true);
                Log($"{target.Name} を入れ替えました。");
            }
            Log("更新が完了しました。");
            sync.Success = true;
        }
        catch (Exception ex)
        {
            sync.Error = ex.Message;
            Log("エラー: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            }
            catch { }
            sync.Done = true;
        }
    }

    private void ShowFfmpegUpdateDialog()
    {
        // FFmpeg-Builds (GitHub) の最新ビルドで、設定済みの ffmpeg / ffprobe (と、あれば ffplay) を更新するダイアログ。
        if (_jobRunning)
        {
            MessageBox.Show("出力処理中は更新できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 更新対象の解決 (ffprobe は未設定なら ffmpeg と同じフォルダーのものを対象にする)
        static string ResolveToolPath(string text)
        {
            var toolPath = Environment.ExpandEnvironmentVariables((text ?? "").Trim().Trim('"'));
            if (toolPath.Length > 0 && File.Exists(toolPath)) return Path.GetFullPath(toolPath);
            return "";
        }
        var ffmpegTarget = ResolveToolPath(_ffmpegPath);
        if (ffmpegTarget.Length == 0)
        {
            MessageBox.Show("ffmpeg のパスが設定されていないか、ファイルが見つからないため更新できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var ffprobeTarget = ResolveToolPath(_ffprobePath);
        if (ffprobeTarget.Length == 0)
        {
            var siblingProbe = Path.Combine(Path.GetDirectoryName(ffmpegTarget) ?? "", "ffprobe.exe");
            if (File.Exists(siblingProbe)) ffprobeTarget = siblingProbe;
        }
        var targets = new List<FfmpegUpdateTarget> { new() { Name = "ffmpeg.exe", Dest = ffmpegTarget } };
        if (ffprobeTarget.Length > 0) targets.Add(new FfmpegUpdateTarget { Name = "ffprobe.exe", Dest = ffprobeTarget });
        // ffplay はアプリでは使わないが、配布物のセットを揃えるため、ffmpeg と同じフォルダーにあれば更新対象に含める。
        var ffplayTarget = Path.Combine(Path.GetDirectoryName(ffmpegTarget) ?? "", "ffplay.exe");
        if (File.Exists(ffplayTarget)) targets.Add(new FfmpegUpdateTarget { Name = "ffplay.exe", Dest = ffplayTarget });

        var dialogForm = new Form
        {
            Icon = AppIcon.Value,
            Text = "ffmpeg / ffprobe / ffplay の更新",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(640, 484),
            MinimumSize = new Size(560, 444),
            SizeGripStyle = SizeGripStyle.Hide,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
        };
        const string sourcePrefix = "更新元: ";
        const string sourceUrl = "https://github.com/FFmpeg-Builds/FFmpeg-Builds";
        var sourceLabel = new LinkLabel
        {
            Location = new Point(12, 10),
            Size = new Size(616, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.LabelFore,
            LinkColor = Color.FromArgb(172, 158, 235),
            ActiveLinkColor = Color.FromArgb(200, 188, 245),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Text = sourcePrefix + sourceUrl,
            LinkArea = new LinkArea(sourcePrefix.Length, sourceUrl.Length),
        };
        sourceLabel.LinkClicked += (_, _) =>
        {
            try
            {
                var urlPsi = new ProcessStartInfo { FileName = sourceUrl, UseShellExecute = true };
                Process.Start(urlPsi);
            }
            catch { }
        };
        dialogForm.Controls.Add(sourceLabel);
        var statusLabel = Theme.MakeLabel("最新版を確認しています...", 12, 34, 616, 26);
        statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        dialogForm.Controls.Add(statusLabel);
        var currentVersionLabel = Theme.MakeLabel("", 12, 62, 616, 24);
        currentVersionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var currentVersion = GetToolVersionLine(ffmpegTarget);
        currentVersionLabel.Text = currentVersion.Length == 0 ? "現在の ffmpeg: (バージョンを取得できませんでした)" : "現在の ffmpeg: " + currentVersion;
        dialogForm.Controls.Add(currentVersionLabel);
        var latestVersionLabel = Theme.MakeLabel("", 12, 86, 616, 24);
        latestVersionLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        dialogForm.Controls.Add(latestVersionLabel);
        var progressBar = new ThemedProgressBar
        {
            Location = new Point(12, 118),
            Size = new Size(616, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };
        dialogForm.Controls.Add(progressBar);
        var phaseLabel = Theme.MakeLabel("", 12, 142, 616, 22);
        phaseLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        dialogForm.Controls.Add(phaseLabel);
        var logBox = new TextBox
        {
            Location = new Point(12, 168),
            Size = new Size(616, 256),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
        };
        foreach (var target in targets) logBox.AppendText($"更新対象: {target.Dest}\r\n");
        if (ffprobeTarget.Length == 0) logBox.AppendText("ffprobe.exe が見つからないため、ffmpeg.exe のみ更新します。\r\n");
        dialogForm.Controls.Add(logBox);
        var updateButton = Theme.MakeButton("更新する", 262, 436, 120, 32);
        updateButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        updateButton.Enabled = false;
        dialogForm.Controls.Add(updateButton);
        var cancelButton = Theme.MakeButton("キャンセル", 390, 436, 110, 32);
        cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        cancelButton.Enabled = false;
        dialogForm.Controls.Add(cancelButton);
        var closeButton = Theme.MakeButton("閉じる", 508, 436, 120, 32);
        closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        closeButton.DialogResult = DialogResult.Cancel;
        dialogForm.Controls.Add(closeButton);
        dialogForm.CancelButton = closeButton;
        dialogForm.ActiveControl = closeButton;

        var updateTimer = new System.Windows.Forms.Timer { Interval = 300 };

        dialogForm.Shown += async (_, _) =>
        {
            dialogForm.Cursor = Cursors.WaitCursor;
            try
            {
                _ffmpegUpdateInfo = await GetLatestFfmpegBuildInfoAsync();
                var latestJst = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(_ffmpegUpdateInfo.PublishedAt, "Tokyo Standard Time");
                var latestText = latestJst.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " JST";
                latestVersionLabel.Text = $"最新リリース: {latestText} 公開 ({Math.Round(_ffmpegUpdateInfo.ZipSize / 1048576.0, 1).ToString(CultureInfo.InvariantCulture)} MB)";
                var hasStored = DateTimeOffset.TryParse(_settings.FfmpegBuildPublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var storedPublished);
                if (hasStored && _ffmpegUpdateInfo.PublishedAt <= storedPublished)
                {
                    statusLabel.Text = "この機能で更新したビルドと同じか、それより古いリリースです (再更新は可能です)。";
                }
                else if (hasStored)
                {
                    statusLabel.Text = "新しいビルドに更新できます。";
                }
                else
                {
                    statusLabel.Text = "この機能での更新記録がないため、新旧の判定はできません。";
                }
                updateButton.Enabled = true;
            }
            catch (Exception ex)
            {
                statusLabel.Text = "最新版の確認に失敗しました。";
                logBox.AppendText($"エラー: {ex.Message}\r\n");
            }
            finally
            {
                dialogForm.Cursor = Cursors.Default;
            }
        };

        updateButton.Click += (_, _) =>
        {
            if (_ffmpegUpdateInfo == null) return;
            var info = _ffmpegUpdateInfo;
            var stagingDir = Path.Combine(Path.GetTempPath(), "AsterCut-ffmpeg-update");
            _ffmpegUpdateSync = new FfmpegUpdateSync();
            var sync = _ffmpegUpdateSync;
            _ = RunFfmpegUpdateAsync(sync, info.ZipUrl, info.ZipSize, info.Sha256, stagingDir, targets);
            updateButton.Enabled = false;
            cancelButton.Enabled = true;
            closeButton.Enabled = false;
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = 0;
            updateTimer.Start();
        };

        cancelButton.Click += (_, _) =>
        {
            if (_ffmpegUpdateSync != null)
            {
                _ffmpegUpdateSync.Cancel = true;
                phaseLabel.Text = "キャンセルしています...";
                cancelButton.Enabled = false;
            }
        };

        updateTimer.Tick += (_, _) =>
        {
            var sync = _ffmpegUpdateSync;
            if (sync == null) return;
            while (sync.LogQueue.TryDequeue(out var logLineText)) logBox.AppendText(logLineText + "\r\n");
            switch (sync.Phase)
            {
                case "download":
                    progressBar.Style = ProgressBarStyle.Blocks;
                    progressBar.Value = sync.Progress;
                    phaseLabel.Text = $"ダウンロード中... {sync.Progress}% ({Math.Round(sync.DownloadedBytes / 1048576.0, 1).ToString(CultureInfo.InvariantCulture)} / {Math.Round(sync.TotalBytes / 1048576.0, 1).ToString(CultureInfo.InvariantCulture)} MB)";
                    break;
                case "verify":
                    progressBar.Style = ProgressBarStyle.Marquee;
                    phaseLabel.Text = "検証中...";
                    break;
                case "extract":
                    progressBar.Style = ProgressBarStyle.Marquee;
                    phaseLabel.Text = "解凍中...";
                    break;
                case "replace":
                    progressBar.Style = ProgressBarStyle.Marquee;
                    phaseLabel.Text = "入れ替え中...";
                    break;
            }
            if (sync.Done)
            {
                updateTimer.Stop();
                cancelButton.Enabled = false;
                closeButton.Enabled = true;
                if (sync.Success)
                {
                    progressBar.Style = ProgressBarStyle.Blocks;
                    progressBar.Value = 100;
                    phaseLabel.Text = "";
                    statusLabel.Text = "更新が完了しました。";
                    if (_ffmpegUpdateInfo != null)
                    {
                        _settings.FfmpegBuildPublishedAt = _ffmpegUpdateInfo.PublishedAt.ToString("o", CultureInfo.InvariantCulture);
                        SaveSettingsFromUi();
                    }
                    var newCurrentVersion = GetToolVersionLine(ffmpegTarget);
                    if (newCurrentVersion.Length > 0) currentVersionLabel.Text = "現在の ffmpeg: " + newCurrentVersion;
                    WriteLog("ffmpeg / ffprobe / ffplay を最新ビルドに更新しました。");
                }
                else
                {
                    phaseLabel.Text = "";
                    statusLabel.Text = "更新できませんでした。詳細はログを確認してください。";
                    updateButton.Enabled = _ffmpegUpdateInfo != null;
                }
                _ffmpegUpdateSync = null;
            }
        };

        dialogForm.FormClosing += (_, e) =>
        {
            if (_ffmpegUpdateSync != null && !_ffmpegUpdateSync.Done) e.Cancel = true;
        };

        try
        {
            Theme.ApplyDarkTitleBar(dialogForm);
            dialogForm.ShowDialog(this);
        }
        finally
        {
            updateTimer.Stop();
            updateTimer.Dispose();
            _ffmpegUpdateSync = null;
            _ffmpegUpdateInfo = null;
            dialogForm.Dispose();
        }
    }
}
