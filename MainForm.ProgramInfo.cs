using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AsterCut;

/// <summary>
/// TS の番組情報 (SDT / EIT / TOT) を無劣化カットの出力へ移植する後処理。
/// ffmpeg は番組表を通常のストリームとして扱うため、そのままでは出力に残らない
/// (全体コピーでも置き場所が変わって番組表として読めなくなる)。
/// ここでは元ファイルからセクション単位で番組情報を取り出し、サービス ID と
/// トランスポートストリーム ID を出力側に合わせて書き換え、出力の SDT と
/// 置き換える形で合流させる。TOT は切り出し範囲内のものを位置に対応させて
/// 配置し、範囲内に無い場合は直前のものを時刻を進めて先頭に置く。
/// あわせて、番組に含まれる字幕・データ放送などのストリーム (PMT に記載された
/// 映像・音声以外のもの) も、出力の PMT に記載を加えたうえで、パケット単位で
/// 位置に対応させて引き継ぐ。PES の時刻は出力の映像時計に合わせて引き直し、
/// 時刻を持たないパケットは範囲内のパケット位置の割合で配置する。
/// </summary>
internal sealed partial class MainForm
{
    private const int TsPacketSize = 188;
    private const int SiPidNit = 0x10;
    private const int SiPidSdt = 0x11;
    private const int SiPidEit = 0x12;
    private const int SiPidTot = 0x14;

    private sealed class SiSection
    {
        public int Pid;
        public byte[] Bytes = Array.Empty<byte>();
        public long PcrAtFirst = -1;
        public byte TableId => Bytes.Length > 0 ? Bytes[0] : (byte)0;
        public byte SectionNumber => Bytes.Length > 6 ? Bytes[6] : (byte)0;
    }

    /// <summary>1 PID 分のセクション組み立て器。PUSI とポインターを処理し、完成したセクションを返す。</summary>
    private sealed class SiSectionAssembler
    {
        private readonly List<byte> _buffer = new();
        private long _firstPcr = -1;

        public List<SiSection> Feed(byte[] data, int packetOffset, long currentPcr, int pid)
        {
            var done = new List<SiSection>();
            var pos = packetOffset + 4;
            var end = packetOffset + TsPacketSize;
            var afc = (data[packetOffset + 3] >> 4) & 3;
            if (afc == 2) return done; // 適応フィールドのみ (ペイロードなし)
            if (afc == 3) pos += 1 + data[packetOffset + 4];
            if (pos >= end) return done;
            var pusi = ((data[packetOffset + 1] >> 6) & 1) != 0;
            if (pusi)
            {
                var pointer = data[pos];
                pos++;
                if (_buffer.Count > 0 && pointer > 0)
                {
                    var take = Math.Min(pointer, end - pos);
                    for (var i = 0; i < take; i++) _buffer.Add(data[pos + i]);
                    pos += pointer;
                    TryComplete(done, pid, currentPcr);
                }
                if (_buffer.Count > 0) _buffer.Clear(); // 前のセクションが途中で切れた場合は破棄する
                while (pos < end && data[pos] != 0xFF)
                {
                    if (_buffer.Count == 0) _firstPcr = currentPcr;
                    for (var i = pos; i < end; i++) _buffer.Add(data[i]);
                    pos = end;
                    TryComplete(done, pid, currentPcr);
                }
            }
            else if (_buffer.Count > 0)
            {
                for (var i = pos; i < end; i++) _buffer.Add(data[i]);
                TryComplete(done, pid, currentPcr);
            }
            return done;
        }

        private void TryComplete(List<SiSection> done, int pid, long currentPcr)
        {
            while (_buffer.Count >= 3)
            {
                var total = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
                if (_buffer.Count < total) return;
                var bytes = _buffer.GetRange(0, total).ToArray();
                _buffer.RemoveRange(0, total);
                done.Add(new SiSection { Pid = pid, Bytes = bytes, PcrAtFirst = _firstPcr });
                _firstPcr = currentPcr;
            }
        }
    }

    private static readonly uint[] SiCrcTable = BuildSiCrcTable();

