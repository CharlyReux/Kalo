using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kalo.Core.Scripting.Multiplayer;

/// <summary>
/// MCC map variant files (.mvar: a Forge map), made from the 'mvar' chunk the game encodes (Kalo's xinput1_4.dll command
/// kalo_write_map_variant). Layout, checked against MCC's own haloreach\hopper_map_variants\*.mvar:
/// _blf (0x30) + chdr (0x2C0, version 10.2, content type 5, the map id) + mvar (0x7028, version 31.1: SHA-1, big-endian
/// size, bit-packed data) + _eof (0x11). The bit-packed data starts with the same content header as a game variant's,
/// without the game variant's engine type and version: content type 4, file length 32, 4 ids of 64, activity 3, mode 3,
/// engine 3, map id 32, category 8, created, modified, name, description.
/// </summary>
public static class MapVariantFile
{
    public const int MvarChunkSize = 0x7028;
    private const int ChdrChunkSize = 0x2C0;
    private const int TitleChars = 128;

    /// <summary>The bit-packed data of an 'mvar' chunk (header 12, SHA-1 20, big-endian size 4, data).</summary>
    public static byte[] ChunkData(byte[] chunk)
    {
        if (chunk.Length < 0x24 || chunk[0] != 'm' || chunk[1] != 'v' || chunk[2] != 'a' || chunk[3] != 'r')
            throw new InvalidDataException("Not a map variant chunk.");
        int size = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(0x20));
        if (size <= 0 || 0x24 + size > chunk.Length) throw new InvalidDataException($"The map variant chunk has a bad size ({size}).");
        return chunk.AsSpan(0x24, size).ToArray();
    }

    /// <summary>The map id in the data's content header (bit 4 + 32 + 4×64 + 9 = 301, 32 bits).</summary>
    public static int MapId(byte[] data) => (int)ReadBits(data, 301, 32);

    /// <summary>The MCC .mvar file for the chunk, with this name and description (in the data and in chdr).</summary>
    public static byte[] ToMcc(byte[] chunk, string title, string description)
    {
        byte[] data = GameVariantFile.WithTitle(ChunkData(chunk), title, description, headerBit: 0);
        if (12 + 24 + data.Length > MvarChunkSize) throw new InvalidDataException($"The map variant is too big ({data.Length:N0} bytes).");
        int total = 0x30 + ChdrChunkSize + MvarChunkSize + 17;
        var file = new byte[total];
        var span = file.AsSpan();
        int at = 0;

        GameVariantFile.WriteHeader(span, at, "_blf", 0x30, 1, 2);
        span[at + 12] = 0xFF; span[at + 13] = 0xFE;
        at += 0x30;

        // chdr: content header (little endian), as in MCC's own map variants.
        GameVariantFile.WriteHeader(span, at, "chdr", ChdrChunkSize, 10, 2);
        var c = span.Slice(at + 12, ChdrChunkSize - 12);
        c[0] = 0xFF; c[1] = 0xFF;                                   // build
        BinaryPrimitives.WriteInt32LittleEndian(c[4..], 5);          // content type: map variant
        BinaryPrimitives.WriteInt32LittleEndian(c[8..], total);      // file length
        for (int k = 0; k < 3; k++)                                  // unique / parent / root ids, from the data
            BinaryPrimitives.WriteUInt64LittleEndian(c[(12 + 8 * k)..], (ulong)ReadBits(data, 36 + 64 * k, 64));
        c[44] = 4; c[45] = 3;                                        // as in MCC's own map variants
        BinaryPrimitives.WriteInt32LittleEndian(c[48..], MapId(data));
        c[52] = 0xFF;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteInt64LittleEndian(c[60..], now);       // created
        BinaryPrimitives.WriteInt64LittleEndian(c[96..], now);       // modified
        GameVariantFile.WriteUtf16(c.Slice(132, TitleChars * 2), title);
        GameVariantFile.WriteUtf16(c.Slice(132 + TitleChars * 2, TitleChars * 2), description);
        at += ChdrChunkSize;

        // mvar: SHA-1(size + data), size, data, zero padding (as the game writes it).
        GameVariantFile.WriteHeader(span, at, "mvar", MvarChunkSize, 31, 1);
        var m = span.Slice(at + 12, MvarChunkSize - 12);
        BinaryPrimitives.WriteInt32BigEndian(m[20..], data.Length);
        data.CopyTo(m[24..]);
        SHA1.HashData(m.Slice(20, 4 + data.Length)).CopyTo(m);
        at += MvarChunkSize;

        GameVariantFile.WriteHeader(span, at, "_eof", 17, 1, 1);
        BinaryPrimitives.WriteInt32BigEndian(span[(at + 12)..], at);
        return file;
    }

    /// <summary>True when the chunk's SHA-1 is SHA-1(size + data), the way the test build signs it.</summary>
    public static bool ChunkHashIsSha1(byte[] chunk)
    {
        int size = BinaryPrimitives.ReadInt32BigEndian(chunk.AsSpan(0x20));
        return SHA1.HashData(chunk.AsSpan(0x20, 4 + size)).AsSpan().SequenceEqual(chunk.AsSpan(0x0C, 20));
    }

    private static long ReadBits(byte[] data, int at, int count)
    {
        long v = 0;
        for (int k = 0; k < count; k++)
        {
            int i = at + k;
            v = (v << 1) | (long)((data[i >> 3] >> (7 - (i & 7))) & 1);
        }
        return v;
    }
}
