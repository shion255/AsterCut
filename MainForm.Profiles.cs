using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AsterCut;

/// <summary>再エンコード設定のプロファイル (1 件分)。profiles フォルダーに 1 件 1 ファイルで保存する。</summary>
internal sealed class OutputProfile
{
    public string VideoFormat { get; set; } = "HEVC";
    public double Crf { get; set; } = 23.0;
    public int BitDepth { get; set; } = 12;
    public string AudioFormat { get; set; } = "Opus";
    public int AudioBitrateKbps { get; set; } = 192;
    public string Container { get; set; } = "MKV";
}

/// <summary>
/// 出力プロファイルの読み書きと、出力タブのフォーマット系コンボの連動。
/// プロファイル名 = ファイル名 (拡張子なし) で、名前の変更と削除はプロファイル欄の右クリックから行う。
/// </summary>
internal sealed partial class MainForm
{
    private bool _profileControlsUpdating;

    private static readonly string[] VideoFormatNames = { "AVC", "HEVC", "AV1" };
    private static readonly string[] AudioFormatNames = { "AAC", "Opus", "FLAC" };

    private static string ProfilesDirectory =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static OutputProfile StorageProfile() => new()
    {
        VideoFormat = "HEVC",
        Crf = 23.0,
        BitDepth = 12,
        AudioFormat = "Opus",
        AudioBitrateKbps = 192,
        Container = "MKV",
    };

    private static OutputProfile UploadProfile() => new()
    {
        VideoFormat = "AVC",
        Crf = 18.0,
        BitDepth = 8,
        AudioFormat = "AAC",
        AudioBitrateKbps = 320,
        Container = "MP4",
    };

    // ------------------------------------------------------------------
    // ファイルの読み書き
    // ------------------------------------------------------------------

    private static string GetProfileFilePath(string name) => Path.Combine(ProfilesDirectory, name + ".json");

    /// <summary>profiles フォルダーを作り、組み込みプロファイル (保存用・投稿用) が無ければ作成する。既存のファイルは上書きしない。</summary>
    private void EnsureBuiltinProfiles()
    {
        try
        {
            Directory.CreateDirectory(ProfilesDirectory);
            if (!File.Exists(GetProfileFilePath("保存用"))) WriteProfileFile("保存用", StorageProfile());
            if (!File.Exists(GetProfileFilePath("投稿用"))) WriteProfileFile("投稿用", UploadProfile());
        }
        catch (Exception ex)
        {
            WriteLog($"プロファイルフォルダーの準備に失敗しました: {ex.Message}");
        }
    }

