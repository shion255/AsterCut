using System;
using System.Drawing;
using System.Windows.Forms;

namespace AsterCut;

internal sealed partial class MainForm
{
    // ------------------------------------------------------------------
    // オプション画面 (左の項目一覧で「全般」と「パス」を切り替える)
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
            ClientSize = new Size(640, 500),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.PageBack,
            ForeColor = Theme.MenuFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };

        var generalPanel = new Panel
        {
            Location = new Point(140, 12),
            Size = new Size(488, 436),
            BackColor = Theme.PageBack,
        };
        var pathPanel = new Panel
        {
            Location = new Point(140, 12),
            Size = new Size(488, 436),
            BackColor = Theme.PageBack,
            Visible = false,
        };
        dialog.Controls.Add(generalPanel);
        dialog.Controls.Add(pathPanel);

        // 枠線はテーマが描くとマウスオーバーで青く変わるため、枠線なしにして自前の枠で囲む。
        var categoryListBorder = new Panel
        {
            Location = new Point(12, 12),
            Size = new Size(118, 438),
            BackColor = Color.FromArgb(88, 80, 110),
            Padding = new Padding(1),
        };
        var categoryList = new ListBox
        {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.PanelBack,
            ForeColor = Theme.LabelFore,
            Font = Font,
        };
        categoryList.Items.Add("全般");
        categoryList.Items.Add("パス");
        categoryList.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= categoryList.Items.Count) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(selected ? Theme.Accent : Theme.PanelBack))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics, categoryList.Items[e.Index]?.ToString() ?? "", categoryList.Font,
                new Rectangle(e.Bounds.Left + 10, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height),
                selected ? Color.White : Theme.LabelFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        categoryList.SelectedIndexChanged += (_, _) =>
        {
            var showGeneral = categoryList.SelectedIndex <= 0;
            generalPanel.Visible = showGeneral;
            pathPanel.Visible = !showGeneral;
        };
        categoryListBorder.Controls.Add(categoryList);
        dialog.Controls.Add(categoryListBorder);
        categoryList.SelectedIndex = 0;

        // ---------------- 「パス」 ----------------

        var pathRows = new System.Collections.Generic.List<(TextBox Box, Button Browse)>();
        var pathLabels = new System.Collections.Generic.List<Label>();

        TextBox MakePathRow(Control parent, string labelText, int y, string value, string fileName, int labelYOffset = 2)
        {
            // ラベルの文字はボックスの文字より上に寄るため、少し下げて中央に合わせる。
            // 幅は文字幅に合わせ、末尾の「:」などが欠けないようにする。
            var rowLabel = Theme.MakeLabel(labelText, 8, y + labelYOffset, 64, 26);
            parent.Controls.Add(rowLabel);
            rowLabel.Width = rowLabel.PreferredWidth;
            pathLabels.Add(rowLabel);
            var box = Theme.MakeTextBox(76, y, 318);
            box.Text = value;
            parent.Controls.Add(box);
            var browse = Theme.MakeButton("参照...", 402, y - 1, 84, 28);
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
            parent.Controls.Add(browse);
            pathRows.Add((box, browse));
            return box;
        }

        var ffmpegBox = MakePathRow(pathPanel, "ffmpeg:", 14, _ffmpegPath, "ffmpeg.exe");
        var ffprobeBox = MakePathRow(pathPanel, "ffprobe:", 54, _ffprobePath, "ffprobe.exe");
        // mpv のラベルだけは、実機では他の行より文字が下に寄って表示されるため、4px 上に置く。
        var mpvBox = MakePathRow(pathPanel, "mpv:", 94, _mpvPath, "mpv.exe", -2);

        // ラベルと入力欄の間隔の基準 (「全般」のコンボで使う)。
        // パスの欄は、ラベルの文字幅に合わせず、全行で位置と幅を統一する。
        var gap = GetLabelBoxGap();

        var trimLabel = Theme.MakeLabel(_trimStatusText, 8, 134, 472, 24);
        trimLabel.ForeColor = _trimStatusColor;
        pathPanel.Controls.Add(trimLabel);

        // 出力先フォルダーの固定 (有効な間は、読み込み時の出力先の初期値が固定フォルダーになる)。
        var fixedOutputCheck = new CheckBox
        {
            Text = "出力先フォルダーを固定する",
            Location = new Point(8, 166),
            Size = new Size(300, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = _settings.FixedOutputDirectoryEnabled,
        };
        pathPanel.Controls.Add(fixedOutputCheck);

        // フォルダーのラベルは 64px では文字が入りきらず折り返して欠けるため、文字幅に合わせる。
        // 縦位置は、実機では他の行より文字が下に寄って表示されるため、3px 上に置く。
        var folderLabel = Theme.MakeLabel("フォルダー:", 8, 193, 64, 26);
        pathPanel.Controls.Add(folderLabel);
        folderLabel.Width = folderLabel.PreferredWidth;
        var fixedOutputBox = Theme.MakeTextBox(76, 194, 318);
        fixedOutputBox.Text = _settings.FixedOutputDirectory;
        pathPanel.Controls.Add(fixedOutputBox);
        var fixedOutputBrowse = Theme.MakeButton("参照...", 402, 193, 84, 28);
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
        pathPanel.Controls.Add(fixedOutputBrowse);
        void UpdateFixedOutputEnabled()
        {
            fixedOutputBox.Enabled = fixedOutputCheck.Checked;
            fixedOutputBrowse.Enabled = fixedOutputCheck.Checked;
        }
        fixedOutputCheck.CheckedChanged += (_, _) => UpdateFixedOutputEnabled();
        UpdateFixedOutputEnabled();
        pathRows.Add((fixedOutputBox, fixedOutputBrowse));

        // ボックスは、全行で一番長いラベルの後ろから始める (ラベルがボックスに重なって
        // 左端を隠さないようにする)。右端は元の位置のまま、幅だけ短くなる。
        var longestLabel = folderLabel.PreferredWidth;
        foreach (var pathLabel in pathLabels) longestLabel = Math.Max(longestLabel, pathLabel.PreferredWidth);
        var pathBoxLeft = 8 + longestLabel + 6;
        foreach (var row in pathRows)
        {
            row.Box.Left = pathBoxLeft;
            row.Box.Width = Math.Max(10, 394 - pathBoxLeft);
        }

        // ---------------- 「全般」 ----------------

        var rememberSizeCheck = new CheckBox
        {
            Text = "ウィンドウサイズを記憶する",
            Location = new Point(8, 14),
            Size = new Size(300, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = _settings.RememberWindowSize,
        };
        generalPanel.Controls.Add(rememberSizeCheck);

        var opusWarningCheck = new CheckBox
        {
            Text = "Opus in MP4 の確認ポップアップを表示する",
            Location = new Point(8, 70),
            Size = new Size(464, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = !_settings.SuppressOpusInMp4Warning,
        };
        generalPanel.Controls.Add(opusWarningCheck);

        // 仕様・使い方の日本語の表示フォント (英数字は Consolas で固定)。
        generalPanel.Controls.Add(Theme.MakeLabel("仕様・使い方のフォント:", 8, 98, 240, 24));

        var japaneseFontCombo = new ThemedComboBox
        {
            Location = new Point(76, 126),
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
        generalPanel.Controls.Add(japaneseFontCombo);
        generalPanel.Controls.Add(Theme.MakeLabel("日本語:", 8, 126, 64, 26));
        AlignLabelAndBox(FindLabelByText(generalPanel, "日本語:"), japaneseFontCombo, gap, true);

        var autoClosePopupCheck = new CheckBox
        {
            Text = "動画出力終了時にポップアップを自動的に閉じる",
            Location = new Point(8, 42),
            Size = new Size(464, 26),
            ForeColor = Theme.LabelFore,
            BackColor = Color.Transparent,
            Checked = _settings.AutoCloseOutputLogPopup,
        };
        generalPanel.Controls.Add(autoClosePopupCheck);

        var okButton = Theme.MakeButton("OK", 432, 460, 96, 32);
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
            _settings.AutoCloseOutputLogPopup = autoClosePopupCheck.Checked;
            SaveSettingsFromUi();
            ApplySpecTextFonts();
            _ = CheckTrimSupportAsync();
        };
        dialog.Controls.Add(okButton);
        var cancelButton = Theme.MakeButton("キャンセル", 536, 460, 96, 32);
        cancelButton.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        // パスの欄は、ダイアログ表示の時点では非表示のパネル内にあり、まだハンドルが
        // 作られていないため、ハンドルが作られた時点で先頭の余白を付ける。
        foreach (var pathBox in new[] { ffmpegBox, ffprobeBox, mpvBox, fixedOutputBox })
        {
            pathBox.HandleCreated += (_, _) => ApplyTextBoxMarginsOnce(pathBox, 2, 0);
            if (pathBox.IsHandleCreated) ApplyTextBoxMarginsOnce(pathBox, 2, 0);
        }

        Theme.ApplyDarkTitleBar(dialog);
        dialog.ShowDialog(this);
    }
}
