using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AsterCut;

/// <summary>ffprobe の JSON 出力の 1 ストリーム分。</summary>
internal sealed class ProbeStream
{
    public int Index { get; set; }
    public string CodecType { get; set; } = "";
    public string CodecName { get; set; } = "";
    public string CodecTag { get; set; } = "";
    public string CodecLongName { get; set; } = "";
    public string Profile { get; set; } = "";
    public string TimeBase { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string Duration { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int SampleAspectNum { get; set; } = 1;
    public int SampleAspectDen { get; set; } = 1;
    public string AvgFrameRate { get; set; } = "";
    public string RFrameRate { get; set; } = "";
    public int Channels { get; set; }
    public int AttachedPic { get; set; }
    public string PixFmt { get; set; } = "";
    public string BitsPerRawSample { get; set; } = "";
    /// <summary>Display Matrix の回転 (反時計回りの度数文字列)。なければ空。</summary>
    public string DisplayMatrixRotation { get; set; } = "";
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Tag(string name) => Tags.TryGetValue(name, out var value) ? value : "";
}

/// <summary>ffprobe の JSON 出力全体。</summary>
internal sealed class ProbeResult
{
    public List<ProbeStream> Streams { get; } = new();
    public string FormatName { get; set; } = "";
    public string FormatLongName { get; set; } = "";
    public string FormatDuration { get; set; } = "";
    public string FormatStartTime { get; set; } = "";
    public Dictionary<string, string> FormatTags { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class Probe
{
    /// <summary>ffprobe を実行し、結果を返す。</summary>
    public static async Task<ProbeResult> InvokeAsync(string ffprobePath, string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            WorkingDirectory = Path.GetDirectoryName(ffprobePath) ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[]
        {
            "-v", "error",
            "-probesize", "100M",
            "-analyzeduration", "100M",
            "-max_probe_packets", "500000",
            "-show_streams", "-show_format", "-of", "json",
            inputPath,
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("ffprobe プロセスを起動できませんでした。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var raw = await stdoutTask;
        var diagnostics = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(raw))
        {
            var detail = string.IsNullOrWhiteSpace(diagnostics) ? "" : "\r\n" + diagnostics.Trim();
            throw new InvalidOperationException($"ffprobe が失敗しました (終了コード {process.ExitCode})。{detail}");
        }
        return Parse(raw);
    }

    private static ProbeResult Parse(string json)
    {
        var result = new ProbeResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
        {
            result.FormatName = GetText(format, "format_name");
            result.FormatLongName = GetText(format, "format_long_name");
            result.FormatDuration = GetText(format, "duration");
            result.FormatStartTime = GetText(format, "start_time");
            ReadTags(format, result.FormatTags);
        }
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in streams.EnumerateArray())
            {
                var stream = new ProbeStream
                {
                    Index = GetInt(element, "index"),
                    CodecType = GetText(element, "codec_type"),
                    CodecName = GetText(element, "codec_name"),
                    CodecTag = GetText(element, "codec_tag"),
                    CodecLongName = GetText(element, "codec_long_name"),
                    Profile = GetText(element, "profile"),
                    TimeBase = GetText(element, "time_base"),
                    StartTime = GetText(element, "start_time"),
                    Duration = GetText(element, "duration"),
                    Width = GetInt(element, "width"),
                    Height = GetInt(element, "height"),
                    SampleAspectNum = ParseAspectPart(GetText(element, "sample_aspect_ratio"), 0),
                    SampleAspectDen = ParseAspectPart(GetText(element, "sample_aspect_ratio"), 1),
                    AvgFrameRate = GetText(element, "avg_frame_rate"),
                    RFrameRate = GetText(element, "r_frame_rate"),
                    Channels = GetInt(element, "channels"),
                    PixFmt = GetText(element, "pix_fmt"),
                    BitsPerRawSample = GetText(element, "bits_per_raw_sample"),
                };
                if (element.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object)
                {
                    stream.AttachedPic = GetInt(disposition, "attached_pic");
                }
                if (element.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in sideData.EnumerateArray())
                    {
                        if (GetText(entry, "side_data_type") == "Display Matrix" && entry.TryGetProperty("rotation", out var rotation))
                        {
                            stream.DisplayMatrixRotation = rotation.ValueKind == JsonValueKind.String
                                ? rotation.GetString() ?? ""
                                : rotation.ToString();
                        }
                    }
                }
                ReadTags(element, stream.Tags);
                result.Streams.Add(stream);
            }
        }
        return result;
    }

    private static void ReadTags(JsonElement owner, Dictionary<string, string> tags)
    {
        if (!owner.TryGetProperty("tags", out var element) || element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            tags[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.ToString();
        }
    }

    private static string GetText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    /// <summary>「4:3」のような SAR 文字列の分子 (part 0) / 分母 (part 1) を取り出す。不明な場合は 1。</summary>
    private static int ParseAspectPart(string text, int part)
    {
        var pieces = (text ?? "").Split(':');
        if (pieces.Length == 2 && int.TryParse(pieces[part].Trim(), out var value) && value > 0) return value;
        return 1;
    }

    private static int GetInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)) return parsed;
        return 0;
    }

