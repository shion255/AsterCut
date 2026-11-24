using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AsterCut;

/// <summary>
/// AsterCut.settings.json を読み書きする。
/// 設定ファイルは exe と同じフォルダーに置く。
/// </summary>
internal sealed class AppSettings
{
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public int Volume { get; set; } = 80;
    public int VolumeSliderValue { get; set; } = 80;
    public bool PreviewMuted { get; set; }
    public string MpvPath { get; set; } = "";
    public string PreviewBackend { get; set; } = "mpv";
    public int WindowWidth { get; set; } = 1120;
    public int WindowHeight { get; set; } = 1000;
    public bool RememberWindowSize { get; set; } = true;
    public bool SuppressOpusInMp4Warning { get; set; }
    public bool AutoCloseOutputLogPopup { get; set; }
    public bool DeleteInvalidHistoryOnStartup { get; set; }
    public bool ShowKeyframesOnSeekBar { get; set; }
    public bool FixedOutputDirectoryEnabled { get; set; }
    public string FixedOutputDirectory { get; set; } = "";
    public string OutputProfileName { get; set; } = "";
    public string SpecJapaneseFontName { get; set; } = "BIZ UDPゴシック";
    public List<string> InputHistory { get; set; } = new();
    public List<string> ChapterInputHistory { get; set; } = new();
    public List<EditHistoryEntry> EditHistory { get; set; } = new();
    public List<int> EditHistoryColumnWidths { get; set; } = new();
    public int EditHistoryMaxCount { get; set; } = 20;
    public string FfmpegBuildPublishedAt { get; set; } = "";

    public static string SettingsPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AsterCut.settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (!File.Exists(SettingsPath)) return settings;
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return settings;

            settings.FFmpegPath = GetString(root, "FFmpegPath");
            settings.FFprobePath = GetString(root, "FFprobePath");
            settings.MpvPath = GetString(root, "MpvPath");
            if (GetString(root, "PreviewBackend") == "mpv") settings.PreviewBackend = "mpv";

            if (root.TryGetProperty("InputHistory", out var history) && history.ValueKind == JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in history.EnumerateArray())
                {
                    var text = (item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").Trim().Trim('"');
                    if (text.Length == 0 || !seen.Add(text)) continue;
                    settings.InputHistory.Add(text);
                    if (settings.InputHistory.Count >= 10) break;
                }
            }

