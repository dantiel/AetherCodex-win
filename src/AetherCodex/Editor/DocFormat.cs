using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace AetherCodex.Editor;

/// <summary>
/// Legacy binary Word (.doc, Word 97-2003) import. Reads the OLE2 compound
/// file, locates the WordDocument stream, parses the FIB and reconstructs the
/// text from the piece table (Clx/Pcdt). Character formatting is not preserved;
/// the result is a faithful paragraph-level FlowDocument. Import-only: WPF has
/// no writer for the OLE2 .doc binary format.
/// </summary>
internal static class DocFormat
{
    public static FlowDocument Load(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 512 || !HasCfbSignature(data))
            throw new NotSupportedException("Not a valid legacy Word (.doc) OLE2 compound file.");

        var cfb = new CfbReader(data);
        var word = cfb.ReadStream("WordDocument")
            ?? throw new NotSupportedException("WordDocument stream not found in .doc file.");

        var fWhichTblStm = word.Length > 0x0C && (ReadU16(word, 0x0A) & 0x0200) != 0;
        var table = cfb.ReadStream(fWhichTblStm ? "1Table" : "0Table") ?? Array.Empty<byte>();

        var text = ExtractText(word, table);
        return BuildDocument(text);
    }

    // ── FIB + piece table ──────────────────────────────────────────────────────

    private static string ExtractText(byte[] word, byte[] table)
    {
        if (word.Length < 0x24) return string.Empty;
        if (ReadU16(word, 0) != 0xA5EC) return string.Empty; // wIdent

        var flags = ReadU16(word, 0x0A);
        bool fExtChar = (flags & 0x0400) != 0;

        // Locate fcClx/lcbClx in the FIB using the dynamic FibRgFcLcb offset.
        var csw = ReadU16(word, 0x20);
        var p = 0x22 + 2 * csw;
        if (p + 2 > word.Length) return string.Empty;
        var cslw = ReadU16(word, p);
        p += 2 + 4 * cslw;
        if (p + 2 > word.Length) return string.Empty;
        var cbRgFcLcb = ReadU16(word, p);
        p += 2;

        const int fcClxIndex = 33; // Word 97+ FibRgFcLcb layout
        if (p + (fcClxIndex + 1) * 8 > word.Length) return string.Empty;

        var fcClx = ReadU32(word, p + fcClxIndex * 8);
        var lcbClx = ReadU32(word, p + fcClxIndex * 8 + 4);
        if (lcbClx == 0 || fcClx + lcbClx > table.Length) return string.Empty;

        // Walk the Clx: skip Prc (0x01) records, find the Pcdt (0x02).
        var clx = (int)fcClx;
        var clxEnd = (int)(fcClx + lcbClx);
        while (clx < clxEnd)
        {
            var tag = table[clx];
            if (tag == 0x01)
            {
                if (clx + 3 > clxEnd) break;
                var cbGrpprl = ReadU16(table, clx + 1);
                clx += 3 + cbGrpprl;
            }
            else if (tag == 0x02)
            {
                if (clx + 5 > clxEnd) break;
                var lcb = (int)ReadU32(table, clx + 1);
                return ExtractFromPcdt(table, clx + 5, lcb, word, fExtChar);
            }
            else break;
        }
        return string.Empty;
    }

    private static string ExtractFromPcdt(byte[] table, int pcdt, int lcb, byte[] word, bool fExtChar)
    {
        var n = (lcb - 4) / 12;
        if (n <= 0) return string.Empty;

        var cps = new int[n + 1];
        for (var i = 0; i <= n; i++) cps[i] = (int)ReadU32(table, pcdt + i * 4);

        var pcdBase = pcdt + (n + 1) * 4;
        var sb = new StringBuilder();

        for (var i = 0; i < n; i++)
        {
            var rawFc = ReadU32(table, pcdBase + i * 8);
            var compressed = (rawFc & 0x40000000) != 0;
            var fc = rawFc & 0x3FFFFFFF;
            var cch = cps[i + 1] - cps[i];
            if (cch <= 0) continue;

            var byteLen = compressed ? cch : cch * 2;
            var piece = fc < (uint)word.Length
                ? Slice(word, (int)fc, byteLen)
                : Slice(table, (int)(fc - (uint)word.Length), byteLen);

            sb.Append(DecodePiece(piece, compressed, fExtChar));
        }
        return sb.ToString();
    }

    private static string DecodePiece(byte[] piece, bool compressed, bool fExtChar)
    {
        if (piece.Length == 0) return string.Empty;
        if (!compressed) return Encoding.Unicode.GetString(piece);
        return (fExtChar ? Encoding.UTF8 : Encoding.Latin1).GetString(piece);
    }

    private static byte[] Slice(byte[] src, int offset, int len)
    {
        if (offset < 0 || offset >= src.Length || len <= 0) return Array.Empty<byte>();
        len = Math.Min(len, src.Length - offset);
        var dst = new byte[len];
        Array.Copy(src, offset, dst, 0, len);
        return dst;
    }

    // ── Control chars → FlowDocument ───────────────────────────────────────────

    private static FlowDocument BuildDocument(string text)
    {
        var doc = new FlowDocument();
        text = StripFieldCodes(text);

        foreach (var line in text.Split('\r', '\x0c')) // paragraph / page / section break
        {
            var para = new Paragraph();
            var segments = line.Split('\x0b'); // soft line break
            for (var i = 0; i < segments.Length; i++)
            {
                if (i > 0) para.Inlines.Add(new LineBreak());
                var seg = segments[i].Replace('\x07', '\t'); // cell mark
                if (seg.Length > 0) para.Inlines.Add(new Run(seg));
            }
            doc.Blocks.Add(para);
        }
        return doc;
    }

    private static string StripFieldCodes(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;
        var inResult = false;
        foreach (var c in s)
        {
            switch (c)
            {
                case '\x13': depth++; inResult = false; break; // field begin
                case '\x14': inResult = true; break;            // separator: code → result
                case '\x15': depth = Math.Max(0, depth - 1); inResult = depth > 0; break; // field end
                default:
                    if (depth == 0 || inResult) sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool HasCfbSignature(byte[] d) =>
        d.Length >= 8 && d[0] == 0xD0 && d[1] == 0xCF && d[2] == 0x11 && d[3] == 0xE0 &&
        d[4] == 0xA1 && d[5] == 0xB1 && d[6] == 0x1A && d[7] == 0xE1;

    // ── OLE2 compound file reader ──────────────────────────────────────────────

    private sealed class CfbReader
    {
        private readonly byte[] _data;
        private readonly int _sectorShift;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly uint _miniStreamCutoff;
        private readonly uint _firstDirSector;
        private readonly uint[] _fat;
        private readonly uint[] _miniFat;
        private readonly List<DirEntry> _entries = new();

        public CfbReader(byte[] data)
        {
            _data = data;
            _sectorShift = ReadU16(data, 0x1E);
            _sectorSize = 1 << _sectorShift;
            var miniShift = ReadU16(data, 0x20);
            _miniSectorSize = 1 << miniShift;
            _miniStreamCutoff = ReadU32(data, 0x38);

            var numFat = ReadU32(data, 0x2C);
            _firstDirSector = ReadU32(data, 0x30);
            var firstMiniFat = ReadU32(data, 0x3C);
            var numMiniFat = ReadU32(data, 0x40);

            _fat = BuildFat(numFat);
            _miniFat = ReadMiniFat(firstMiniFat, numMiniFat);
            ReadDirectory();
        }

        private uint[] BuildFat(uint numFat)
        {
            var fatSectors = new List<uint>();
            var difatStart = 0x4C;
            for (var i = 0; i < 109 && fatSectors.Count < numFat; i++)
            {
                var s = ReadU32(_data, difatStart + i * 4);
                if (s != 0xFFFFFFFF && s != 0xFFFFFFFE) fatSectors.Add(s);
            }

            // Follow the DIFAT chain for large files.
            var difat = ReadU32(_data, 0x44);
            var guard = 0;
            while (difat is not (0xFFFFFFFE or 0xFFFFFFFF) && guard++ < 10000)
            {
                var off = (int)((difat + 1) * _sectorSize);
                if (off + _sectorSize > _data.Length) break;
                var per = _sectorSize / 4 - 1;
                for (var i = 0; i < per && fatSectors.Count < numFat; i++)
                {
                    var s = ReadU32(_data, off + i * 4);
                    if (s is 0xFFFFFFFF or 0xFFFFFFFE) break;
                    fatSectors.Add(s);
                }
                difat = ReadU32(_data, off + per * 4);
            }

            var fat = new uint[fatSectors.Count * (_sectorSize / 4)];
            var idx = 0;
            foreach (var fs in fatSectors)
            {
                var off = (int)((fs + 1) * _sectorSize);
                for (var i = 0; i < _sectorSize / 4 && idx < fat.Length; i++, idx++)
                    fat[idx] = off + i * 4 + 4 <= _data.Length ? ReadU32(_data, off + i * 4) : 0xFFFFFFFF;
            }
            return fat;
        }

        private uint[] ReadMiniFat(uint first, uint numSectors)
        {
            var stream = ReadChain(first, numSectors * (uint)_sectorSize);
            var fat = new uint[stream.Length / 4];
            for (var i = 0; i < fat.Length; i++)
                fat[i] = ReadU32(stream, i * 4);
            return fat;
        }

        private void ReadDirectory()
        {
            var dir = ReadChain(_firstDirSector, uint.MaxValue);
            for (var i = 0; i + 128 <= dir.Length; i += 128)
            {
                var nameLen = ReadU16(dir, i + 0x40);
                if (nameLen == 0 || nameLen > 64) continue;
                var name = Encoding.Unicode.GetString(dir, i, Math.Max(0, nameLen - 2));
                var objType = dir[i + 0x42];
                var start = ReadU32(dir, i + 0x74);
                var size = (long)ReadU64(dir, i + 0x78);
                if (objType is 1 or 2 or 5) // storage / stream / root
                    _entries.Add(new DirEntry(name, start, size));
            }
        }

        public byte[]? ReadStream(string name)
        {
            var e = _entries.FirstOrDefault(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (e is null) return null;

            if (e.Size >= _miniStreamCutoff)
                return ReadChain(e.Start, (uint)e.Size);

            // Small streams live in the root entry's mini stream.
            var root = _entries.FirstOrDefault(x => x.Start == _firstDirSector) ?? _entries.FirstOrDefault();
            var mini = ReadChain(root?.Start ?? 0, root is null ? 0 : (uint)root.Size);
            return ReadMiniChain(e.Start, (int)e.Size, mini);
        }

        private byte[] ReadChain(uint start, uint size)
        {
            var cap = size == uint.MaxValue ? 0 : (int)size;
            using var ms = new MemoryStream(cap);
            var s = start;
            var guard = 0;
            while (s is not (0xFFFFFFFE or 0xFFFFFFFF) && guard++ < 100000)
            {
                var off = (int)((s + 1) * _sectorSize);
                var remaining = size == uint.MaxValue ? _sectorSize : (int)size - (int)ms.Length;
                if (remaining <= 0) break;
                var n = Math.Min(_sectorSize, remaining);
                if (off < _data.Length)
                {
                    var m = Math.Min(n, _data.Length - off);
                    ms.Write(_data, off, m);
                }
                if (s >= _fat.Length) break;
                s = _fat[s];
            }
            return ms.ToArray();
        }

        private byte[] ReadMiniChain(uint start, int size, byte[] mini)
        {
            var buf = new byte[size];
            var written = 0;
            var s = start;
            var guard = 0;
            while (s is not (0xFFFFFFFE or 0xFFFFFFFF) && written < size && guard++ < 100000)
            {
                var off = (int)(s * _miniSectorSize);
                var n = Math.Min(_miniSectorSize, size - written);
                if (off < mini.Length)
                {
                    var m = Math.Min(n, mini.Length - off);
                    Array.Copy(mini, off, buf, written, m);
                }
                written += n;
                if (s >= _miniFat.Length) break;
                s = _miniFat[s];
            }
            return buf;
        }

        private sealed record DirEntry(string Name, uint Start, long Size);
    }

    private static ushort ReadU16(byte[] d, int off) =>
        (ushort)(d[off] | (d[off + 1] << 8));

    private static uint ReadU32(byte[] d, int off) =>
        (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));

    private static ulong ReadU64(byte[] d, int off) =>
        ReadU32(d, off) | ((ulong)ReadU32(d, off + 4) << 32);
}