    private static uint[] BuildSiCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i << 24;
            for (var k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7u : c << 1;
            table[i] = c;
        }
        return table;
    }

    private static void WriteSiSectionCrc(byte[] section)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < section.Length - 4; i++) crc = (crc << 8) ^ SiCrcTable[((crc >> 24) ^ section[i]) & 0xFF];
        section[^4] = (byte)(crc >> 24);
        section[^3] = (byte)(crc >> 16);
        section[^2] = (byte)(crc >> 8);
        section[^1] = (byte)crc;
    }

    private static int ReadPid(byte[] data, int offset) => ((data[offset + 1] & 0x1F) << 8) | data[offset + 2];

    private static long ReadPcrTicks(byte[] data, int offset)
    {
        var afc = (data[offset + 3] >> 4) & 3;
        if (afc != 2 && afc != 3) return -1;
        if (data[offset + 4] < 7 || (data[offset + 5] & 0x10) == 0) return -1;
        return ((long)data[offset + 6] << 25) | ((long)data[offset + 7] << 17) | ((long)data[offset + 8] << 9)
            | ((long)data[offset + 9] << 1) | ((long)data[offset + 10] >> 7);
    }

    /// <summary>セクションを持つパケットの、セクション開始位置 (絶対オフセット)。対象外なら -1。</summary>
    private static int SectionPayloadStart(byte[] data, int offset)
    {
        if (((data[offset + 1] >> 6) & 1) == 0) return -1; // PUSI でなければセクションは始まらない
        var afc = (data[offset + 3] >> 4) & 3;
        if (afc == 2) return -1;
        var pos = offset + 4;
        if (afc == 3) pos += 1 + data[offset + 4];
        if (pos >= offset + TsPacketSize) return -1;
        pos += 1 + data[pos]; // ポインターフィールド
        return pos < offset + TsPacketSize ? pos : -1;
    }

    /// <summary>PAT の最初の番組から PMT の PID を読む。読めなければ -1。</summary>
    private static int ParsePatPmtPid(byte[] data, int offset)
    {
        var pos = SectionPayloadStart(data, offset);
        if (pos < 0 || data[pos] != 0x00) return -1;
        var sectionLength = ((data[pos + 1] & 0x0F) << 8) | data[pos + 2];
        var entryEnd = Math.Min(pos + 3 + sectionLength - 4, offset + TsPacketSize);
        for (var e = pos + 8; e + 4 <= entryEnd; e += 4)
        {
            var programNumber = (data[e] << 8) | data[e + 1];
            if (programNumber != 0) return ((data[e + 2] & 0x1F) << 8) | data[e + 3];
        }
        return -1;
    }

    /// <summary>PAT の最初の番組の番組番号を読む。読めなければ -1。</summary>
    private static int ParsePatMainService(byte[] data, int offset)
    {
        var pos = SectionPayloadStart(data, offset);
        if (pos < 0 || data[pos] != 0x00) return -1;
        var sectionLength = ((data[pos + 1] & 0x0F) << 8) | data[pos + 2];
        var entryEnd = Math.Min(pos + 3 + sectionLength - 4, offset + TsPacketSize);
        for (var e = pos + 8; e + 4 <= entryEnd; e += 4)
        {
            var programNumber = (data[e] << 8) | data[e + 1];
            if (programNumber != 0) return programNumber;
        }
        return -1;
    }

    /// <summary>PAT の TS ID を読む。読めなければ -1。</summary>
    private static int ParsePatTsId(byte[] data, int offset)
    {
        var pos = SectionPayloadStart(data, offset);
        if (pos < 0 || data[pos] != 0x00) return -1;
        return (data[pos + 3] << 8) | data[pos + 4];
    }

    /// <summary>PMT から映像ストリームの PID を読む。読めなければ -1。</summary>
    private static int ParsePmtVideoPid(byte[] data, int offset)
    {
        var pos = SectionPayloadStart(data, offset);
        if (pos < 0 || data[pos] != 0x02) return -1;
        var sectionLength = ((data[pos + 1] & 0x0F) << 8) | data[pos + 2];
        var programInfoLength = ((data[pos + 10] & 0x0F) << 8) | data[pos + 11];
        var entryEnd = Math.Min(pos + 3 + sectionLength - 4, offset + TsPacketSize);
        for (var e = pos + 12 + programInfoLength; e + 5 <= entryEnd;)
        {
            var streamType = data[e];
            var elementaryPid = ((data[e + 1] & 0x1F) << 8) | data[e + 2];
            var esInfoLength = ((data[e + 3] & 0x0F) << 8) | data[e + 4];
            if (streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x24 or 0x42) return elementaryPid;
            e += 5 + esInfoLength;
        }
        return -1;
    }

    /// <summary>映像 PES の PTS (90kHz 単位) を読む。PES 先頭でなければ -1。</summary>
    private static long ReadPesPtsTicks(byte[] data, int offset)
    {
        var afc = (data[offset + 3] >> 4) & 3;
        if (afc == 2 || ((data[offset + 1] >> 6) & 1) == 0) return -1;
        var pos = offset + 4;
        if (afc == 3) pos += 1 + data[offset + 4];
        if (pos + 14 > offset + TsPacketSize) return -1;
        if (data[pos] != 0x00 || data[pos + 1] != 0x00 || data[pos + 2] != 0x01) return -1;
        var streamId = data[pos + 3];
        if (streamId < 0xE0 || streamId > 0xEF) return -1;
        var ptsDtsFlags = (data[pos + 7] >> 6) & 3;
        if (ptsDtsFlags == 0) return -1;
        return (((long)(data[pos + 9] >> 1) & 0x07) << 30) | ((long)data[pos + 10] << 22)
            | (((long)(data[pos + 11] >> 1) & 0x7F) << 15) | ((long)data[pos + 12] << 7)
            | (((long)(data[pos + 13] >> 1) & 0x7F));
    }

    /// <summary>
    /// 任意の PES 先頭パケットから PTS / DTS (90kHz 単位) を読む。PES でなければ (-1, -1)。
    /// 字幕やデータ放送など、映像以外の PES の時刻を読むために使う (ReadPesPtsTicks は映像専用)。
    /// </summary>
    private static (long Pts, long Dts) ReadPesHeaderTicks(byte[] data, int offset)
    {
        var afc = (data[offset + 3] >> 4) & 3;
        if (afc == 2 || ((data[offset + 1] >> 6) & 1) == 0) return (-1, -1);
        var pos = offset + 4;
        if (afc == 3) pos += 1 + data[offset + 4];
        if (pos + 14 > offset + TsPacketSize) return (-1, -1);
        if (data[pos] != 0x00 || data[pos + 1] != 0x00 || data[pos + 2] != 0x01) return (-1, -1);
        var streamId = data[pos + 3];
        // ヘッダーを持たないストリーム (パディング等) は対象外。
        if (streamId is 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xFF) return (-1, -1);
        if ((data[pos + 6] >> 6) != 2) return (-1, -1); // PES ヘッダーの '10' マーカー
        var flags = (data[pos + 7] >> 6) & 3;
        if (flags == 0) return (-1, -1);
        long Decode(int p) => (((long)(data[p] >> 1) & 0x07) << 30) | ((long)data[p + 1] << 22)
            | (((long)(data[p + 2] >> 1) & 0x7F) << 15) | ((long)data[p + 3] << 7)
            | (((long)(data[p + 4] >> 1) & 0x7F));
        var pts = Decode(pos + 9);
        var dts = flags == 3 && pos + 19 <= offset + TsPacketSize ? Decode(pos + 14) : -1;
        return (pts, dts);
    }

    /// <summary>PES のタイムスタンプ (5 バイト) を書き換える。先頭バイトの上位ニブル (接頭辞) は元の値を保つ。</summary>
    private static void WritePesTimestamp(byte[] packet, int pos, long value)
    {
        packet[pos] = (byte)((packet[pos] & 0xF0) | ((int)(((value >> 30) & 0x07) << 1) | 1));
        packet[pos + 1] = (byte)(value >> 22);
        packet[pos + 2] = (byte)((((value >> 15) & 0x7F) << 1) | 1);
        packet[pos + 3] = (byte)(value >> 7);
        packet[pos + 4] = (byte)(((value & 0x7F) << 1) | 1);
    }

    /// <summary>パケット内のペイロード開始位置 (絶対オフセット)。ペイロードが無ければ -1。</summary>
    private static int PacketPayloadStart(byte[] data, int offset)
    {
        var afc = (data[offset + 3] >> 4) & 3;
        if (afc == 2) return -1;
        var pos = offset + 4;
        if (afc == 3) pos += 1 + data[offset + 4];
        return pos < offset + TsPacketSize ? pos : -1;
    }

    /// <summary>出力 TS の PAT から PMT の PID を読む。読めなければ -1。</summary>
    private static int ReadOutputPmtPid(string outputPath)
    {
        try
        {
            var buffer = new byte[TsPacketSize * 512];
            int read;
            using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                read = fs.Read(buffer, 0, buffer.Length);
            }
            for (var offset = 0; offset + TsPacketSize <= read; offset += TsPacketSize)
            {
                if (buffer[offset] != 0x47 || ReadPid(buffer, offset) != 0) continue;
                var pid = ParsePatPmtPid(buffer, offset);
                if (pid >= 0) return pid;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>ファイル先頭から、指定 PID の最初のセクション (バイト列) を読む。読めなければ null。</summary>
    private static byte[]? ReadFirstSectionBytes(string path, int pid)
    {
        try
        {
            var buffer = new byte[TsPacketSize * 1024];
            int read;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                read = fs.Read(buffer, 0, buffer.Length);
            }
            var assembler = new SiSectionAssembler();
            for (var offset = 0; offset + TsPacketSize <= read; offset += TsPacketSize)
            {
                if (buffer[offset] != 0x47 || ReadPid(buffer, offset) != pid) continue;
                var done = assembler.Feed(buffer, offset, -1, pid);
                if (done.Count > 0) return done[0].Bytes;
            }
        }
        catch { }
        return null;
    }

    /// <summary>出力 TS の PAT / PMT から、トランスポートストリーム ID と番組番号 (サービス ID) を読む。失敗したら 1 / 1。</summary>
    private static (int TsId, int ServiceId, int VideoPid) ReadOutputProgramInfo(string outputPath)
    {
        try
        {
            var buffer = new byte[TsPacketSize * 512];
            int read;
            using (var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                read = fs.Read(buffer, 0, buffer.Length);
            }
            var pmtPid = -1;
            var tsId = 1;
            for (var offset = 0; offset + TsPacketSize <= read; offset += TsPacketSize)
            {
                if (buffer[offset] != 0x47) continue;
                var pid = ReadPid(buffer, offset);
                var pusi = ((buffer[offset + 1] >> 6) & 1) != 0;
                if (!pusi) continue;
                var pos = offset + 4;
                var afc = (buffer[offset + 3] >> 4) & 3;
                if (afc == 3) pos += 1 + buffer[offset + 4];
                if (afc == 2 || pos >= offset + TsPacketSize) continue;
                pos += 1 + buffer[pos]; // ポインターフィールドを飛ばす
                if (pos + 12 >= offset + TsPacketSize) continue;
                if (pid == 0 && buffer[pos] == 0x00)
                {
                    tsId = (buffer[pos + 3] << 8) | buffer[pos + 4];
                    var sectionLength = ((buffer[pos + 1] & 0x0F) << 8) | buffer[pos + 2];
                    var entryEnd = Math.Min(pos + 3 + sectionLength - 4, offset + TsPacketSize);
                    for (var e = pos + 8; e + 4 <= entryEnd; e += 4)
                    {
                        var programNumber = (buffer[e] << 8) | buffer[e + 1];
                        if (programNumber != 0) { pmtPid = ((buffer[e + 2] & 0x1F) << 8) | buffer[e + 3]; break; }
                    }
                }
                else if (pmtPid >= 0 && pid == pmtPid && buffer[pos] == 0x02)
                {
                    return (tsId, (buffer[pos + 3] << 8) | buffer[pos + 4], ParsePmtVideoPid(buffer, offset));
                }
            }
        }
        catch { }
        return (1, 1, -1);
    }

    /// <summary>SDT セクションのサービスループに、指定サービスのエントリが含まれるか。</summary>
    private static bool SdtContainsService(byte[] section, int serviceId)
    {
        if (section.Length < 11) return false;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var loopEnd = Math.Min(3 + sectionLength - 4, section.Length);
        for (var e = 11; e + 5 <= loopEnd;)
        {
            if (((section[e] << 8) | section[e + 1]) == serviceId) return true;
            var descLen = ((section[e + 3] & 0x0F) << 8) | section[e + 4];
            e += 5 + descLen;
        }
        return false;
    }

    private static int BcdDecode(byte value) => ((value >> 4) * 10) + (value & 0x0F);

    private static byte BcdEncode(int value) => (byte)(((value / 10) << 4) | (value % 10));

    /// <summary>TOT セクションの UTC 時刻を指定秒数だけ進め、CRC を付け直す。</summary>
    private static byte[] AdvanceTotSection(byte[] section, long deltaSeconds)
    {
        var result = (byte[])section.Clone();
        var mjd = (result[3] << 8) | result[4];
        var total = mjd * 86400L + BcdDecode(result[5]) * 3600L + BcdDecode(result[6]) * 60L + BcdDecode(result[7]) + deltaSeconds;
        var newMjd = (int)(total / 86400);
        var rest = total % 86400;
        result[3] = (byte)(newMjd >> 8);
        result[4] = (byte)newMjd;
        result[5] = BcdEncode((int)(rest / 3600));
        result[6] = BcdEncode((int)(rest % 3600) / 60);
        result[7] = BcdEncode((int)(rest % 60));
        WriteSiSectionCrc(result);
        return result;
    }

    /// <summary>
    /// 元ファイルの番組情報を、無劣化カットの出力 TS へ移植する。
    /// 失敗しても出力そのものは使えるため、例外は呼び出し側で記録するだけにとどめる。
    /// </summary>
    private bool PreserveProgramInfo(string sourcePath, string outputPath, long startMs, long endMs)
    {
        var videoStartSec = 0.0;
        if (_videoStreams.Count > 0)
        {
            double.TryParse(_videoStreams[0].StartTime, NumberStyles.Float, CultureInfo.InvariantCulture, out videoStartSec);
        }
        var startTicks = (long)Math.Round(videoStartSec * 90000.0) + startMs * 90;
        var endTicks = (long)Math.Round(videoStartSec * 90000.0) + endMs * 90;

        // ---- 元ファイルを走査し、番組情報のセクションを集める (切り出し範囲の終わりまで) ----
        var latest = new Dictionary<(int Pid, byte TableId, byte SectionNumber, int ServiceKey), SiSection>();
        var pfTimeline = new List<SiSection>();
        var totsInRange = new List<SiSection>();
        SiSection? lastTotBefore = null;
        var assemblers = new Dictionary<int, SiSectionAssembler>
        {
            [SiPidNit] = new(),
            [SiPidSdt] = new(),
            [SiPidEit] = new(),
            [SiPidTot] = new(),
        };
        var pcrPid = -1;
        long currentPcr = -1;
        var allTots = new List<SiSection>();
        var pcrSamples = new List<long>();
        var pmtPid = -1;
        var patMainService = -1;
        var sourceTsId = -1;
        var videoPid = -1;
        // 字幕・データ放送など、番組に含まれる映像・音声以外のストリーム (PMT 記載分) の引き継ぎ用。
        var pmtAssembler = new SiSectionAssembler();
        SiSection? sourcePmt = null;
        var extraEntries = new List<(int Pid, byte[] Raw)>();
        var extraPids = new HashSet<int>();
        var dsmccPids = new HashSet<int>();
        var dataPackets = new List<(long Target, int Pid, byte[] Packet, long Pts, long Dts, long Ordinal, bool OrdinalBased)>();
        var carryState = new Dictionary<int, (bool Active, long Target, bool OrdinalBased)>();
        long packetOrdinal = 0;
        long firstVideoOrdinal = -1;
        long lastVideoOrdinal = -1;
        long lastVideoPtsInRange = -1;
        var dataCapped = false;
        var scanBuffer = new byte[TsPacketSize * 8192];

        // 元の PMT から、引き継ぎ対象のストリーム (映像・音声以外) の記載を取り出す。
        void ParseExtraEntries()
        {
            if (sourcePmt == null || videoPid < 0) return;
            // データ放送 (DSM-CC) は、出力タブの「データ放送を引き継ぐ」が外れている場合は引き継がない。
            var keepDataBroadcast = _keepDataBroadcastCheckBox == null || _keepDataBroadcastCheckBox.Checked;
            var b = sourcePmt.Bytes;
            if (b.Length < 12) return;
            var sectionLength = ((b[1] & 0x0F) << 8) | b[2];
            var programInfoLength = ((b[10] & 0x0F) << 8) | b[11];
            var end = Math.Min(3 + sectionLength - 4, b.Length);
            for (var e = 12 + programInfoLength; e + 5 <= end;)
            {
                var type = b[e];
                var epid = ((b[e + 1] & 0x1F) << 8) | b[e + 2];
                var esLen = ((b[e + 3] & 0x0F) << 8) | b[e + 4];
                if (e + 5 + esLen > b.Length) break;
                // 映像と音声は ffmpeg 側の担当なので除く (選択されていない音声も含む)。
                // 番組表などのテーブル PID も除く。
                if (epid != videoPid && type is not (0x03 or 0x04 or 0x0F or 0x11)
                    && (keepDataBroadcast || type != 0x0D)
                    && epid is not (0x00 or 0x10 or SiPidSdt or SiPidEit or 0x13 or SiPidTot)
                    && extraEntries.All(x => x.Pid != epid))
                {
                    var raw = new byte[5 + esLen];
                    Array.Copy(b, e, raw, 0, raw.Length);
                    extraEntries.Add((epid, raw));
                    extraPids.Add(epid);
                    if (type == 0x0D) dsmccPids.Add(epid);
                }
                e += 5 + esLen;
            }
        }

        void AddDataPacket(int pid, int offset, long target, bool ordinalBased, long pts, long dts)
        {
            if (dataCapped) return;
            if (dataPackets.Count >= 300000) { dataCapped = true; return; }
            var copy = new byte[TsPacketSize];
            Array.Copy(scanBuffer, offset, copy, 0, TsPacketSize);
            dataPackets.Add((target, pid, copy, pts, dts, packetOrdinal, ordinalBased));
        }

        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            var stop = false;
            while (!stop && (read = source.Read(scanBuffer, 0, scanBuffer.Length)) > 0)
            {
                for (var offset = 0; offset + TsPacketSize <= read && !stop; offset += TsPacketSize)
                {
                    if (scanBuffer[offset] != 0x47) continue;
                    var pid = ReadPid(scanBuffer, offset);
                    packetOrdinal++;
                    var pcr = ReadPcrTicks(scanBuffer, offset);
                    if (pcr >= 0)
                    {
                        if (pcrPid < 0) pcrPid = pid;
                        if (pid == pcrPid)
                        {
                            currentPcr = pcr;
                            if (pcr > endTicks + 180000) stop = true; // 範囲の終わりを十分に過ぎたら走査を終える
                        }
                    }
                    // PAT / PMT から映像の PID を知り、映像 PES の PTS と PCR の差を集める
                    // (番組情報の配置を、パケット位置の時計ではなく表示時刻に合わせるため)。
                    if (pid == 0 && pmtPid < 0) pmtPid = ParsePatPmtPid(scanBuffer, offset);
                    if (pid == 0 && patMainService < 0) patMainService = ParsePatMainService(scanBuffer, offset);
                    if (pid == 0 && sourceTsId < 0) sourceTsId = ParsePatTsId(scanBuffer, offset);
                    else if (pmtPid >= 0 && pid == pmtPid && videoPid < 0) videoPid = ParsePmtVideoPid(scanBuffer, offset);
                    var samplePid = videoPid >= 0 ? videoPid : pcrPid;
                    if (samplePid >= 0 && pid == samplePid && pcrSamples.Count < 400)
                    {
                        var pts = ReadPesPtsTicks(scanBuffer, offset);
                        var pcrForSample = pcr >= 0 ? pcr : currentPcr;
                        if (pts >= 0 && pcrForSample >= 0) pcrSamples.Add(pts - pcrForSample);
                    }
                    // 映像パケットの位置 (時刻を持たないストリームの配置位置の算出に使う)。
                    if (videoPid >= 0 && pid == videoPid)
                    {
                        var vpts = ReadPesPtsTicks(scanBuffer, offset);
                        if (vpts >= startTicks && firstVideoOrdinal < 0) firstVideoOrdinal = packetOrdinal;
                        if (vpts >= 0 && vpts <= endTicks) { lastVideoOrdinal = packetOrdinal; lastVideoPtsInRange = vpts; }
                    }
                    // 元の PMT を組み立て、映像・音声以外のストリーム (字幕・データ放送など) を知る。
                    if (pmtPid >= 0 && pid == pmtPid && sourcePmt == null)
                    {
                        foreach (var section in pmtAssembler.Feed(scanBuffer, offset, currentPcr, pid))
                        {
                            sourcePmt = section;
                            ParseExtraEntries();
                        }
                    }
                    // 字幕・データ放送などのパケットを、範囲内ぶんだけ集める。
                    if (extraPids.Contains(pid))
                    {
                        var pusi = ((scanBuffer[offset + 1] >> 6) & 1) != 0;
                        if (pusi)
                        {
                            var (pesPts, pesDts) = ReadPesHeaderTicks(scanBuffer, offset);
                            if (pesPts >= 0)
                            {
                                var inRange = pesPts >= startTicks && pesPts <= endTicks;
                                carryState[pid] = (inRange, pesPts - startTicks, false);
                                if (inRange) AddDataPacket(pid, offset, pesPts - startTicks, false, pesPts, pesDts);
                            }
                            else
                            {
                                // 時刻を持たないバーストは、パケット位置の割合で配置する (解決は走査の後)。
                                carryState[pid] = (true, -2, true);
                                AddDataPacket(pid, offset, -2, true, -1, -1);
                            }
                        }
                        else if (carryState.TryGetValue(pid, out var state) && state.Active)
                        {
                            AddDataPacket(pid, offset, state.Target, state.OrdinalBased, -1, -1);
                        }
                    }
                    if (!assemblers.TryGetValue(pid, out var assembler)) continue;
                    foreach (var section in assembler.Feed(scanBuffer, offset, currentPcr, pid))
                    {
                        if (pid == SiPidTot)
                        {
                            allTots.Add(section);
                            continue;
                        }
                        if (pid == SiPidEit && section.TableId == 0x4E)
                        {
                            // 現在/次番組は時刻で内容が変わるため、最新集合ではなく時系列で保持する。
                            pfTimeline.Add(section);
                            continue;
                        }
                        var serviceKey = 0;
                        if (pid == SiPidEit && section.Bytes.Length > 4)
                        {
                            serviceKey = (section.Bytes[3] << 8) | section.Bytes[4];
                        }
                        else if (pid == SiPidSdt && section.Bytes.Length > 12)
                        {
                            serviceKey = (section.Bytes[11] << 8) | section.Bytes[12];
                        }
                        latest[(pid, section.TableId, section.SectionNumber, serviceKey)] = section;
                    }
                }
            }
        }

        // PCR (パケット位置の時計) と PTS (表示時刻) のずれの中央値。
        // 番組情報の配置は、パケット位置ではなく表示時刻にそろえる。
        var pcrToPtsOffset = 0L;
        if (pcrSamples.Count >= 3)
        {
            var sorted = pcrSamples.ToArray();
            Array.Sort(sorted);
            pcrToPtsOffset = sorted[sorted.Length / 2];
        }
        long ToContentTicks(long pcr) => pcr < 0 ? pcr : pcr + pcrToPtsOffset;
        foreach (var tot in allTots)
        {
            var content = ToContentTicks(tot.PcrAtFirst);
            if (content >= startTicks && content <= endTicks) totsInRange.Add(tot);
            else if (content >= 0 && content < startTicks) lastTotBefore = tot;
        }

        // 引き継ぎパケットの配置位置を確定する。時刻を持たないパケットは、範囲内の映像パケットの
        // 位置の割合から求める。映像の実際の終わりを大きく過ぎるものは落とす。
        var dataEvents = new List<(long Target, int Pid, byte[] Packet, long Pts, long Dts)>();
        {
            var span = Math.Max(1, endTicks - startTicks);
            var trueSpan = lastVideoPtsInRange >= 0 ? lastVideoPtsInRange - startTicks : span;
            foreach (var dp in dataPackets)
            {
                long target;
                if (dp.OrdinalBased)
                {
                    if (firstVideoOrdinal < 0 || lastVideoOrdinal <= firstVideoOrdinal) continue;
                    if (dp.Ordinal < firstVideoOrdinal || dp.Ordinal > lastVideoOrdinal) continue;
                    target = (dp.Ordinal - firstVideoOrdinal) * trueSpan / (lastVideoOrdinal - firstVideoOrdinal);
                }
                else
                {
                    target = dp.Target;
                }
                if (target < 0 || target > trueSpan + 45000) continue; // 45,000 = 0.5 秒の余裕
                dataEvents.Add((target, dp.Pid, dp.Packet, dp.Pts, dp.Dts));
            }
            dataEvents = dataEvents.OrderBy(e => e.Target).ToList(); // 安定ソート: 同じ位置では収集順を保つ
        }

        // ---- 主サービスの決定 (EIT のサービスに合わせ、無ければ SDT の先頭サービス) ----
        SiSection? sdtSection = null;
        var eitServiceCounts = new Dictionary<int, int>();
        foreach (var section in pfTimeline)
        {
            if (section.Bytes.Length <= 4) continue;
            var svc = (section.Bytes[3] << 8) | section.Bytes[4];
            eitServiceCounts[svc] = eitServiceCounts.TryGetValue(svc, out var count) ? count + 1 : 1;
        }
        var mainService = -1;
        var bestCount = -1;
        foreach (var pair in eitServiceCounts)
        {
            if (pair.Value > bestCount) { bestCount = pair.Value; mainService = pair.Key; }
        }
        // 主サービスは、PMT を読んだ PAT 先頭の番組を優先する。EIT の出現回数だけでは、
        // EPG 専用のサービスなど映像を持たないサービスが選ばれてしまうことがあるため。
        if (patMainService > 0) mainService = patMainService;
        foreach (var pair in latest)
        {
            if (pair.Key.Pid != SiPidSdt) continue;
            if (mainService < 0 || pair.Key.ServiceKey == mainService) { sdtSection = pair.Value; if (mainService >= 0) break; }
        }
        if (sdtSection == null)
        {
            // SDT は全サービスを 1 つのセクションに載せるため、先頭が主サービスでなくても、
            // サービスループの中に主サービスがあればそれを使う。
            foreach (var pair in latest)
            {
                if (pair.Key.Pid != SiPidSdt) continue;
                if (SdtContainsService(pair.Value.Bytes, mainService)) { sdtSection = pair.Value; break; }
            }
        }
        if (sdtSection == null)
        {
            // それでも見つからなければ、どれか 1 つの SDT を使う (無いよりはましなため)。
            foreach (var pair in latest)
            {
                if (pair.Key.Pid == SiPidSdt) { sdtSection = pair.Value; break; }
            }
        }
        if (mainService < 0)
        {
            if (sdtSection == null && dataEvents.Count == 0) { WriteLog("番組情報: 元ファイルに番組情報 (SDT / EIT) が見つからないため、移植しませんでした。"); return false; }
            if (sdtSection != null) mainService = (sdtSection.Bytes[11] << 8) | sdtSection.Bytes[12];
        }

        var (outTsId, outServiceId, outVideoPid) = ReadOutputProgramInfo(outputPath);
        // 元の TS ID と主サービスが分かる場合は、出力側の PAT / PMT を元の ID に合わせ、
        // 番組情報は元の ID のまま移植する。ID を出力側の初期値 (1) に書き換えると、
        // 再生側が番組を元の放送と同一視できず、データ放送などを取得できないことがあるため。
        var preserveIds = sourceTsId > 0 && mainService > 0;
        var siTsId = preserveIds ? sourceTsId : outTsId;
        var siServiceId = preserveIds ? mainService : outServiceId;

        // 出力の PMT に、引き継ぐストリームの記載を加える (記載が無いと再生側がストリームを見つけられない)。
        var outPmtPid = ReadOutputPmtPid(outputPath);
        byte[]? patchedPmtSection = null;
        if (extraEntries.Count > 0 && dataEvents.Count > 0 && outPmtPid >= 0)
        {
            // 出力側で使われている PID と重なるものは引き継がない (出力の PMT に載っている
            // 全ストリームの PID が対象。第 2 映像や音声の PID と重なると出力が壊れるため)。
            var reserved = new HashSet<int> { 0, outPmtPid, SiPidSdt, SiPidEit, SiPidTot };
            if (outVideoPid >= 0) reserved.Add(outVideoPid);
            var outPmtSection = ReadFirstSectionBytes(outputPath, outPmtPid);
            if (outPmtSection != null && outPmtSection.Length >= 12)
            {
                var oSectionLength = ((outPmtSection[1] & 0x0F) << 8) | outPmtSection[2];
                var oProgramInfoLength = ((outPmtSection[10] & 0x0F) << 8) | outPmtSection[11];
                var oEnd = Math.Min(3 + oSectionLength - 4, outPmtSection.Length);
                for (var e = 12 + oProgramInfoLength; e + 5 <= oEnd;)
                {
                    reserved.Add(((outPmtSection[e + 1] & 0x1F) << 8) | outPmtSection[e + 2]);
                    var oEsLen = ((outPmtSection[e + 3] & 0x0F) << 8) | outPmtSection[e + 4];
                    e += 5 + oEsLen;
                }
            }
            dataEvents = dataEvents.Where(e => !reserved.Contains(e.Pid)).ToList();
            var usable = extraEntries.Where(x => !reserved.Contains(x.Pid) && dataEvents.Any(e => e.Pid == x.Pid)).ToList();
            if (outPmtSection != null && usable.Count > 0)
            {
                var oLen = ((outPmtSection[1] & 0x0F) << 8) | outPmtSection[2];
                if (3 + oLen <= outPmtSection.Length)
                {
                    var coreLen = 3 + oLen - 4;
                    var oPil = ((outPmtSection[10] & 0x0F) << 8) | outPmtSection[11];
                    // 元の PMT の番組共通記述子も引き継ぐ (出力の PMT には無いため)。
                    var progDesc = Array.Empty<byte>();
                    if (sourcePmt != null && sourcePmt.Bytes.Length >= 12)
                    {
                        var sPil = ((sourcePmt.Bytes[10] & 0x0F) << 8) | sourcePmt.Bytes[11];
                        if (sPil > 0 && 12 + sPil <= sourcePmt.Bytes.Length)
                        {
                            progDesc = new byte[sPil];
                            Array.Copy(sourcePmt.Bytes, 12, progDesc, 0, sPil);
                        }
                    }
                    var outEntriesLen = coreLen - (12 + oPil);
                    var merged = new byte[12 + progDesc.Length + outEntriesLen + usable.Sum(x => x.Raw.Length) + 4];
                    Array.Copy(outPmtSection, 0, merged, 0, 12);
                    Array.Copy(progDesc, 0, merged, 12, progDesc.Length);
                    Array.Copy(outPmtSection, 12 + oPil, merged, 12 + progDesc.Length, outEntriesLen);
                    var pos = 12 + progDesc.Length + outEntriesLen;
                    foreach (var x in usable) { Array.Copy(x.Raw, 0, merged, pos, x.Raw.Length); pos += x.Raw.Length; }
                    if (preserveIds)
                    {
                        merged[3] = (byte)(mainService >> 8);
                        merged[4] = (byte)mainService;
                    }
                    merged[10] = (byte)(0xF0 | ((progDesc.Length >> 8) & 0x0F));
                    merged[11] = (byte)progDesc.Length;
                    var newSectionLength = merged.Length - 3;
                    merged[1] = (byte)(0xB0 | ((newSectionLength >> 8) & 0x0F));
                    merged[2] = (byte)newSectionLength;
                    WriteSiSectionCrc(merged);
                    patchedPmtSection = merged;
                }
            }
            if (patchedPmtSection == null) dataEvents = new List<(long Target, int Pid, byte[] Packet, long Pts, long Dts)>();
        }

        // 出力の PAT も元の TS ID とサービス ID に合わせる (番組情報を元の ID のまま移植するため)。
        byte[]? patchedPatSection = null;
        if (preserveIds && outPmtPid >= 0)
        {
            var outPatSection = ReadFirstSectionBytes(outputPath, 0);
            if (outPatSection != null && outPatSection.Length >= 12)
            {
                var pLen = ((outPatSection[1] & 0x0F) << 8) | outPatSection[2];
                var pEnd = Math.Min(3 + pLen - 4, outPatSection.Length);
                var pat = (byte[])outPatSection.Clone();
                pat[3] = (byte)(sourceTsId >> 8);
                pat[4] = (byte)sourceTsId;
                for (var e = 8; e + 4 <= pEnd; e += 4)
                {
                    var entryPid = ((pat[e + 2] & 0x1F) << 8) | pat[e + 3];
                    if (entryPid == outPmtPid)
                    {
                        pat[e] = (byte)(mainService >> 8);
                        pat[e + 1] = (byte)mainService;
                    }
                }
                if (pat.Length > 3 + pLen) pat = pat.Take(3 + pLen).ToArray();
                WriteSiSectionCrc(pat);
                patchedPatSection = pat;
                WriteLog($"番組情報の ID を元の放送に合わせました (TS ID {sourceTsId} / サービス ID {mainService})。");
            }
        }

        byte[] RewriteSdt(byte[] section)
        {
            var copy = (byte[])section.Clone();
            copy[3] = (byte)(siTsId >> 8);
            copy[4] = (byte)siTsId;
            // サービスループの中から主サービスのエントリを探し、出力側の ID に合わせる
            // (SDT には全サービスのエントリが並ぶため、先頭とは限らない)。
            var sectionLength = ((copy[1] & 0x0F) << 8) | copy[2];
            var loopEnd = Math.Min(3 + sectionLength - 4, copy.Length);
            var rewritten = false;
            for (var e = 11; e + 5 <= loopEnd;)
            {
                var serviceId = (copy[e] << 8) | copy[e + 1];
                var descLen = ((copy[e + 3] & 0x0F) << 8) | copy[e + 4];
                if (serviceId == mainService)
                {
                    copy[e] = (byte)(siServiceId >> 8);
                    copy[e + 1] = (byte)siServiceId;
                    rewritten = true;
                    break;
                }
                e += 5 + descLen;
            }
            if (!rewritten && copy.Length > 12)
            {
                // 主サービスが見つからなければ、先頭サービスの ID を合わせる。
                copy[11] = (byte)(siServiceId >> 8);
                copy[12] = (byte)siServiceId;
            }
            WriteSiSectionCrc(copy);
            return copy;
        }

        byte[] RewriteEit(byte[] section)
        {
            var copy = (byte[])section.Clone();
            copy[3] = (byte)(siServiceId >> 8);
            copy[4] = (byte)siServiceId;
            copy[8] = (byte)(siTsId >> 8);
            copy[9] = (byte)siTsId;
            WriteSiSectionCrc(copy);
            return copy;
        }

        var eitSchedule = new List<byte[]>();
        foreach (var pair in latest)
        {
            if (pair.Key.Pid != SiPidEit || pair.Key.ServiceKey != mainService) continue;
            if (pair.Value.TableId >= 0x50 && pair.Value.TableId <= 0x5F) eitSchedule.Add(RewriteEit(pair.Value.Bytes));
        }

        // NIT (ネットワーク情報) は元のバイト列のまま引き継ぐ。受信機がチャンネルを
        // 特定する手がかりになるため、ID を書き換えずにそのまま使う。
        var nitSections = latest
            .Where(p => p.Key.Pid == SiPidNit && p.Value.TableId == 0x40)
            .OrderBy(p => p.Key.SectionNumber)
            .Select(p => p.Value.Bytes)
            .ToList();

        // EIT 現在/次は、切り出し範囲の中で内容が切り替わる (番組の境界) ため、
        // 時刻ごとの集合 (状態) として保持し、位置に対応させて配置する。
        var pfStates = new List<(long Pcr, List<byte[]> Sections)>();
        {
            var current = new Dictionary<byte, SiSection>();
            foreach (var section in pfTimeline)
            {
                if (section.Bytes.Length <= 4) continue;
                if (((section.Bytes[3] << 8) | section.Bytes[4]) != mainService) continue;
                if (!current.TryGetValue(section.SectionNumber, out var old) || !old.Bytes.SequenceEqual(section.Bytes))
                {
                    current[section.SectionNumber] = section;
                    pfStates.Add((ToContentTicks(section.PcrAtFirst), current.Values.Select(s => RewriteEit(s.Bytes)).ToList()));
                }
            }
        }
        List<byte[]> PfStateAt(long ticks)
        {
            var result = pfStates.Count > 0 ? pfStates[0].Sections : new List<byte[]>();
            foreach (var (pcr, sections) in pfStates)
            {
                if (pcr <= ticks) result = sections;
                else break;
            }
            return result;
        }

        if (sdtSection == null && pfStates.Count == 0 && totsInRange.Count == 0 && lastTotBefore == null && dataEvents.Count == 0 && nitSections.Count == 0)
        {
            WriteLog("番組情報: 移植できる番組情報が見つからないため、移植しませんでした。");
            return false;
        }
        var sdtBytes = sdtSection != null ? RewriteSdt(sdtSection.Bytes) : null;

        // 番組情報も引き継ぎパケットも無くなった場合は、出力に手を入れない
        // (合流処理は ffmpeg の SDT を取り除くため、何も入れないまま書き直すと情報が減ってしまう)。
        if (sdtBytes == null && pfStates.Count == 0 && totsInRange.Count == 0 && lastTotBefore == null && dataEvents.Count == 0 && nitSections.Count == 0)
        {
            WriteLog("番組情報: 移植できる情報が見つからないため、移植しませんでした。");
            return false;
        }

        // ---- 合流イベント (切り出し開始からの相対時刻 → 挿入するセクション) を組み立てる ----
        var outputLength = new FileInfo(outputPath).Length;
        var totalPackets = outputLength / TsPacketSize;
        if (totalPackets <= 0) return false;
        var spanTicks = Math.Max(1, endTicks - startTicks);
        var timeEvents = new List<(long Target, int Pid, byte[] Section)>();
        void AddTimeEvent(long target, int pid, byte[] section) => timeEvents.Add((target, pid, section));
        void AddTableSet(long target, bool includeSchedule, List<byte[]> pfSections)
        {
            if (sdtBytes != null) AddTimeEvent(target, SiPidSdt, sdtBytes);
            foreach (var section in nitSections) AddTimeEvent(target, SiPidNit, section);
            foreach (var section in pfSections) AddTimeEvent(target, SiPidEit, section);
            if (includeSchedule) foreach (var section in eitSchedule) AddTimeEvent(target, SiPidEit, section);
        }
        AddTableSet(long.MinValue, true, PfStateAt(startTicks));
        for (var target = 180000L; target < spanTicks; target += 180000L)
        {
            AddTableSet(target, false, PfStateAt(startTicks + target));
        }
        // 番組の境界 (状態が変わる時刻) にも、その時点の集合を置く。
        foreach (var (pcr, sections) in pfStates)
        {
            if (pcr <= startTicks || pcr > endTicks) continue;
            foreach (var section in sections) AddTimeEvent(pcr - startTicks, SiPidEit, section);
        }

        if (totsInRange.Count > 0)
        {
            long firstTarget = long.MaxValue;
            foreach (var tot in totsInRange)
            {
                var target = ToContentTicks(tot.PcrAtFirst) - startTicks;
                AddTimeEvent(target, SiPidTot, tot.Bytes);
                if (target < firstTarget) firstTarget = target;
            }
            // 先頭付近に TOT が無い場合は、直前の TOT の時刻を進めて先頭にも置く。
            if (lastTotBefore != null && firstTarget > spanTicks / 4)
            {
                var delta = (long)Math.Round((startTicks - ToContentTicks(lastTotBefore.PcrAtFirst)) / 90000.0);
                AddTimeEvent(0, SiPidTot, AdvanceTotSection(lastTotBefore.Bytes, delta));
            }
        }
        else if (lastTotBefore != null)
        {
            var baseDelta = (long)Math.Round((startTicks - ToContentTicks(lastTotBefore.PcrAtFirst)) / 90000.0);
            AddTimeEvent(0, SiPidTot, AdvanceTotSection(lastTotBefore.Bytes, baseDelta));
            for (var target = 2700000L; target < spanTicks; target += 2700000L)
            {
                AddTimeEvent(target, SiPidTot, AdvanceTotSection(lastTotBefore.Bytes, baseDelta + target / 90000));
            }
        }
        var sortedEvents = timeEvents.OrderBy(e => e.Target).ToList(); // 安定ソート: 同じ時刻では登録順を保つ

        // ---- 出力を書き直す (ffmpeg の SDT を除き、イベント位置で番組情報を合流) ----
        var tempPath = outputPath + ".proginfo.tmp";
        var counters = new Dictionary<int, int> { [SiPidNit] = 0, [SiPidSdt] = 0, [SiPidEit] = 0, [SiPidTot] = 0 };
        if (patchedPmtSection != null && outPmtPid >= 0) counters[outPmtPid] = 0;
        if (patchedPatSection != null) counters[0] = 0;
        var dataCarried = new Dictionary<int, int>();
        long firstOutPts = -1;
        long currentOutPts = -1;

        // 引き継ぎパケットを 1 つ書き出す。PES の時刻は、出力の映像時計に合わせて引き直す。
        void WriteDataPacket(Stream writer, (long Target, int Pid, byte[] Packet, long Pts, long Dts) e)
        {
            var packet = (byte[])e.Packet.Clone();
            if (e.Pts >= 0 && firstOutPts >= 0)
            {
                var pos = PacketPayloadStart(packet, 0);
                if (pos >= 0 && pos + 14 <= TsPacketSize
                    && packet[pos] == 0x00 && packet[pos + 1] == 0x00 && packet[pos + 2] == 0x01)
                {
                    WritePesTimestamp(packet, pos + 9, e.Pts - startTicks + firstOutPts);
                    if (e.Dts >= 0 && pos + 19 <= TsPacketSize) WritePesTimestamp(packet, pos + 14, e.Dts - startTicks + firstOutPts);
                }
            }
            var cc = counters.TryGetValue(e.Pid, out var c0) ? c0 : 0;
            packet[3] = (byte)((packet[3] & 0xF0) | (cc & 0x0F));
            counters[e.Pid] = cc + 1;
            writer.Write(packet, 0, TsPacketSize);
            dataCarried[e.Pid] = dataCarried.TryGetValue(e.Pid, out var n) ? n + 1 : 1;
        }

        void WriteSection(Stream writer, int pid, byte[] section)
        {
            var payloadLength = section.Length + 1; // 先頭のポインターフィールドを含む
            var offsetInSection = 0;
            var first = true;
            while (offsetInSection < payloadLength)
            {
                var packet = new byte[TsPacketSize];
                packet[0] = 0x47;
                packet[1] = (byte)((first ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
                packet[2] = (byte)pid;
                var cc = counters[pid];
                packet[3] = (byte)(0x10 | (cc & 0x0F));
                counters[pid] = cc + 1;
                var chunk = Math.Min(184, payloadLength - offsetInSection);
                for (var i = 0; i < 184; i++)
                {
                    var sourceIndex = offsetInSection + i;
                    byte value;
                    if (sourceIndex == 0) value = 0; // ポインターフィールド
                    else if (sourceIndex - 1 < section.Length) value = section[sourceIndex - 1];
                    else value = 0xFF;
                    packet[4 + i] = value;
                }
                offsetInSection += chunk;
                first = false;
                writer.Write(packet, 0, TsPacketSize);
            }
        }

        var outBuffer = new byte[TsPacketSize * 8192];
        using (var input = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (outVideoPid >= 0)
            {
                // 出力自身の映像 PTS を読みながら、その時刻に来たイベントから順に差し込む。
                // パケット数の割合で置くより、ビットレートの変動に左右されずに済む。
                var eventIndex = 0;
                var dataIndex = 0;
                void FireDue()
                {
                    while (true)
                    {
                        var siDue = eventIndex < sortedEvents.Count
                            && (sortedEvents[eventIndex].Target == long.MinValue
                                || (firstOutPts >= 0 && currentOutPts >= firstOutPts + sortedEvents[eventIndex].Target));
                        var dataDue = dataIndex < dataEvents.Count
                            && firstOutPts >= 0 && currentOutPts >= firstOutPts + dataEvents[dataIndex].Target;
                        if (!siDue && !dataDue) break;
                        if (dataDue && (!siDue || dataEvents[dataIndex].Target < sortedEvents[eventIndex].Target))
                        {
                            WriteDataPacket(output, dataEvents[dataIndex]);
                            dataIndex++;
                        }
                        else
                        {
                            var (target, pid, section) = sortedEvents[eventIndex];
                            WriteSection(output, pid, section);
                            eventIndex++;
                        }
                    }
                }
                FireDue(); // 先頭の表集合は、最初のパケットより前に出す
                int read;
                while ((read = input.Read(outBuffer, 0, outBuffer.Length)) > 0)
                {
                    for (var offset = 0; offset + TsPacketSize <= read; offset += TsPacketSize)
                    {
                        if (outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == outVideoPid)
                        {
                            var pts = ReadPesPtsTicks(outBuffer, offset);
                            if (pts >= 0)
                            {
                                if (firstOutPts < 0) firstOutPts = pts;
                                currentOutPts = pts;
                            }
                        }
                        FireDue();
                        if (patchedPatSection != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == 0)
                        {
                            WriteSection(output, 0, patchedPatSection); // PAT は元の TS / サービス ID に合わせたものに置き換える
                            continue;
                        }
                        if (sdtBytes != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == SiPidSdt) continue; // ffmpeg の SDT は自前のものに置き換える (自前が無い場合は ffmpeg のものを残す)
                        if (patchedPmtSection != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == outPmtPid)
                        {
                            WriteSection(output, outPmtPid, patchedPmtSection); // PMT には引き継ぎストリームの記載を加える
                            continue;
                        }
                        output.Write(outBuffer, offset, TsPacketSize);
                    }
                }
                // 末尾までに届かなかったイベントも落とさない。
                while (eventIndex < sortedEvents.Count)
                {
                    var (_, pid, section) = sortedEvents[eventIndex];
                    WriteSection(output, pid, section);
                    eventIndex++;
                }
                while (dataIndex < dataEvents.Count)
                {
                    WriteDataPacket(output, dataEvents[dataIndex]);
                    dataIndex++;
                }
            }
            else
            {
                // 映像 PID が分からない場合は、パケット数の割合で近似する。
                var events = new Dictionary<long, List<(int Pid, byte[] Section)>>();
                foreach (var (target, pid, section) in sortedEvents)
                {
                    var position = target == long.MinValue ? 0 : (long)(target / (double)spanTicks * (totalPackets - 1));
                    if (position < 0) position = 0;
                    if (position >= totalPackets) position = totalPackets - 1;
                    if (!events.TryGetValue(position, out var list)) { list = new List<(int, byte[])>(); events[position] = list; }
                    list.Add((pid, section));
                }
                var dataByPosition = new Dictionary<long, List<(long Target, int Pid, byte[] Packet, long Pts, long Dts)>>();
                foreach (var e in dataEvents)
                {
                    var position = (long)(e.Target / (double)spanTicks * (totalPackets - 1));
                    if (position < 0) position = 0;
                    if (position >= totalPackets) position = totalPackets - 1;
                    if (!dataByPosition.TryGetValue(position, out var list)) { list = new List<(long, int, byte[], long, long)>(); dataByPosition[position] = list; }
                    list.Add(e);
                }
                long packetIndex = 0;
                int read;
                while ((read = input.Read(outBuffer, 0, outBuffer.Length)) > 0)
                {
                    for (var offset = 0; offset + TsPacketSize <= read; offset += TsPacketSize, packetIndex++)
                    {
                        if (events.TryGetValue(packetIndex, out var list))
                        {
                            foreach (var (pid, section) in list) WriteSection(output, pid, section);
                        }
                        if (dataByPosition.TryGetValue(packetIndex, out var dataList))
                        {
                            foreach (var e in dataList) WriteDataPacket(output, e);
                        }
                        if (patchedPatSection != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == 0)
                        {
                            WriteSection(output, 0, patchedPatSection); // PAT は元の TS / サービス ID に合わせたものに置き換える
                            continue;
                        }
                        if (sdtBytes != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == SiPidSdt) continue; // ffmpeg の SDT は自前のものに置き換える (自前が無い場合は ffmpeg のものを残す)
                        if (patchedPmtSection != null && outBuffer[offset] == 0x47 && ReadPid(outBuffer, offset) == outPmtPid)
                        {
                            WriteSection(output, outPmtPid, patchedPmtSection);
                            continue;
                        }
                        output.Write(outBuffer, offset, TsPacketSize);
                    }
                }
            }
        }
        File.Move(tempPath, outputPath, true);
        WriteLog($"番組情報を移植しました (NIT {nitSections.Count} / SDT {(sdtBytes != null ? 1 : 0)} / EIT 現在・次 {pfStates.Count} 状態 / EIT スケジュール {eitSchedule.Count} / TOT {totsInRange.Count} 件 / 時刻ずれ補正 {(pcrToPtsOffset / 90000.0).ToString("0.00", CultureInfo.InvariantCulture)} 秒)。");
        if (dataCarried.Count > 0)
        {
            WriteLog($"字幕・データ放送などを引き継ぎました ({string.Join(" / ", dataCarried.OrderBy(p => p.Key).Select(p => $"PID 0x{p.Key:X3} {p.Value} 件"))})。");
        }
        CheckDataBroadcastCompleteness(dataEvents, dsmccPids);
        if (dataCapped)
        {
            WriteLog("引き継ぎ対象のパケットが上限を超えたため、一部のみ引き継ぎました。");
        }
        return true;
    }

    // データ放送 (DSM-CC カルーセル) の引き継ぎが一巡ぶん揃っているかを、引き継いだ
    // パケットの DII (モジュール一覧) と DDB (モジュールのブロック) から確かめる。
    // データ放送は同じデータを繰り返し送る仕組みのため、短い区間では一巡ぶんが
    // 入らないことがある。揃っていない場合だけ実行ログへ注意を出す。
    // モジュールとブロックの対応付けは PID ごとに行う (DDB のセクションヘッダーに
    // あるのはダウンロード ID ではなくモジュール ID のため)。
    private void CheckDataBroadcastCompleteness(List<(long Target, int Pid, byte[] Packet, long Pts, long Dts)> dataEvents, HashSet<int> dsmccPids)
    {
        if (dsmccPids.Count == 0) return;
        try
        {
            var modulesByPid = new Dictionary<int, Dictionary<int, (int BlockSize, long ModuleSize)>>();
            var blocksByPid = new Dictionary<int, Dictionary<int, HashSet<int>>>();
            var carriedPids = new HashSet<int>();
            foreach (var pid in dsmccPids)
            {
                var assembler = new SiSectionAssembler();
                foreach (var e in dataEvents)
                {
                    if (e.Pid != pid) continue;
                    carriedPids.Add(pid);
                    foreach (var section in assembler.Feed(e.Packet, 0, -1, pid))
                    {
                        var b = section.Bytes;
                        if (b.Length < 26) continue;
                        if (b[8] != 0x11 || b[9] != 0x03) continue; // DSM-CC のダウンロードメッセージのみ
                        var messageId = (b[10] << 8) | b[11];
                        var body = 20 + b[17]; // 適応フィールドぶんを飛ばした本体の位置
                        if (body > b.Length) continue;
                        if (section.TableId == 0x3B && messageId == 0x1002)
                        {
                            // DII: モジュール一覧を取り出す。
                            if (body + 18 > b.Length) continue;
                            var blockSize = (b[body + 4] << 8) | b[body + 5];
                            if (blockSize <= 0) continue;
                            var pos = body + 16;
                            var compatLength = (b[pos] << 8) | b[pos + 1];
                            pos += 2 + compatLength;
                            if (pos + 2 > b.Length) continue;
                            var moduleCount = (b[pos] << 8) | b[pos + 1];
                            pos += 2;
                            if (!modulesByPid.TryGetValue(pid, out var modules)) { modules = new Dictionary<int, (int, long)>(); modulesByPid[pid] = modules; }
                            for (var i = 0; i < moduleCount; i++)
                            {
                                if (pos + 8 > b.Length) break;
                                var moduleId = (b[pos] << 8) | b[pos + 1];
                                var moduleSize = ((long)b[pos + 2] << 24) | ((long)b[pos + 3] << 16) | ((long)b[pos + 4] << 8) | b[pos + 5];
                                if (!modules.ContainsKey(moduleId)) modules[moduleId] = (blockSize, moduleSize);
                                pos += 8 + b[pos + 7];
                                if (pos > b.Length) break;
                            }
                        }
                        else if (section.TableId == 0x3C && messageId == 0x1003)
                        {
                            // DDB: モジュールのブロック番号を集める。
                            if (body + 6 > b.Length) continue;
                            var moduleId = (b[body] << 8) | b[body + 1];
                            var blockNumber = (b[body + 4] << 8) | b[body + 5];
                            if (!blocksByPid.TryGetValue(pid, out var pidBlocks)) { pidBlocks = new Dictionary<int, HashSet<int>>(); blocksByPid[pid] = pidBlocks; }
                            if (!pidBlocks.TryGetValue(moduleId, out var set)) { set = new HashSet<int>(); pidBlocks[moduleId] = set; }
                            set.Add(blockNumber);
                        }
                    }
                }
            }
            if (carriedPids.Count == 0) return;
            if (modulesByPid.Count == 0)
            {
                WriteLog("注意: データ放送の引き継ぎにモジュール一覧 (DII) が含まれておらず、内容が一巡ぶん揃っていません。受信側でデータ放送を組み立てられない場合があります。");
                return;
            }
            var totalModules = 0;
            var completeModules = 0;
            var incomplete = false;
            foreach (var (pid, modules) in modulesByPid)
            {
                blocksByPid.TryGetValue(pid, out var pidBlocks);
                foreach (var (moduleId, info) in modules)
                {
                    totalModules++;
                    var expected = info.ModuleSize == 0 ? 0 : (int)((info.ModuleSize + info.BlockSize - 1) / info.BlockSize);
                    var have = 0;
                    if (pidBlocks != null && pidBlocks.TryGetValue(moduleId, out var set))
                    {
                        foreach (var n in set) if (n < expected) have++;
                    }
                    if (have >= expected) completeModules++;
                    else incomplete = true;
                }
            }
            // DII の無い PID のブロックや、DII に無いモジュールのブロックも不完全として扱う。
            foreach (var (pid, pidBlocks) in blocksByPid)
            {
                if (!modulesByPid.TryGetValue(pid, out var modules)) { incomplete = true; continue; }
                foreach (var moduleId in pidBlocks.Keys)
                {
                    if (!modules.ContainsKey(moduleId)) incomplete = true;
                }
            }
            if (incomplete)
            {
                WriteLog($"注意: データ放送の引き継ぎが一巡ぶん揃っていません (完全なモジュール {completeModules}/{totalModules})。受信側でデータ放送を組み立てられない場合があります。");
            }
        }
        catch
        {
            // 確認に失敗しても出力には影響しないため、注意は出さずに続ける。
        }
    }
}