    private void WriteProfileFile(string name, OutputProfile profile)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("プロファイル名に使えない文字が含まれています: " + name);
        Directory.CreateDirectory(ProfilesDirectory);
        var json = JsonSerializer.Serialize(profile, ProfileJsonOptions);
        File.WriteAllText(GetProfileFilePath(name), json + "\r\n", new UTF8Encoding(false));
    }

    private static OutputProfile ReadProfileFile(string name)
    {
        var profile = StorageProfile();
        try
        {
            var path = GetProfileFilePath(name);
            if (!File.Exists(path)) return profile;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return profile;
            string GetText(string key) =>
                root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            var videoFormat = GetText("VideoFormat");
            if (VideoFormatNames.Contains(videoFormat)) profile.VideoFormat = videoFormat;
            if (root.TryGetProperty("Crf", out var crf) && crf.TryGetDouble(out var crfValue) && crfValue >= 0 && crfValue <= 63)
            {
                profile.Crf = crfValue;
            }
            if (root.TryGetProperty("BitDepth", out var depth) && depth.TryGetInt32(out var depthValue) && depthValue is 8 or 10 or 12)
            {
                profile.BitDepth = depthValue;
            }
            var audioFormat = GetText("AudioFormat");
            if (AudioFormatNames.Contains(audioFormat)) profile.AudioFormat = audioFormat;
            if (root.TryGetProperty("AudioBitrateKbps", out var bitrate) && bitrate.TryGetInt32(out var bitrateValue) && bitrateValue >= 32 && bitrateValue <= 1024)
            {
                profile.AudioBitrateKbps = bitrateValue;
            }
            var container = GetText("Container");
            if (container is "MKV" or "MP4" or "WebM" or "TS") profile.Container = container;
        }
        catch { }
        return profile;
    }

    /// <summary>プロファイル名の一覧。設定に保存された並び順を優先し、残りは組み込みの 2 件を先頭に名前順。</summary>
    private List<string> ListProfileNames()
    {
        var names = new List<string>();
        try
        {
            if (Directory.Exists(ProfilesDirectory))
            {
                foreach (var file in Directory.GetFiles(ProfilesDirectory, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length > 0) names.Add(name);
                }
            }
        }
        catch { }
        var ordered = new List<string>();
        foreach (var saved in _settings.ProfileOrder)
        {
            if (names.Contains(saved) && !ordered.Contains(saved)) ordered.Add(saved);
        }
        foreach (var builtin in new[] { "投稿用", "保存用" })
        {
            if (names.Contains(builtin) && !ordered.Contains(builtin)) ordered.Add(builtin);
        }
        foreach (var name in names.Where(n => n != "保存用" && n != "投稿用").OrderBy(n => n, StringComparer.CurrentCulture))
        {
            if (!ordered.Contains(name)) ordered.Add(name);
        }
        return ordered;
    }

    // ------------------------------------------------------------------
    // コンボの初期化と選択
    // ------------------------------------------------------------------

    private void InitializeOutputProfiles()
    {
        EnsureBuiltinProfiles();
        RefreshProfileCombo(_settings.OutputProfileName);
        ApplySelectedProfile();
    }

    private void RefreshProfileCombo(string? preferredName)
    {
        if (_profileCombo == null) return;
        EnsureBuiltinProfiles();
        var names = ListProfileNames();
        _profileControlsUpdating = true;
        try
        {
            _profileCombo.Items.Clear();
            foreach (var name in names) _profileCombo.Items.Add(name);
            var index = preferredName != null ? names.IndexOf(preferredName) : -1;
            if (index < 0) index = names.IndexOf("投稿用");
            _profileCombo.SelectedIndex = index >= 0 ? index : (names.Count > 0 ? 0 : -1);
        }
        finally { _profileControlsUpdating = false; }
    }

    private void ApplySelectedProfile()
    {
        var name = _profileCombo?.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name)) return;
        ApplyProfileToUi(ReadProfileFile(name));
    }

    private void OnProfileSelectionChanged()
    {
        if (_profileControlsUpdating) return;
        var name = _profileCombo?.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name)) return;
        _settings.OutputProfileName = name;
        SaveSettingsFromUi();
        ApplyProfileToUi(ReadProfileFile(name));
        WriteLog($"プロファイルを切り替えました: {name}");
    }

    private void AddNewProfile()
    {
        try
        {
            EnsureBuiltinProfiles();
            var name = "新規プロファイル";
            var number = 2;
            while (File.Exists(GetProfileFilePath(name)))
            {
                name = $"新規プロファイル {number}";
                number++;
            }
            WriteProfileFile(name, CaptureProfileFromUi());
            RefreshProfileCombo(name);
            _settings.OutputProfileName = name;
            SaveSettingsFromUi();
            UpdateProfileDirtyMark();
            WriteLog($"プロファイルを追加しました: {name} (名前は右クリックで変更できます)");
            SetStatus($"プロファイル「{name}」を追加しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveCurrentProfile()
    {
        var name = _profileCombo?.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("保存先のプロファイルが選択されていません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            WriteProfileFile(name, CaptureProfileFromUi());
            UpdateProfileDirtyMark();
            WriteLog($"プロファイル「{name}」に現在の設定を保存しました。");
            SetStatus($"プロファイル「{name}」に保存しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DeleteProfile(string? targetName)
    {
        // ダイアログを出す前にポップアップを閉じる。開いたままだとダイアログがポップアップを
        // 所有者として開かれ、ポップアップが非アクティブ化で閉じるときに一緒に閉じてしまうため。
        CloseProfilePopup();
        var name = targetName;
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "削除するプロファイルが選択されていません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (name is "保存用" or "投稿用")
        {
            MessageBox.Show(this, "組み込みのプロファイルは削除できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"プロファイル「{name}」を削除しますか？", AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        try
        {
            File.Delete(GetProfileFilePath(name));
            if (_settings.ProfileOrder.Remove(name)) SaveSettingsFromUi();
            var wasSelected = _profileCombo?.SelectedItem?.ToString() == name;
            RefreshProfileCombo(wasSelected ? "投稿用" : _profileCombo?.SelectedItem?.ToString());
            if (wasSelected) ApplySelectedProfile();
            WriteLog($"プロファイルを削除しました: {name}");
            SetStatus($"プロファイル「{name}」を削除しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------
    // プロファイルの右クリックメニューと自前ポップアップ一覧
    // ------------------------------------------------------------------

    private ContextMenuStrip? _profileRenameMenu;
    private ToolStripMenuItem? _profileMoveUpItem;
    private ToolStripMenuItem? _profileMoveDownItem;
    private string? _profileRenameTarget;
    private Form? _profilePopup;
    private ListBox? _profilePopupList;
    private DateTime _profilePopupToggleSuppressedUntil;
    private bool _profileFaceFilterRegistered;

    /// <summary>
    /// プロファイル欄への左クリックを横取りし、標準のドロップダウンの代わりに自前ポップアップを開閉する。
    /// 標準の一覧は開いている間マウス入力を握り続けるため、その上では右クリックメニューが使えない。
    /// </summary>
    private sealed class ProfileFaceFilter : IMessageFilter
    {
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonDblClk = 0x0203;
        private readonly MainForm _form;

        public ProfileFaceFilter(MainForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            if ((m.Msg != WmLButtonDown && m.Msg != WmLButtonDblClk) || _form.IsDisposed) return false;
            if (_form._profileCombo == null || !_form._profileCombo.IsHandleCreated) return false;
            if (m.HWnd != _form._profileCombo.Handle) return false;
            return _form.ToggleProfilePopup();
        }
    }

    private void InitializeProfileMenu()
    {
        if (_profileCombo == null) return;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()) };
        var moveUpItem = new ToolStripMenuItem("上へ移動") { ForeColor = Theme.MenuFore };
        moveUpItem.Click += (_, _) => MoveProfile(_profileRenameTarget, -1);
        menu.Items.Add(moveUpItem);
        _profileMoveUpItem = moveUpItem;
        var moveDownItem = new ToolStripMenuItem("下へ移動") { ForeColor = Theme.MenuFore };
        moveDownItem.Click += (_, _) => MoveProfile(_profileRenameTarget, 1);
        menu.Items.Add(moveDownItem);
        _profileMoveDownItem = moveDownItem;
        menu.Items.Add(new ToolStripSeparator());
        var renameItem = new ToolStripMenuItem("名前を変更") { ForeColor = Theme.MenuFore };
        renameItem.Click += (_, _) => RenameProfile(_profileRenameTarget);
        menu.Items.Add(renameItem);
        var deleteItem = new ToolStripMenuItem("削除") { ForeColor = Theme.MenuFore };
        deleteItem.Click += (_, _) => DeleteProfile(_profileRenameTarget);
        menu.Items.Add(deleteItem);
        menu.Opening += (_, e) =>
        {
            // 開いたメニューにツールチップが被らないよう、表示中のツールチップを消す。
            if (_profileCombo != null) _outputToolTip.Hide(_profileCombo);
            // 一覧から開いたときは右クリックした項目が対象で、コンボ本体から開いたときは選択中の項目が対象。
            if (!ReferenceEquals(menu.SourceControl, _profilePopupList))
            {
                _profileRenameTarget = _profileCombo.SelectedItem?.ToString();
            }
            if (string.IsNullOrEmpty(_profileRenameTarget))
            {
                e.Cancel = true;
                return;
            }
            var names = ListProfileNames();
            var index = names.IndexOf(_profileRenameTarget);
            if (_profileMoveUpItem != null) _profileMoveUpItem.Enabled = index > 0;
            if (_profileMoveDownItem != null) _profileMoveDownItem.Enabled = index >= 0 && index < names.Count - 1;
        };
        _profileRenameMenu = menu;
        _profileCombo.ContextMenuStrip = menu;
        _profileCombo.KeyDown += ProfileComboKeyDown;
        if (!_profileFaceFilterRegistered)
        {
            Application.AddMessageFilter(new ProfileFaceFilter(this));
            _profileFaceFilterRegistered = true;
        }
    }

    private void ProfileComboKeyDown(object? sender, KeyEventArgs e)
    {
        // ドロップダウンを開くキーは、標準の一覧ではなく自前ポップアップを開閉する。
        if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ToggleProfilePopup();
        }
    }

    /// <summary>自前ポップアップ一覧を開閉する。標準のドロップダウンは開かないため、常に true を返す。</summary>
    private bool ToggleProfilePopup()
    {
        if (_profileCombo == null) return false;
        if (_profilePopup is { IsDisposed: false })
        {
            CloseProfilePopup();
            return true;
        }
        // 一覧を閉じるためのクリック (Deactivate 経由で閉じた直後) では、開き直さない。
        if (DateTime.UtcNow < _profilePopupToggleSuppressedUntil) return true;
        _profileCombo.Focus();
        ShowProfilePopup();
        return true;
    }

    private void CloseProfilePopup()
    {
        var popup = _profilePopup;
        _profilePopup = null;
        _profilePopupList = null;
        if (popup is { IsDisposed: false })
        {
            try { popup.Close(); } catch { /* 閉じ済みなら何もしない */ }
        }
    }

    private void ShowProfilePopup()
    {
        if (_profileCombo == null) return;
        // 開いた一覧にツールチップが被らないよう、表示中のツールチップを消す。
        _outputToolTip.Hide(_profileCombo);
        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
        };
        _profilePopup = popup;

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.BoxBack,
            ForeColor = Theme.BoxFore,
            Font = _profileCombo.Font,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            HorizontalScrollbar = false,
            ItemHeight = 26,
            ContextMenuStrip = _profileRenameMenu,
        };
        _profilePopupList = list;
        popup.Controls.Add(list);

        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            var isSelected = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(isSelected ? Color.FromArgb(86, 74, 110) : Theme.BoxBack))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            var bounds = new Rectangle(e.Bounds.Left + 2, e.Bounds.Top - 1, e.Bounds.Width - 2, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString() ?? "", list.Font, bounds, Theme.BoxFore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        };
        list.MouseMove += (_, e) =>
        {
            var index = list.IndexFromPoint(e.Location);
            if (index >= 0 && index != list.SelectedIndex)
            {
                var previousIndex = list.SelectedIndex;
                list.SelectedIndex = index;
                // 選択が移った行は、念のため明示的に描き直す (履歴ポップアップと同じやり方)。
                try { if (previousIndex >= 0) list.Invalidate(list.GetItemRectangle(previousIndex)); } catch { }
                try { list.Invalidate(list.GetItemRectangle(index)); } catch { }
            }
        };
        list.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var index = list.IndexFromPoint(e.Location);
            // 項目の無い場所ではメニューを出さない。
            list.ContextMenuStrip = index >= 0 ? _profileRenameMenu : null;
            if (index < 0 || index >= list.Items.Count) return;
            list.SelectedIndex = index;
            _profileRenameTarget = list.Items[index]?.ToString();
        };
        list.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var index = list.IndexFromPoint(e.Location);
            if (index < 0 || index >= list.Items.Count) return;
            SelectProfileFromPopup(list.Items[index]?.ToString());
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseProfilePopup();
                _profileCombo?.Focus();
            }
            else if (e.KeyCode == Keys.Enter && list.SelectedIndex >= 0)
            {
                e.Handled = true;
                SelectProfileFromPopup(list.Items[list.SelectedIndex]?.ToString());
            }
        };
        popup.Deactivate += (_, _) =>
        {
            // コンボ本体を押して閉じた場合は、直後に来る本体のクリックで開き直さないよう印を付ける。
            try
            {
                if (_profileCombo is { IsDisposed: false })
                {
                    var cursorInCombo = _profileCombo.PointToClient(Control.MousePosition);
                    if (_profileCombo.ClientRectangle.Contains(cursorInCombo))
                    {
                        _profilePopupToggleSuppressedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    }
                }
            }
            catch { /* 判定できなくても閉じる */ }
            // 右クリックメニューを開いている間は閉じない (メニューで移動した後も一覧を残すため)。
            if (_profileRenameMenu is { Visible: true }) return;
            CloseProfilePopup();
        };
        popup.FormClosed += (_, _) =>
        {
            if (_profilePopup == popup)
            {
                _profilePopup = null;
                _profilePopupList = null;
            }
        };

        RefreshProfilePopupItems(_profileCombo.SelectedItem?.ToString());
        popup.Width = Math.Max(160, _profileCombo.Width);
        var belowLeft = _profileCombo.PointToScreen(new Point(0, _profileCombo.Height + 2));
        var aboveLeft = _profileCombo.PointToScreen(new Point(0, -popup.Height - 2));
        var workingArea = Screen.FromControl(_profileCombo).WorkingArea;
        var popupX = belowLeft.X;
        if (popupX + popup.Width > workingArea.Right) popupX = workingArea.Right - popup.Width;
        if (popupX < workingArea.Left) popupX = workingArea.Left;
        var popupY = belowLeft.Y;
        if (popupY + popup.Height > workingArea.Bottom) popupY = aboveLeft.Y;
        if (popupY < workingArea.Top) popupY = workingArea.Top;
        popup.Location = new Point(popupX, popupY);
        popup.Show(this);
        list.Focus();
    }

    /// <summary>ポップアップ一覧の項目を並び順どおりに作り直し、指定した名前を選択する。</summary>
    private void RefreshProfilePopupItems(string? selectName)
    {
        var list = _profilePopupList;
        if (list == null || list.IsDisposed) return;
        var names = ListProfileNames();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var name in names) list.Items.Add(name);
            var index = selectName != null ? names.IndexOf(selectName) : -1;
            list.SelectedIndex = index >= 0 ? index : (names.Count > 0 ? 0 : -1);
        }
        finally { list.EndUpdate(); }
        if (_profilePopup is { IsDisposed: false })
        {
            _profilePopup.Height = Math.Clamp(list.Items.Count, 1, 8) * list.ItemHeight + 2;
        }
    }

    private void SelectProfileFromPopup(string? name)
    {
        CloseProfilePopup();
        if (!string.IsNullOrEmpty(name) && _profileCombo != null && _profileCombo.SelectedItem?.ToString() != name)
        {
            _profileCombo.SelectedItem = name;
        }
        _profileCombo?.Focus();
    }

    /// <summary>プロファイルの並び順を 1 つ上 (delta = -1) または下 (delta = 1) へ動かし、設定に保存する。組み込みも対象。</summary>
    private void MoveProfile(string? targetName, int delta)
    {
        if (string.IsNullOrEmpty(targetName)) return;
        var names = ListProfileNames();
        var index = names.IndexOf(targetName);
        var newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= names.Count) return;
        (names[index], names[newIndex]) = (names[newIndex], names[index]);
        _settings.ProfileOrder = names;
        SaveSettingsFromUi();
        RefreshProfileCombo(_profileCombo?.SelectedItem?.ToString());
        // ポップアップ一覧が開いているときは、一覧を開いたままその場で入れ替えて見せる。
        if (_profilePopup is { IsDisposed: false })
        {
            RefreshProfilePopupItems(targetName);
            _profilePopup.Activate();
            _profilePopupList?.Focus();
        }
    }

    private void RenameProfile(string? targetName)
    {
        CloseProfilePopup();
        var name = targetName;
        if (string.IsNullOrEmpty(name)) return;
        if (name is "保存用" or "投稿用")
        {
            MessageBox.Show(this, "組み込みのプロファイルは名前を変更できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Icon = AppIcon.Value,
            Text = "プロファイルの名前を変更",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 100),
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

        dialog.Controls.Add(Theme.MakeLabel("新しい名前", 16, 12, 388, 24));
        var nameBox = Theme.MakeTextBox(16, 42, 220, 28);
        nameBox.Text = name;
        nameBox.SelectAll();
        dialog.Controls.Add(nameBox);

        string? newName = null;
        var renameButton = Theme.MakeButton("変更", 244, 41, 76, 30);
        renameButton.Click += (_, _) =>
        {
            var candidate = nameBox.Text.Trim();
            if (candidate.Length == 0
                || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || (candidate != name && File.Exists(GetProfileFilePath(candidate))))
            {
                MessageBox.Show("その名前は使えません。空の名前、ファイル名に使えない文字、同名のプロファイルは指定できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                nameBox.SelectAll();
                nameBox.Focus();
                return;
            }
            newName = candidate;
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Controls.Add(renameButton);
        var cancelButton = Theme.MakeButton("キャンセル", 328, 41, 96, 30);
        cancelButton.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        dialog.Controls.Add(cancelButton);
        dialog.AcceptButton = renameButton;
        dialog.CancelButton = cancelButton;
        dialog.Shown += (_, _) => nameBox.Focus();

        if (dialog.ShowDialog(this) != DialogResult.OK || newName == null || newName == name) return;
        try
        {
            File.Move(GetProfileFilePath(name), GetProfileFilePath(newName));
            var orderIndex = _settings.ProfileOrder.IndexOf(name);
            if (orderIndex >= 0)
            {
                _settings.ProfileOrder[orderIndex] = newName;
                SaveSettingsFromUi();
            }
            var wasSelected = _profileCombo?.SelectedItem?.ToString() == name;
            RefreshProfileCombo(wasSelected ? newName : _profileCombo?.SelectedItem?.ToString());
            if (wasSelected) ApplySelectedProfile();
            WriteLog($"プロファイルの名前を変更しました: {name} → {newName}");
            SetStatus($"プロファイルの名前を「{newName}」に変更しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 選択中のプロファイルの保存内容と現在の設定が食い違うとき、プロファイル欄の右上に * を出す。
    /// プログラムからの適用中 (_profileControlsUpdating) は判定しない (適用後の呼び出しで確定する)。
    /// </summary>
    private void UpdateProfileDirtyMark()
    {
        if (_profileDirtyLabel == null) return;
        if (_profileControlsUpdating) return;
        var name = _profileCombo?.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            _profileDirtyLabel.Visible = false;
            return;
        }
        // 印は、プロファイル名の文字のすぐ右上に置く (文字の幅はコンボのフォントで測る)。
        var nameWidth = TextRenderer.MeasureText(name, _profileCombo!.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        var starX = _profileCombo.Left + 7 + nameWidth + 1;
        var maxX = _profileCombo.Left + _profileCombo.Width - 24 - _profileDirtyLabel.Width;
        _profileDirtyLabel.Location = new Point(Math.Min(starX, maxX), _profileCombo.Top + 2);
        var saved = ReadProfileFile(name);
        var current = CaptureProfileFromUi();
        _profileDirtyLabel.Visible =
            saved.VideoFormat != current.VideoFormat ||
            saved.Crf != current.Crf ||
            saved.BitDepth != current.BitDepth ||
            saved.AudioFormat != current.AudioFormat ||
            saved.AudioBitrateKbps != current.AudioBitrateKbps ||
            saved.Container != current.Container;
    }

    // ------------------------------------------------------------------
    // UI とプロファイルの相互変換
    // ------------------------------------------------------------------

    private OutputProfile CaptureProfileFromUi()
    {
        var crf = 23.0;
        var crfText = (_videoCrfBox?.Text ?? "").Trim();
        if (!double.TryParse(crfText, NumberStyles.Float, CultureInfo.InvariantCulture, out crf)) crf = 23.0;
        return new OutputProfile
        {
            VideoFormat = GetSelectedVideoFormat(),
            Crf = crf,
            BitDepth = GetSelectedBitDepth(),
            AudioFormat = GetSelectedAudioFormat(),
            AudioBitrateKbps = GetAudioBitrateKbpsOrDefault(),
            Container = GetSelectedContainer(),
        };
    }

    private void ApplyProfileToUi(OutputProfile profile)
    {
        _profileControlsUpdating = true;
        try
        {
            SetComboText(_videoFormatCombo, profile.VideoFormat, "HEVC");
            RebuildBitDepthItems();
            SetBitDepth(profile.BitDepth);
            SetCrfValue(profile.Crf);
            SetComboText(_audioFormatCombo, profile.AudioFormat, "Opus");
            RebuildAudioBitrateItems();
            SetAudioBitrateKbps(profile.AudioBitrateKbps);
            UpdateAudioBitrateToolTip();
            RebuildContainerItems();
            SetComboText(_containerCombo, profile.Container, "MKV");
        }
        finally { _profileControlsUpdating = false; }
        UpdateOutputNameView();
        UpdateProfileDirtyMark();
    }

    private static void SetComboText(System.Windows.Forms.ComboBox? combo, string value, string fallback)
    {
        if (combo == null) return;
        var index = combo.Items.IndexOf(value);
        combo.SelectedIndex = index >= 0 ? index : combo.Items.IndexOf(fallback);
    }

    // ------------------------------------------------------------------
    // フォーマット系コンボの連動 (選択肢の組み立て)
    // ------------------------------------------------------------------

    private static int[] GetAllowedBitDepths(string videoFormat) =>
        videoFormat == "HEVC" ? new[] { 8, 10, 12 } : new[] { 8, 10 };

    private void RebuildBitDepthItems()
    {
        if (_bitDepthCombo == null) return;
        var allowed = GetAllowedBitDepths(GetSelectedVideoFormat());
        var current = GetSelectedBitDepth();
        if (!allowed.Contains(current)) current = allowed[^1];
        _bitDepthCombo.Items.Clear();
        foreach (var depth in allowed) _bitDepthCombo.Items.Add($"{depth}-bit");
        _bitDepthCombo.SelectedItem = $"{current}-bit";
    }

    private void SetBitDepth(int depth)
    {
        if (_bitDepthCombo == null) return;
        var allowed = GetAllowedBitDepths(GetSelectedVideoFormat());
        if (!allowed.Contains(depth)) depth = allowed[^1];
        _bitDepthCombo.SelectedItem = $"{depth}-bit";
    }

    private static int[] GetAudioBitrateValues(string audioFormat) =>
        audioFormat == "AAC" ? new[] { 128, 192, 256, 320 }
        : audioFormat == "FLAC" ? Array.Empty<int>()
        : new[] { 128, 160, 192, 256, 510 };

    private void RebuildAudioBitrateItems()
    {
        if (_audioBitrateCombo == null) return;
        var values = GetAudioBitrateValues(GetSelectedAudioFormat());
        // FLAC は無劣化でビットレートの指定がないため、欄を無効にして空にする。
        _audioBitrateCombo.Enabled = values.Length > 0;
        if (values.Length == 0)
        {
            _audioBitrateCombo.Items.Clear();
            return;
        }
        var current = GetAudioBitrateKbpsOrDefault();
        var best = values[0];
        foreach (var value in values)
        {
            if (Math.Abs(value - current) < Math.Abs(best - current) ||
                (Math.Abs(value - current) == Math.Abs(best - current) && value > best))
            {
                best = value;
            }
        }
        _audioBitrateCombo.Items.Clear();
        foreach (var value in values) _audioBitrateCombo.Items.Add($"{value} kbps");
        _audioBitrateCombo.SelectedItem = $"{best} kbps";
    }

    private void SetAudioBitrateKbps(int kbps)
    {
        if (_audioBitrateCombo == null) return;
        var text = $"{kbps} kbps";
        if (_audioBitrateCombo.Items.Contains(text)) _audioBitrateCombo.SelectedItem = text;
    }

    // 画質 (CRF) は、再生速度の欄と同じくホイールと ↑↓ でも変更できる。値はフォーマットの
    // 刻みと範囲にそろえて確定する (AVC / HEVC は 0.1 刻みで 0～51、AV1 は 1 刻みで 0～63)。
    private double _crfValue = 23.0;

    private void SetCrfValue(double value)
    {
        var isAv1 = GetSelectedVideoFormat() == "AV1";
        var snapped = isAv1
            ? Math.Round(value, MidpointRounding.AwayFromZero)
            : Math.Round(value * 10.0, MidpointRounding.AwayFromZero) / 10.0;
        _crfValue = Math.Clamp(snapped, 0.0, isAv1 ? 63.0 : 51.0);
        var text = _crfValue.ToString("0.0", CultureInfo.InvariantCulture);
        if (_videoCrfBox != null && _videoCrfBox.Text != text) _videoCrfBox.Text = text;
        UpdateProfileDirtyMark();
    }

    /// <summary>画質の欄への直接入力を確定する。解釈できないときは現在の値に戻す。</summary>
    private void CommitCrfBoxText()
    {
        if (_videoCrfBox == null) return;
        var text = _videoCrfBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            SetCrfValue(value);
        }
        else
        {
            _videoCrfBox.Text = _crfValue.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    private double GetCrfStep() => GetSelectedVideoFormat() == "AV1" ? 1.0 : 0.1;

    private void CrfBoxMouseWheel(MouseEventArgs e)
    {
        // ホイール 1 ノッチで 1 刻み。上で上げ、下で下げる。
        var notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        if (notches == 0) return;
        SetCrfValue(_crfValue + GetCrfStep() * notches);
    }

    private void CrfBoxKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            CommitCrfBoxText();
        }
        else if (e.KeyCode == Keys.Up)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SetCrfValue(_crfValue + GetCrfStep());
        }
        else if (e.KeyCode == Keys.Down)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            SetCrfValue(_crfValue - GetCrfStep());
        }
    }

    private void RebuildContainerItems()
    {
        if (_containerCombo == null) return;
        var items = new List<string> { "MKV" };
        if (GetSelectedAudioFormat() != "FLAC") items.Add("MP4");
        // TS に入れられるのは、規格で収載が定められた AVC / HEVC + AAC / Opus の組み合わせだけ。
        if ((GetSelectedVideoFormat() is "AVC" or "HEVC") && GetSelectedAudioFormat() != "FLAC") items.Add("TS");
        if (GetSelectedVideoFormat() == "AV1" && GetSelectedAudioFormat() == "Opus") items.Add("WebM");
        var current = GetSelectedContainer();
        if (!items.Contains(current)) current = "MKV";
        _containerCombo.Items.Clear();
        foreach (var item in items) _containerCombo.Items.Add(item);
        _containerCombo.SelectedItem = current;
    }

    private void UpdateAudioBitrateToolTip()
    {
        if (GetSelectedAudioFormat() == "FLAC")
        {
            _outputToolTip.SetToolTip(_audioBitrateCombo, "FLAC は無劣化のため、ビットレートの指定はありません。");
        }
        else if (GetSelectedAudioFormat() == "AAC")
        {
            _outputToolTip.SetToolTip(_audioBitrateCombo, "AAC (LC) のビットレート。");
        }
        else
        {
            _outputToolTip.SetToolTip(_audioBitrateCombo, "Opus の目標ビットレート。\r\nVBR は常に有効。\r\nモノラル音声で 510 kbps を選ぶと、libopus の上限に合わせて 256 kbps を適用します。");
        }
    }

    private void OnVideoFormatChanged()
    {
        if (_profileControlsUpdating) return;
        RebuildBitDepthItems();
        SetCrfValue(_crfValue);
        RebuildContainerItems();
        UpdateOutputNameView();
        UpdateProfileDirtyMark();
    }

    private void OnAudioFormatChanged()
    {
        if (_profileControlsUpdating) return;
        RebuildAudioBitrateItems();
        UpdateAudioBitrateToolTip();
        RebuildContainerItems();
        UpdateOutputNameView();
        UpdateProfileDirtyMark();
    }

    private void OnContainerChanged()
    {
        if (_profileControlsUpdating) return;
        UpdateOutputNameView();
        UpdateProfileDirtyMark();
    }

    // ------------------------------------------------------------------
    // 現在の選択値
    // ------------------------------------------------------------------

    private string GetSelectedVideoFormat() => _videoFormatCombo?.SelectedItem?.ToString() ?? "HEVC";

    private string GetSelectedAudioFormat() => _audioFormatCombo?.SelectedItem?.ToString() ?? "Opus";

    private string GetSelectedContainer() => _containerCombo?.SelectedItem?.ToString() ?? "MKV";

    private string GetContainerExtension() => GetSelectedContainer() switch
    {
        "MP4" => ".mp4",
        "WebM" => ".webm",
        "TS" => ".ts",
        _ => ".mkv",
    };

    private int GetSelectedBitDepth()
    {
        var text = _bitDepthCombo?.SelectedItem?.ToString() ?? "";
        var match = Regex.Match(text, @"^(\d+)-bit$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 12;
    }

    private int GetAudioBitrateKbpsOrDefault()
    {
        var text = _audioBitrateCombo?.SelectedItem?.ToString() ?? "";
        var match = Regex.Match(text, @"^\s*(\d+)\s*kbps\s*$");
        if (match.Success) return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return GetSelectedAudioFormat() == "AAC" ? 320 : 192;
    }
}
