using System;
using System.Diagnostics;
using System.Drawing;
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
            ClientSize = new Size(430, 170),
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

        var copyrightLabel = Theme.MakeLabel("© 2026 shion255 (MIT License)", 18, 112, 300, 24);
        copyrightLabel.ForeColor = Theme.MutedFore;
        dialog.Controls.Add(copyrightLabel);

        var okButton = Theme.MakeButton("OK", 332, 126, 84, 30);
        okButton.DialogResult = DialogResult.OK;
        dialog.Controls.Add(okButton);
        dialog.AcceptButton = okButton;

        Theme.ApplyDarkTitleBar(dialog);
        dialog.ShowDialog(this);
    }
}
