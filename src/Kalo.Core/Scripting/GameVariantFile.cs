using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kalo.Core.Scripting;

/// <summary>
/// Halo: Reach game variant files (.bin, "BLF" chunks). HREK's Megalo compiler serializes a variant as
/// _blf + athr + gvar + _eof; MCC's custom games use _blf + chdr (content header) + mpvr + _eof, where mpvr
/// holds a SHA-1 of (size + data), the size, then the same bit-packed variant data the gvar chunk carries.
/// Kalo rewraps the compiler's variant that way so MCC can list it as a custom game type.
/// (MCC's own built-in variants also carry a signed _fsm chunk; files saved by players do not.)
/// </summary>
public static class GameVariantFile
{
    /// <summary>Size of MCC's mpvr chunk (header included); the data is padded with zeros up to it.</summary>
    public const int MpvrChunkSize = 0x5028;
    private const int ChdrChunkSize = 0x2C0;
    private const int TitleChars = 128;

    public sealed record Chunk(string Tag, int Offset, int Size, ushort Major, ushort Minor)
    {
        public int DataOffset => Offset + 12;
        public int DataSize => Size - 12;
    }

    /// <summary>The chunks of a BLF file (stops at _eof or at anything that isn't a chunk).</summary>
    public static List<Chunk> Chunks(byte[] file)
    {
        var list = new List<Chunk>();
        int i = 0;
        while (i + 12 <= file.Length)
        {
            string tag = Encoding.ASCII.GetString(file, i, 4);
            int size = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(i + 4));
            if (size < 12 || i + size > file.Length || !tag.All(c => c >= 32 && c < 127)) break;
            list.Add(new Chunk(tag, i, size, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(i + 8)), BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(i + 10))));
            i += size;
            if (tag == "_eof") break;
        }
        return list;
    }

    /// <summary>The bit-packed variant data from a compiler file (gvar) or an MCC file (mpvr).</summary>
    public static byte[] VariantData(byte[] file)
    {
        var chunks = Chunks(file);
        var gvar = chunks.FirstOrDefault(c => c.Tag == "gvar");
        if (gvar != null) return file.AsSpan(gvar.DataOffset, gvar.DataSize).ToArray();
        var mpvr = chunks.FirstOrDefault(c => c.Tag == "mpvr") ?? throw new InvalidDataException("No gvar or mpvr chunk: not a game variant.");
        int size = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(mpvr.DataOffset + 24));
        return file.AsSpan(mpvr.DataOffset + 28, size).ToArray();
    }

    /// <summary>True when an MCC file's mpvr hash matches its data.</summary>
    public static bool HashIsValid(byte[] file)
    {
        var mpvr = Chunks(file).FirstOrDefault(c => c.Tag == "mpvr");
        if (mpvr == null) return false;
        int size = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(mpvr.DataOffset + 24));
        byte[] hash = SHA1.HashData(file.AsSpan(mpvr.DataOffset + 24, 4 + size));
        return file.AsSpan(mpvr.DataOffset, 20).SequenceEqual(hash);
    }

    /// <summary>
    /// Builds an MCC custom game type file from the compiler's .bin: content header with the given title and
    /// description, mpvr with the variant data and its hash, end of file.
    /// </summary>
    public static byte[] ToMcc(byte[] compilerBin, string title, string description)
    {
        byte[] data = WithTitle(VariantData(compilerBin), title, description);
        if (data.Length + 12 + 28 > MpvrChunkSize) throw new InvalidDataException($"The variant is too big for a game type file ({data.Length:N0} bytes).");
        int total = 0x30 + ChdrChunkSize + MpvrChunkSize + 17;
        var file = new byte[total];
        var span = file.AsSpan();
        int at = 0;

        // _blf: header chunk, byte order mark FFFE.
        WriteHeader(span, at, "_blf", 0x30, 1, 2);
        span[at + 12] = 0xFF; span[at + 13] = 0xFE;
        at += 0x30;

        // chdr: content header (little endian), mirroring the one inside the variant data.
        WriteHeader(span, at, "chdr", ChdrChunkSize, 10, 2);
        var c = span.Slice(at + 12, ChdrChunkSize - 12);
        c[0] = 0xFF; c[1] = 0xFF;                                   // build
        BinaryPrimitives.WriteInt32LittleEndian(c[4..], 6);          // content type: game variant
        BinaryPrimitives.WriteInt32LittleEndian(c[8..], total);      // file length
        var ids = UniqueIds(data);                                   // unique / parent / root ids
        for (int k = 0; k < 3; k++) BinaryPrimitives.WriteUInt64LittleEndian(c[(12 + 8 * k)..], ids);
        c[44] = 3; c[45] = 3; c[46] = 2;                             // as in MCC's own variant files
        BinaryPrimitives.WriteInt32LittleEndian(c[48..], -1);        // map id: any
        BinaryPrimitives.WriteInt32LittleEndian(c[52..], 1);         // category icon
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt64LittleEndian(c[60..], now);       // created
        BinaryPrimitives.WriteInt64LittleEndian(c[96..], now);       // modified
        WriteUtf16(c.Slice(132, TitleChars * 2), title);
        WriteUtf16(c.Slice(132 + TitleChars * 2, TitleChars * 2), description);
        at += ChdrChunkSize;

        // mpvr: SHA-1(size + data), 4 zero bytes, size, data, zero padding.
        WriteHeader(span, at, "mpvr", MpvrChunkSize, 0x36, 1);
        var m = span.Slice(at + 12, MpvrChunkSize - 12);
        BinaryPrimitives.WriteInt32BigEndian(m[24..], data.Length);
        data.CopyTo(m[28..]);
        SHA1.HashData(m.Slice(24, 4 + data.Length)).CopyTo(m);
        at += MpvrChunkSize;

        // _eof: offset of this chunk, no signature.
        WriteHeader(span, at, "_eof", 17, 1, 1);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 12)..], at);
        return file;
    }

    /// <summary>
    /// The variant data with its own name and description set. MCC's game type menus show the name stored in the
    /// variant's bit-packed content header, not the file's chdr; the compiler leaves it empty, which MCC shows as
    /// "$HR_GVAR_". Header layout (bits, big endian), checked against a game type MCC saved: engine type 4, encoding
    /// version 32, -1 32, then content type 4, file length 32, 4 ids of 64, activity 3, game mode 3, engine 3,
    /// map id 32, category 8, created and modified (time 64, xuid 64, author: bytes up to 16, 0-terminated, online 1),
    /// then name and description: UTF-16 characters of 16 bits, 0-terminated (at most 128 each).
    /// Returns the data unchanged if the header doesn't read that way.
    /// </summary>
    public static byte[] WithTitle(byte[] data, string title, string description)
    {
        int total = data.Length * 8;
        int Bit(int i) => (data[i >> 3] >> (7 - (i & 7))) & 1;
        long Read(ref int i, int n)
        {
            long v = 0;
            for (int k = 0; k < n; k++) v = (v << 1) | (long)Bit(i++);
            return v;
        }
        try
        {
            int i = 4 + 32 + 32;
            i += 4 + 32 + 4 * 64 + 3 + 3 + 3 + 32 + 8;
            for (int who = 0; who < 2; who++)
            {
                i += 64 + 64;
                for (int c = 0; c < 16 && Read(ref i, 8) != 0; c++) { }
                i += 1;
            }
            int titleAt = i;
            for (int s = 0; s < 2; s++)
            {
                int c = 0;
                while (Read(ref i, 16) != 0) if (++c >= 128) return data;
            }
            int end = i;
            if (end > total) return data;

            var bits = new List<bool>(total + 4096);
            for (int k = 0; k < titleAt; k++) bits.Add(Bit(k) == 1);
            foreach (string text in new[] { title, description })
            {
                string t = text.Length > 127 ? text[..127] : text;
                foreach (char ch in t + "\0")
                    for (int b = 15; b >= 0; b--) bits.Add(((ch >> b) & 1) == 1);
            }
            for (int k = end; k < total; k++) bits.Add(Bit(k) == 1);
            var result = new byte[(bits.Count + 7) / 8];
            for (int k = 0; k < bits.Count; k++)
                if (bits[k]) result[k >> 3] |= (byte)(0x80 >> (k & 7));
            return result;
        }
        catch (IndexOutOfRangeException)
        {
            return data;
        }
    }

    /// <summary>The variant's unique id: 8 bytes at offset 13 of the bit-packed data (big endian there).</summary>
    private static ulong UniqueIds(byte[] data) => data.Length >= 21 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(13)) : 0;

    private static void WriteHeader(Span<byte> s, int at, string tag, int size, ushort major, ushort minor)
    {
        Encoding.ASCII.GetBytes(tag).CopyTo(s[at..]);
        BinaryPrimitives.WriteInt32BigEndian(s[(at + 4)..], size);
        BinaryPrimitives.WriteUInt16BigEndian(s[(at + 8)..], major);
        BinaryPrimitives.WriteUInt16BigEndian(s[(at + 10)..], minor);
    }

    private static void WriteUtf16(Span<byte> into, string text)
    {
        into.Clear();
        byte[] b = Encoding.Unicode.GetBytes(text.Length > TitleChars - 1 ? text[..(TitleChars - 1)] : text);
        b.AsSpan(0, Math.Min(b.Length, into.Length - 2)).CopyTo(into);
    }
}
