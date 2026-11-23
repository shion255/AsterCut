using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
/// プロファイル名 = ファイル名 (拡張子なし) で、名前の変更はエクスプローラー等でファイルをリネームして行う。
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

    /// <summary>プロファイル名の一覧。組み込みの 2 件を先頭にし、残りは名前順。</summary>
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
        foreach (var builtin in new[] { "投稿用", "保存用" })
        {
            if (names.Contains(builtin)) ordered.Add(builtin);
        }
        foreach (var name in names.Where(n => n != "保存用" && n != "投稿用").OrderBy(n => n, StringComparer.CurrentCulture))
        {
            ordered.Add(name);
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
            WriteLog($"プロファイルを追加しました: {name} (名前を変える場合は profiles フォルダー内のファイルをリネームしてください)");
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

    private void DeleteCurrentProfile()
    {
        var name = _profileCombo?.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("削除するプロファイルが選択されていません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (name is "保存用" or "投稿用")
        {
            MessageBox.Show("組み込みのプロファイルは削除できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show($"プロファイル「{name}」を削除しますか？", AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }
        try
        {
            File.Delete(GetProfileFilePath(name));
            RefreshProfileCombo("投稿用");
            ApplySelectedProfile();
            WriteLog($"プロファイルを削除しました: {name}");
            SetStatus($"プロファイル「{name}」を削除しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------
    // プロファイルの名前変更 (コンボボックスの右クリックメニュー)
    // ------------------------------------------------------------------

    private ContextMenuStrip? _profileRenameMenu;
    private string? _profileRenameTarget;
    private bool _profileRenameMenuFromList;
    private IntPtr _profileComboListHandle;
    private bool _profileListFilterRegistered;
    private System.Windows.Forms.Timer? _profileMenuTimer;

    [DllImport("user32.dll")]
    private static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref ComboBoxInfo info);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageProfileList(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int cbSize;
        public Rectangle rcItem;
        public Rectangle rcButton;
        public int stateButton;
        public IntPtr hwndCombo;
        public IntPtr hwndEdit;
        public IntPtr hwndList;
    }

    private sealed class ProfileListFilter : IMessageFilter
    {
        private const int WmRButtonDown = 0x0204;
        private readonly MainForm _form;

        public ProfileListFilter(MainForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WmRButtonDown || _form.IsDisposed) return false;
            return _form.TryProfileListRightClick(m.HWnd);
        }
    }

    private void InitializeProfileRenameMenu()
    {
        if (_profileCombo == null) return;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new AsterCutMenuColorTable()) };
        var item = new ToolStripMenuItem("名前を変更") { ForeColor = Theme.MenuFore };
        item.Click += (_, _) => RenameProfile(_profileRenameTarget);
        menu.Items.Add(item);
        menu.Opening += (_, e) =>
        {
            if (_profileRenameMenuFromList)
            {
                _profileRenameMenuFromList = false;
            }
            else
            {
                _profileRenameTarget = _profileCombo.SelectedItem?.ToString();
            }
            if (string.IsNullOrEmpty(_profileRenameTarget)) e.Cancel = true;
        };
        _profileRenameMenu = menu;
        _profileCombo.ContextMenuStrip = menu;
        _profileCombo.DropDown += (_, _) => CaptureProfileComboListHandle();
        _profileCombo.DropDownClosed += (_, _) => _profileComboListHandle = IntPtr.Zero;
        if (!_profileListFilterRegistered)
        {
            Application.AddMessageFilter(new ProfileListFilter(this));
            _profileListFilterRegistered = true;
        }
    }

    private void CaptureProfileComboListHandle()
    {
        _profileComboListHandle = IntPtr.Zero;
        if (_profileCombo == null || !_profileCombo.IsHandleCreated) return;
        var info = new ComboBoxInfo { cbSize = Marshal.SizeOf<ComboBoxInfo>() };
        if (GetComboBoxInfo(_profileCombo.Handle, ref info)) _profileComboListHandle = info.hwndList;
    }

    /// <summary>ドロップダウン一覧への右クリックなら、その項目を対象にメニューを開く。</summary>
    private bool TryProfileListRightClick(IntPtr hWnd)
    {
        if (_profileCombo == null || _profileRenameMenu == null) return false;
        if (_profileComboListHandle == IntPtr.Zero || hWnd != _profileComboListHandle) return false;
        // LB_ITEMFROMPOINT には一覧のクライアント座標が要るため、一覧の画面上の位置から割り出す。
        if (!GetWindowRect(hWnd, out var listRect)) return false;
        var clientX = Cursor.Position.X - listRect.Left;
        var clientY = Cursor.Position.Y - listRect.Top;
        var pointParam = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
        const uint LbItemFromPoint = 0x01A9;
        var result = SendMessageProfileList(hWnd, LbItemFromPoint, IntPtr.Zero, pointParam).ToInt32();
        var index = result & 0xFFFF;
        var outside = (result >> 16) != 0;
        if (outside || index >= _profileCombo.Items.Count) return false;
        _profileRenameTarget = _profileCombo.Items[index]?.ToString();
        if (string.IsNullOrEmpty(_profileRenameTarget)) return false;
        _profileRenameMenuFromList = true;
        var menu = _profileRenameMenu;
        var showPoint = Cursor.Position;
        // 一覧は開いたままにしてメニューだけを重ねる (一覧を先に閉じると環境によっては不安定になるため)。
        // 一覧は、メニューの項目が選ばれたときに RenameProfile の先頭で閉じる。
        // メニューの表示は、一覧へのマウスメッセージの処理が終わってからタイマーで出す。
        _profileMenuTimer?.Stop();
        _profileMenuTimer?.Dispose();
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        _profileMenuTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_profileMenuTimer, timer)) _profileMenuTimer = null;
            timer.Dispose();
            menu.Show(showPoint);
        };
        timer.Start();
        return true;
    }

    private void RenameProfile(string? targetName)
    {
        if (_profileCombo is { DroppedDown: true }) _profileCombo.DroppedDown = false;
        var name = targetName;
        if (string.IsNullOrEmpty(name)) return;
        if (name is "保存用" or "投稿用")
        {
            MessageBox.Show("組み込みのプロファイルは名前を変更できません。", AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            var wasSelected = _profileCombo?.SelectedItem?.ToString() == name;
            RefreshProfileCombo(wasSelected ? newName : _profileCombo?.SelectedItem?.ToString());
            if (wasSelected) ApplySelectedProfile();
            WriteLog($"プロファイルの名前を変更しました: {name} → {newName}");
            SetStatus($"プロファイルの名前を「{newName}」に変更しました");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _outputToolTip.SetToolTip(_audioBitrateCombo, "Opus の目標ビットレート。VBR は常に有効。モノラル音声で 510 kbps を選ぶと、libopus の上限に合わせて 256 kbps を適用します。");
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