    /// <summary>「30000/1001」のようなフレームレート文字列を double にする。範囲チェック付き。</summary>
    public static double ConvertFrameRateToDouble(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0" || value == "N/A") return 0.0;
        double fps = 0.0;
        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash > 0 &&
            double.TryParse(trimmed[..slash], NumberStyles.Any, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(trimmed[(slash + 1)..], NumberStyles.Any, CultureInfo.InvariantCulture, out var denominator))
        {
            if (denominator > 0) fps = numerator / denominator;
        }
        else
        {
            double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out fps);
        }
        return fps is > 0.1 and < 1000 ? fps : 0.0;
    }

    public static double ParseSeconds(string text)
    {
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0.0;
    }
}

/// <summary>
/// ffprobe の packet=pts_time,dts_time (csv) からフレーム時刻テーブル (マイクロ秒) を作る。
/// </summary>
internal static class FrameIndex
{
    public static long[] ParsePacketTimes(string text, long baseUs)
    {
        var list = new List<long>(65536);
        if (!string.IsNullOrEmpty(text))
        {
            var pos = 0;
            var len = text.Length;
            while (pos < len)
            {
                var end = text.IndexOf('\n', pos);
                if (end < 0) end = len;
                var line = text.Substring(pos, end - pos).Trim();
                pos = end + 1;
                if (line.Length == 0) continue;
                var parts = line.Split(',');
                var ok = TryParseSeconds(parts[0], out var us);
                if (!ok && parts.Length > 1) ok = TryParseSeconds(parts[1], out us);
                if (!ok) continue;
                var rel = us - baseUs;
                if (rel < -2000) continue;
                if (rel < 0) rel = 0;
                list.Add(rel);
            }
        }
        var all = list.ToArray();
        Array.Sort(all);
        var unique = new List<long>(all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            if (unique.Count == 0 || all[i] != unique[unique.Count - 1]) unique.Add(all[i]);
        }
        return unique.ToArray();
    }

    /// <summary>
    /// ffprobe の packet=pts_time,dts_time,flags (csv) から、キーフレームの時刻 (マイクロ秒) を
    /// 並べ替えて重複を取り除いた配列を作る。時刻の基準と丸めは ParsePacketTimes と同じ。
    /// </summary>
    public static long[] ParseKeyframeTimes(string text, long baseUs)
    {
        var list = new List<long>(4096);
        if (!string.IsNullOrEmpty(text))
        {
            var pos = 0;
            var len = text.Length;
            while (pos < len)
            {
                var end = text.IndexOf('\n', pos);
                if (end < 0) end = len;
                var line = text.Substring(pos, end - pos).Trim();
                pos = end + 1;
                if (line.Length == 0) continue;
                var parts = line.Split(',');
                if (parts.Length < 3 || !parts[2].Contains('K')) continue;
                var ok = TryParseSeconds(parts[0], out var us);
                if (!ok && parts.Length > 1) ok = TryParseSeconds(parts[1], out us);
                if (!ok) continue;
                var rel = us - baseUs;
                if (rel < -2000) continue;
                if (rel < 0) rel = 0;
                list.Add(rel);
            }
        }
        var all = list.ToArray();
        Array.Sort(all);
        var unique = new List<long>(all.Length);
        for (var i = 0; i < all.Length; i++)
        {
            if (unique.Count == 0 || all[i] != unique[unique.Count - 1]) unique.Add(all[i]);
        }
        return unique.ToArray();
    }

    private static bool TryParseSeconds(string text, out long micros)
    {
        micros = 0;
        var s = text.Trim();
        if (s.Length == 0 || s == "N/A") return false;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return false;
        micros = (long)Math.Round(d * 1000000.0, MidpointRounding.AwayFromZero);
        return true;
    }

    public static int IndexAtOrBefore(long[] times, long timeUs)
    {
        if (times == null || times.Length == 0) return -1;
        if (timeUs < times[0]) return -1;
        int lo = 0, hi = times.Length - 1;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            if (times[mid] <= timeUs) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public static long[] IntervalStats(long[] times)
    {
        long min = long.MaxValue, max = 0;
        if (times != null)
        {
            for (var i = 1; i < times.Length; i++)
            {
                var d = times[i] - times[i - 1];
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }
        if (min == long.MaxValue) min = 0;
        return new[] { min, max };
    }
}

/// <summary>ミリ秒を HH:MM:SS.mmm に整形する。</summary>
internal static class TimeText
{
    public static string FormatClockFromMs(long ms)
    {
        if (ms < 0) ms = 0;
        var hours = ms / 3600000;
        var minutes = ms % 3600000 / 60000;
        var seconds = ms % 60000 / 1000;
        var millis = ms % 1000;
        return $"{hours:00}:{minutes:00}:{seconds:00}.{millis:000}";
    }
}
