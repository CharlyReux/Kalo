using System.Text;

namespace Kalo.Core.Audio;

/// <summary>
/// Reads the table of contents of an FMOD FSB5 sound bank (MCC's haloreach\fmod\pc\*.fsb): how many sounds, their
/// codec, rate, channels, sample count, where their data is, and their names when the bank has them.
/// </summary>
public sealed class Fsb5
{
    public sealed record Sample(int Index, string? Name, int Frequency, int Channels, long DataOffset, long DataSize, long SampleCount, List<(int Type, long Offset, int Size)> Chunks);

    public int Version { get; private init; }
    public int Mode { get; private init; }
    public int HeaderSize { get; private init; }
    public long DataStart { get; private init; }
    public List<Sample> Samples { get; } = new();

    public string Codec => Mode switch
    {
        1 => "PCM8", 2 => "PCM16", 3 => "PCM24", 4 => "PCM32", 5 => "PCMFLOAT", 6 => "GCADPCM", 7 => "IMAADPCM", 8 => "VAG",
        9 => "HEVAG", 10 => "XMA", 11 => "MPEG", 12 => "CELT", 13 => "AT9", 14 => "XWMA", 15 => "VORBIS", 16 => "FADPCM", 17 => "OPUS",
        _ => "unknown " + Mode,
    };

    private static long Offset(ulong bits) => (long)((bits >> 6) & 0xFFFFFFF) * 16;

    private static readonly int[] Rates = { 4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    public static Fsb5 Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static Fsb5 Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        long start = stream.Position;
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "FSB5") throw new InvalidDataException("Not an FSB5 sound bank.");
        int version = r.ReadInt32();
        int count = r.ReadInt32();
        int headersSize = r.ReadInt32();
        int namesSize = r.ReadInt32();
        int dataSize = r.ReadInt32();
        int mode = r.ReadInt32();
        int headerSize = version == 0 ? 0x40 : 0x3C;
        var fsb = new Fsb5 { Version = version, Mode = mode, HeaderSize = headerSize, DataStart = start + headerSize + headersSize + namesSize };

        stream.Position = start + headerSize;
        var raw = new List<(ulong Bits, List<(int, long, int)> Chunks)>();
        for (int i = 0; i < count; i++)
        {
            ulong bits = r.ReadUInt64();
            var chunks = new List<(int, long, int)>();
            bool more = (bits & 1) != 0;
            while (more)
            {
                uint c = r.ReadUInt32();
                more = (c & 1) != 0;
                int size = (int)((c >> 1) & 0xFFFFFF);
                int type = (int)((c >> 25) & 0x7F);
                chunks.Add((type, stream.Position, size));
                stream.Position += size;
            }
            raw.Add((bits, chunks));
        }

        var names = new string?[count];
        if (namesSize > 0)
        {
            long namesAt = start + headerSize + headersSize;
            stream.Position = namesAt;
            var offsets = Enumerable.Range(0, count).Select(_ => r.ReadInt32()).ToArray();
            for (int i = 0; i < count; i++)
            {
                stream.Position = namesAt + offsets[i];
                var sb = new StringBuilder();
                for (int b = stream.ReadByte(); b > 0; b = stream.ReadByte()) sb.Append((char)b);
                names[i] = sb.ToString();
            }
        }

        for (int i = 0; i < count; i++)
        {
            ulong bits = raw[i].Bits;
            int freqIndex = (int)((bits >> 1) & 0xF);
            // Layout as python-fsb5 reads it (matches MCC's banks: mono voices, 16-byte aligned data):
            // 1 bit next chunk, 4 bits rate, 1 bit stereo, 28 bits data offset / 16, 30 bits sample count.
            int channels = (int)((bits >> 5) & 1) + 1;
            long offset = Offset(bits);
            long samples = (long)((bits >> 34) & 0x3FFFFFFF);
            long next = i + 1 < count ? Offset(raw[i + 1].Bits) : dataSize;
            int freq = freqIndex < Rates.Length ? Rates[freqIndex] : 44100;
            foreach (var (type, at, size) in raw[i].Chunks)
            {
                stream.Position = at;
                if (type == 1 && size >= 4) freq = r.ReadInt32();          // frequency
                else if (type == 2 && size >= 1) channels = r.ReadByte();   // channels
            }
            fsb.Samples.Add(new Sample(i, names[i], freq, channels, fsb.DataStart + offset, next - offset, samples, raw[i].Chunks));
        }
        return fsb;
    }
}
