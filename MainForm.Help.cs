using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsterCut;

internal sealed partial class MainForm
{
    // ------------------------------------------------------------------
    // ヘルプ (リポジトリへのリンクとバージョン情報)
    // ------------------------------------------------------------------

    private const string RepositoryUrl = "https://github.com/shion255/AsterCut";

    /// <summary>表示用バージョン (「1.00」の形式)。正式公開までは 1.00 のままにし、上げることはしない。</summary>
    private static string AppVersionText
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version == null ? "1.00" : $"{version.Major}.{version.Minor:00}";
        }
    }

    private void OpenRepositoryPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WriteLog($"リポジトリを開けませんでした: {ex.Message}");
            SetStatus("リポジトリを開けませんでした");
        }
    }

    private void ShowAboutDialog()
    {
        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = "バージョン情報",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(430, 204),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };

        var iconBox = new PictureBox
        {
            Location = new Point(18, 18),
            Size = new Size(48, 48),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = AppIcon.Value.ToBitmap(),
        };
        dialog.Controls.Add(iconBox);

        var titleLabel = Theme.MakeLabel("AsterCut", 80, 16, 330, 30);
        titleLabel.Font = new Font("Meiryo UI", 13f, FontStyle.Bold);
        dialog.Controls.Add(titleLabel);

        dialog.Controls.Add(Theme.MakeLabel($"バージョン {AppVersionText}", 80, 50, 330, 24));

        dialog.Controls.Add(Theme.MakeLabel("リポジトリ:", 18, 84, 80, 24));
        var linkLabel = new LinkLabel
        {
            Text = RepositoryUrl,
            Location = new Point(100, 84),
            Size = new Size(312, 24),
            Font = Font,
            BackColor = Theme.PageBack,
            LinkColor = Color.FromArgb(172, 158, 235),
            ActiveLinkColor = Color.FromArgb(200, 188, 245),
            VisitedLinkColor = Color.FromArgb(172, 158, 235),
        };
        linkLabel.LinkClicked += (_, _) => OpenRepositoryPage();
        dialog.Controls.Add(linkLabel);

        // 最新バージョンの確認結果 (開いたときに自動で確認する)。
        var updateLabel = Theme.MakeLabel("最新バージョンを確認しています…", 18, 114, 290, 24);
        updateLabel.ForeColor = Theme.MutedFore;
        dialog.Controls.Add(updateLabel);

        var downloadButton = Theme.MakeButton("ダウンロード", 316, 110, 96, 30);
        downloadButton.Visible = false;
        dialog.Controls.Add(downloadButton);

        var copyrightLabel = Theme.MakeLabel("© 2026 shion255 (MIT License)", 18, 146, 300, 24);
        copyrightLabel.ForeColor = Theme.MutedFore;
        dialog.Controls.Add(copyrightLabel);

        var okButton = Theme.MakeButton("OK", 332, 160, 84, 30);
        okButton.DialogResult = DialogResult.OK;
        dialog.Controls.Add(okButton);
        dialog.AcceptButton = okButton;

        dialog.Shown += async (_, _) => await CheckLatestVersionAsync(dialog, updateLabel, downloadButton);

        Theme.ApplyDarkTitleBar(dialog);
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// GitHub の最新リリースと現在のバージョンを比べ、結果をバージョン情報に表示する。
    /// 新しいバージョンがある場合はダウンロードボタンを表示する (zip の展開と入れ替えは利用者が行う)。
    /// </summary>
    private async Task CheckLatestVersionAsync(Form dialog, Label updateLabel, Button downloadButton)
    {
        var tag = "";
        var downloadUrl = "";
        var releaseUrl = RepositoryUrl + "/releases";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AsterCut");
            var json = await client.GetStringAsync("https://api.github.com/repos/shion255/AsterCut/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagElement)) tag = tagElement.GetString() ?? "";
            if (root.TryGetProperty("html_url", out var htmlElement) && htmlElement.GetString() is { Length: > 0 } html) releaseUrl = html;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && asset.TryGetProperty("browser_download_url", out var urlElement)
                        && urlElement.GetString() is { Length: > 0 } url)
                    {
                        downloadUrl = url;
                        break;
                    }
                }
            }
        }
        catch
        {
            if (!dialog.IsDisposed) updateLabel.Text = "最新バージョンを確認できませんでした";
            return;
        }
        if (dialog.IsDisposed) return;
        var latestText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
        var current = typeof(MainForm).Assembly.GetName().Version;
        if (Version.TryParse(latestText, out var latest) && current != null && latest > new Version(current.Major, current.Minor))
        {
            updateLabel.Text = $"新しいバージョン {tag} があります";
            updateLabel.ForeColor = Theme.MenuFore;
            var url = downloadUrl.Length > 0 ? downloadUrl : releaseUrl;
            downloadButton.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    WriteLog($"ダウンロードを開けませんでした: {ex.Message}");
                    SetStatus("ダウンロードを開けませんでした");
                }
            };
            downloadButton.Visible = true;
        }
        else
        {
            updateLabel.Text = "お使いのバージョンは最新です";
        }
    }
}