            if (root.TryGetProperty("ChapterInputHistory", out var chapterHistory) && chapterHistory.ValueKind == JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in chapterHistory.EnumerateArray())
                {
                    var text = (item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "").Trim().Trim('"');
                    if (text.Length == 0 || !seen.Add(text)) continue;
                    settings.ChapterInputHistory.Add(text);
                    if (settings.ChapterInputHistory.Count >= 10) break;
                }
            }

            if (root.TryGetProperty("EditHistoryColumnWidths", out var columnWidths) && columnWidths.ValueKind == JsonValueKind.Array)
            {
                var widths = new List<int>();
                foreach (var item in columnWidths.EnumerateArray())
                {
                    if (!item.TryGetInt32(out var w) || w < 20 || w > 4000) { widths.Clear(); break; }
                    widths.Add(w);
                }
                if (widths.Count == 3) settings.EditHistoryColumnWidths = widths;
            }
            if (root.TryGetProperty("EditHistoryMaxCount", out var maxCount) && maxCount.TryGetInt32(out var maxValue) && maxValue >= 1)
            {
                settings.EditHistoryMaxCount = Math.Min(100, maxValue);
            }
            settings.FfmpegBuildPublishedAt = GetString(root, "FfmpegBuildPublishedAt");
            settings.FixedOutputDirectory = GetString(root, "FixedOutputDirectory");
            settings.OutputProfileName = GetString(root, "OutputProfileName");
            settings.SpecJapaneseFontName = GetString(root, "SpecJapaneseFontName");
            if (settings.SpecJapaneseFontName.Length == 0) settings.SpecJapaneseFontName = "BIZ UDPゴシック";

            if (root.TryGetProperty("EditHistory", out var editHistory) && editHistory.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in editHistory.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    if (GetString(entry, "InputPath").Length == 0) continue;
                    if (!entry.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object) continue;
                    settings.EditHistory.Add(new EditHistoryEntry
                    {
                        SavedAt = GetString(entry, "SavedAt"),
                        InputPath = GetString(entry, "InputPath"),
                        OutputPath = GetString(entry, "OutputPath"),
                        State = ParseEditState(state),
                    });
                    if (settings.EditHistory.Count >= settings.EditHistoryMaxCount) break;
                }
            }

            if (root.TryGetProperty("WindowWidth", out var width) && width.TryGetInt32(out var widthValue) && widthValue > 0)
            {
                settings.WindowWidth = Math.Max(1120, widthValue);
            }
            if (root.TryGetProperty("WindowHeight", out var height) && height.TryGetInt32(out var heightValue) && heightValue > 0)
            {
                settings.WindowHeight = Math.Max(900, heightValue);
            }
            if (root.TryGetProperty("Volume", out var volume) && volume.TryGetInt32(out var volumeValue))
            {
                settings.Volume = Math.Clamp(volumeValue, 1, 100);
            }
            if (root.TryGetProperty("VolumeSliderValue", out var slider) && slider.TryGetInt32(out var sliderValue))
            {
                settings.VolumeSliderValue = Math.Clamp(sliderValue, 0, 100);
            }
            else
            {
                settings.VolumeSliderValue = settings.Volume;
            }
            if (root.TryGetProperty("PreviewMuted", out var muted) &&
                (muted.ValueKind == JsonValueKind.True || muted.ValueKind == JsonValueKind.False))
            {
                settings.PreviewMuted = muted.GetBoolean();
            }
            if (root.TryGetProperty("RememberWindowSize", out var rememberSize) &&
                (rememberSize.ValueKind == JsonValueKind.True || rememberSize.ValueKind == JsonValueKind.False))
            {
                settings.RememberWindowSize = rememberSize.GetBoolean();
            }
            if (root.TryGetProperty("SuppressOpusInMp4Warning", out var suppressOpusWarning) &&
                (suppressOpusWarning.ValueKind == JsonValueKind.True || suppressOpusWarning.ValueKind == JsonValueKind.False))
            {
                settings.SuppressOpusInMp4Warning = suppressOpusWarning.GetBoolean();
            }
            if (root.TryGetProperty("AutoCloseOutputLogPopup", out var autoClosePopup) &&
                (autoClosePopup.ValueKind == JsonValueKind.True || autoClosePopup.ValueKind == JsonValueKind.False))
            {
                settings.AutoCloseOutputLogPopup = autoClosePopup.GetBoolean();
            }
            if (root.TryGetProperty("DeleteInvalidHistoryOnStartup", out var deleteInvalidHistory) &&
                (deleteInvalidHistory.ValueKind == JsonValueKind.True || deleteInvalidHistory.ValueKind == JsonValueKind.False))
            {
                settings.DeleteInvalidHistoryOnStartup = deleteInvalidHistory.GetBoolean();
            }
            if (root.TryGetProperty("ShowKeyframesOnSeekBar", out var showKeyframes) &&
                (showKeyframes.ValueKind == JsonValueKind.True || showKeyframes.ValueKind == JsonValueKind.False))
            {
                settings.ShowKeyframesOnSeekBar = showKeyframes.GetBoolean();
            }
            if (root.TryGetProperty("FixedOutputDirectoryEnabled", out var fixedOutputDir) &&
                (fixedOutputDir.ValueKind == JsonValueKind.True || fixedOutputDir.ValueKind == JsonValueKind.False))
            {
                settings.FixedOutputDirectoryEnabled = fixedOutputDir.GetBoolean();
            }
            if (settings.VolumeSliderValue == 0) settings.PreviewMuted = true;

            // 以前のアプリ内ダウンローダーが書き込んだパス (tools\BtbN-FFmpeg\bin) は無視する。
            var legacyBinDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\BtbN-FFmpeg\bin")).TrimEnd('\\');
            settings.FFmpegPath = DropLegacyPath(settings.FFmpegPath, legacyBinDir);
            settings.FFprobePath = DropLegacyPath(settings.FFprobePath, legacyBinDir);
        }
        catch
        {
            // 不正な設定は無視する。保存するときに書き直される。
        }
        return settings;
    }

    private static string DropLegacyPath(string path, string legacyBinDir)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (full.StartsWith(legacyBinDir + "\\", StringComparison.OrdinalIgnoreCase)) return "";
        }
        catch { /* そのまま返す */ }
        return path;
    }

    private static string GetString(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static long GetInt64(JsonElement obj, string name, long fallback)
    {
        return obj.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : fallback;
    }

    private static int? GetNullableInt(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    }

    private static bool? GetNullableBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        return null;
    }

    /// <summary>履歴 1 件分を JSON から読み取る (プロジェクトファイルの読み込みでも使う)。</summary>
    internal static EditHistoryEntry? ParseEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object) return null;
        return new EditHistoryEntry
        {
            SavedAt = GetString(entry, "SavedAt"),
            InputPath = GetString(entry, "InputPath"),
            OutputPath = GetString(entry, "OutputPath"),
            State = ParseEditState(state),
        };
    }

    /// <summary>編集状態 (履歴・プロジェクト共通) を JSON から読み取る。欠けた項目は null / 既定のままにする。</summary>
    internal static EditState ParseEditState(JsonElement state)
    {
        var result = new EditState
        {
            InputPath = GetString(state, "InputPath"),
            Speed = GetString(state, "Speed"),
            RotationIndex = GetNullableInt(state, "RotationIndex"),
            CutStartText = GetString(state, "CutStartText"),
            CutEndText = GetString(state, "CutEndText"),
            ClipTop = (int)GetInt64(state, "ClipTop", 0),
            ClipRight = (int)GetInt64(state, "ClipRight", 0),
            ClipBottom = (int)GetInt64(state, "ClipBottom", 0),
            ClipLeft = (int)GetInt64(state, "ClipLeft", 0),
            OutputModeIndex = GetNullableInt(state, "OutputModeIndex"),
            OutputModeUserSelected = GetNullableBool(state, "OutputModeUserSelected") ?? false,
            VideoCrfText = GetString(state, "VideoCrfText"),
            AudioBitrateIndex = GetNullableInt(state, "AudioBitrateIndex"),
            OutputPathText = GetString(state, "OutputPathText"),
            OutputPathIsCustom = GetNullableBool(state, "OutputPathIsCustom") ?? false,
            SplitOutputDirText = GetString(state, "SplitOutputDirText"),
            SplitOutputDirIsCustom = GetNullableBool(state, "SplitOutputDirIsCustom") ?? false,
            AppendTimestamp = GetNullableBool(state, "AppendTimestamp"),
            AppendSuffix = GetNullableBool(state, "AppendSuffix"),
            KeepMetadata = GetNullableBool(state, "KeepMetadata"),
            WriteEditorMetadata = GetNullableBool(state, "WriteEditorMetadata"),
            KeepDataBroadcast = GetNullableBool(state, "KeepDataBroadcast"),
            OutputProfileName = GetString(state, "OutputProfileName"),
            VideoFormat = GetString(state, "VideoFormat"),
            BitDepth = GetNullableInt(state, "BitDepth"),
            AudioFormat = GetString(state, "AudioFormat"),
            AudioBitrateKbps = GetNullableInt(state, "AudioBitrateKbps"),
            OutputContainer = GetString(state, "OutputContainer"),
        };
        if (state.TryGetProperty("Segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            result.Segments = new List<EditSegmentState>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object) continue;
                result.Segments.Add(new EditSegmentState
                {
                    StartMs = GetInt64(segment, "StartMs", 0),
                    EndMs = GetInt64(segment, "EndMs", 0),
                    Top = (int)GetInt64(segment, "Top", 0),
                    Right = (int)GetInt64(segment, "Right", 0),
                    Bottom = (int)GetInt64(segment, "Bottom", 0),
                    Left = (int)GetInt64(segment, "Left", 0),
                    JoinWithPrevious = segment.TryGetProperty("JoinWithPrevious", out var join) && join.ValueKind == JsonValueKind.True,
                });
            }
        }
        if (state.TryGetProperty("SelectedAudioIndices", out var audios) && audios.ValueKind == JsonValueKind.Array)
        {
            result.SelectedAudioIndices = new List<int>();
            foreach (var audio in audios.EnumerateArray())
            {
                if (audio.TryGetInt32(out var index)) result.SelectedAudioIndices.Add(index);
            }
        }
        if (state.TryGetProperty("KeepDataKinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array)
        {
            result.KeepDataKinds = new List<string>();
            foreach (var kind in kinds.EnumerateArray())
            {
                if (kind.ValueKind == JsonValueKind.String && kind.GetString() is { Length: > 0 } name) result.KeepDataKinds.Add(name);
            }
        }
        return result;
    }

    public bool Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json + "\r\n", new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>編集履歴の 1 件分。</summary>
internal sealed class EditHistoryEntry
{
    public string SavedAt { get; set; } = "";
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public EditState? State { get; set; }
}

/// <summary>履歴に保存される区間 1 件。</summary>
internal sealed class EditSegmentState
{
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public bool JoinWithPrevious { get; set; }
}

/// <summary>
/// 編集状態 (現在の編集内容一式)。欠けた項目を区別するため、
/// チェック類とインデックス類は nullable で持つ。
/// </summary>
internal sealed class EditState
{
    public string InputPath { get; set; } = "";
    public List<EditSegmentState>? Segments { get; set; }
    public List<int>? SelectedAudioIndices { get; set; }
    public List<string>? KeepDataKinds { get; set; }
    public string Speed { get; set; } = "";
    public int? RotationIndex { get; set; }
    public string CutStartText { get; set; } = "";
    public string CutEndText { get; set; } = "";
    public int ClipTop { get; set; }
    public int ClipRight { get; set; }
    public int ClipBottom { get; set; }
    public int ClipLeft { get; set; }
    public int? OutputModeIndex { get; set; }
    public bool OutputModeUserSelected { get; set; }
    public string VideoCrfText { get; set; } = "";
    public int? AudioBitrateIndex { get; set; }
    public string OutputPathText { get; set; } = "";
    public bool OutputPathIsCustom { get; set; }
    public string SplitOutputDirText { get; set; } = "";
    public bool SplitOutputDirIsCustom { get; set; }
    public bool? AppendTimestamp { get; set; }
    public bool? AppendSuffix { get; set; }
    public bool? KeepMetadata { get; set; }
    public bool? WriteEditorMetadata { get; set; }
    public bool? KeepDataBroadcast { get; set; }
    public string OutputProfileName { get; set; } = "";
    public string VideoFormat { get; set; } = "";
    public int? BitDepth { get; set; }
    public string AudioFormat { get; set; } = "";
    public int? AudioBitrateKbps { get; set; }
    public string OutputContainer { get; set; } = "";
}

/// <summary>指定パス → exe の隣 → PATH の順序で実行ファイルを探す。</summary>
internal static class ExecutableFinder
{
    public static string Find(string name, string preferredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            candidates.Add(Environment.ExpandEnvironmentVariables(preferredPath.Trim().Trim('"')));
        }
        candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name));
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return Path.GetFullPath(full);
            }
            catch { /* 次の候補へ */ }
        }
        return "";
    }
}
