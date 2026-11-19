using System;
using System.Drawing;
using System.Windows.Forms;

namespace AsterCut;

internal sealed partial class MainForm
{
    // ------------------------------------------------------------------
    // オプション画面 (パス設定とウィンドウサイズの記憶)
    // ------------------------------------------------------------------

    private void ShowOptionsDialog()
    {
        if (_jobRunning)
        {
            MessageBox.Show("出力処理中は変更できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = "オプション",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(600, 414),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };

        TextBox MakePathRow(string labelText, int y, string value, string fileName)
        {
            dialog.Controls.Add(Theme.MakeLabel(labelText, 12, y, 72, 26));
            var box = Theme.MakeTextBox(88, y, 398);
            box.Text = value;
            dialog.Controls.Add(box);
            var browse = Theme.MakeButton("参照...", 494, y - 1, 94, 28);
            browse.Click += (_, _) =>
            {
                using var picker = new OpenFileDialog
                {
                    Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
                    FileName = fileName,
                    Title = $"{fileName} を選択",
                };
                if (picker.ShowDialog(dialog) == DialogResult.OK) box.Text = picker.FileName;
            };
            dialog.Controls.Add(browse);
            return box;
        }

        var ffmpegBox = MakePathRow("ffmpeg:", 18, _ffmpegPath, "ffmpeg.exe");
        var ffprobeBox = MakePathRow("ffprobe:", 58, _ffprobePath, "ffprobe.exe");
        var mpvBox = MakePathRow("mpv:", 98, _mpvPath, "mpv.exe");

        // ラベルと入力欄の間隔は、本体の基準 (画質 (crf): の行) にそろえる。
        var gap = GetLabelBoxGap();
        AlignLabelAndBox(FindLabelByText(dialog, "ffmpeg:"), ffmpegBox, gap, true);
        AlignLabelAndBox(FindLabelByText(dialog, "ffprobe:"), ffprobeBox, gap, true);
        AlignLabelAndBox(FindLabelByText(dialog, "mpv:"), mpvBox, gap, true);

        var trimLabel = Theme.MakeLabel(_trimStatusText, 12, 138, 576, 24);
        trimLabel.ForeColor = _trimStatusColor;
        dialog.Controls.Add(trimLabel);

        var rememberSizeCheck = new CheckBox
        {
            Text = "ウィンドウサイズを記憶する",
            Location = new Point(12, 170),
            Size = new Size(300, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = _settings.RememberWindowSize,
        };
        dialog.Controls.Add(rememberSizeCheck);

        var opusWarningCheck = new CheckBox
        {
            Text = "Opus in MP4 の確認ポップアップを表示する",
            Location = new Point(12, 198),
            Size = new Size(470, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = !_settings.SuppressOpusInMp4Warning,
        };
        dialog.Controls.Add(opusWarningCheck);

        // 出力先フォルダーの固定 (有効な間は、読み込み時の出力先の初期値が固定フォルダーになる)。
        var fixedOutputCheck = new CheckBox
        {
            Text = "出力先フォルダーを固定する",
            Location = new Point(12, 226),
            Size = new Size(300, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = _settings.FixedOutputDirectoryEnabled,
        };
        dialog.Controls.Add(fixedOutputCheck);

        dialog.Controls.Add(Theme.MakeLabel("フォルダー:", 12, 254, 72, 26));
        var fixedOutputBox = Theme.MakeTextBox(88, 254, 398);
        fixedOutputBox.Text = _settings.FixedOutputDirectory;
        dialog.Controls.Add(fixedOutputBox);
        var fixedOutputBrowse = Theme.MakeButton("参照...", 494, 253, 94, 28);
        fixedOutputBrowse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "固定する出力先フォルダーを選択",
                ShowNewFolderButton = true,
            };
            var current = fixedOutputBox.Text.Trim().Trim('"');
            if (current.Length > 0 && System.IO.Directory.Exists(current)) picker.SelectedPath = current;
            if (picker.ShowDialog(dialog) == DialogResult.OK) fixedOutputBox.Text = picker.SelectedPath;
        };
        dialog.Controls.Add(fixedOutputBrowse);
        AlignLabelAndBox(FindLabelByText(dialog, "フォルダー:"), fixedOutputBox, gap, true);
        void UpdateFixedOutputEnabled()
        {
            fixedOutputBox.Enabled = fixedOutputCheck.Checked;
            fixedOutputBrowse.Enabled = fixedOutputCheck.Checked;
        }
        fixedOutputCheck.CheckedChanged += (_, _) => UpdateFixedOutputEnabled();
        UpdateFixedOutputEnabled();

        // 仕様・使い方の日本語の表示フォント (英数字は Consolas で固定)。
        dialog.Controls.Add(Theme.MakeLabel("仕様・使い方のフォント:", 12, 306, 240, 24));

        var japaneseFontCombo = new ThemedComboBox
        {
            Location = new Point(88, 334),
            Size = new Size(240, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 24,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.BoxBack,
            ForeColor = Color.White,
            DropDownWidth = 320,
        };
        japaneseFontCombo.DrawItem += DrawFormatComboItem;
        using (var collection = new System.Drawing.Text.InstalledFontCollection())
        {
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.CurrentCulture);
            foreach (var family in collection.Families) names.Add(family.Name);
            foreach (var name in names) japaneseFontCombo.Items.Add(name);
        }
        // 設定のフォント名は、インストール済みフォントの正式な名前へ解決してから選ぶ (存在しない名前を一覧へ足さない)。
        var currentFontName = GetSpecFontName(_settings.SpecJapaneseFontName, "Yu Gothic");
        if (japaneseFontCombo.Items.Contains(currentFontName)) japaneseFontCombo.SelectedItem = currentFontName;
        dialog.Controls.Add(japaneseFontCombo);
        dialog.Controls.Add(Theme.MakeLabel("日本語:", 12, 334, 72, 26));
        AlignLabelAndBox(FindLabelByText(dialog, "日本語:"), japaneseFontCombo, gap, true);

        var okButton = Theme.MakeButton("OK", 392, 368, 96, 32);
        okButton.DialogResult = DialogResult.OK;
        okButton.Click += (_, _) =>
        {
            _ffmpegPath = ffmpegBox.Text;
            _ffprobePath = ffprobeBox.Text;
            _mpvPath = mpvBox.Text;
            _settings.RememberWindowSize = rememberSizeCheck.Checked;
            _settings.SuppressOpusInMp4Warning = !opusWarningCheck.Checked;
            _settings.FixedOutputDirectoryEnabled = fixedOutputCheck.Checked;
            _settings.FixedOutputDirectory = fixedOutputBox.Text.Trim().Trim('"');
            UpdateOutputNameView();
            _settings.SpecJapaneseFontName = japaneseFontCombo.SelectedItem?.ToString() ?? "BIZ UDPゴシック";
            SaveSettingsFromUi();
            ApplySpecTextFonts();
            _ = CheckTrimSupportAsync();
        };
        dialog.Controls.Add(okButton);
        var cancelButton = Theme.MakeButton("キャンセル", 496, 368, 96, 32);
        cancelButton.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        dialog.Shown += (_, _) =>
        {
            ApplyTextBoxMarginsOnce(ffmpegBox, 2, 0);
            ApplyTextBoxMarginsOnce(ffprobeBox, 2, 0);
            ApplyTextBoxMarginsOnce(mpvBox, 2, 0);
            ApplyTextBoxMarginsOnce(fixedOutputBox, 2, 0);
        };

        Theme.ApplyDarkTitleBar(dialog);
        dialog.ShowDialog(this);
    }
}
