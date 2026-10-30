using System.IO.Compression;
using System.Text;

namespace Kalo.Core.Audio;

/// <summary>
/// Turns a Vorbis sound of an FSB5 bank back into a normal Ogg Vorbis file. FMOD keeps only the audio packets; the
/// three Vorbis headers are rebuilt: the id and comment headers from the sound's rate and channels, the setup
/// header from a table keyed by the CRC32 FMOD stores (generated once with libvorbis, like python-fsb5 does).
/// </summary>
public static class FsbVorbis
{
    public sealed record Setup(uint Crc, int Channels, int Rate, int BlockShort, int BlockLong, byte[] ModeLong, byte[] Packet);

    private static Dictionary<uint, Setup>? s_setups;

    /// <summary>The setup headers (FsbVorbisSetupData).</summary>
    public static IReadOnlyDictionary<uint, Setup> Setups => s_setups ??= LoadSetups();

    private static Dictionary<uint, Setup> LoadSetups()
    {
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(FsbVorbisSetupData.Gzip)), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        ms.Position = 0;
        return ReadSetups(ms);
    }

    public static Dictionary<uint, Setup> ReadSetups(Stream s)
    {
        var r = new BinaryReader(s);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "KVS1") throw new InvalidDataException("Bad Vorbis setup table.");
        int count = r.ReadInt32();
        var map = new Dictionary<uint, Setup>();
        for (int i = 0; i < count; i++)
        {
            uint crc = r.ReadUInt32();
            int ch = r.ReadInt32(), rate = r.ReadInt32(), bs = r.ReadInt32(), bl = r.ReadInt32();
            int modes = r.ReadByte();
            byte[] flags = r.ReadBytes(modes);
            byte[] packet = r.ReadBytes(r.ReadInt32());
            map[crc] = new Setup(crc, ch, rate, bs, bl, flags, packet);
        }
        return map;
    }

    /// <summary>The Ogg Vorbis file of one sound of the bank.</summary>
    public static byte[] ToOgg(Stream bank, Fsb5.Sample sample)
    {
        var vorbisData = sample.Chunks.FirstOrDefault(c => c.Type == 11);
        if (vorbisData.Size < 4) throw new InvalidDataException("This sound has no Vorbis data.");
        bank.Position = vorbisData.Offset;
        var r = new BinaryReader(bank);
        uint crc = r.ReadUInt32();
        if (!Setups.TryGetValue(crc, out var setup)) throw new InvalidDataException($"Unknown Vorbis setup (crc {crc}).");
        bank.Position = sample.DataOffset;
        byte[] data = r.ReadBytes((int)Math.Min(sample.DataSize, int.MaxValue));
        return BuildOgg(sample.Channels, sample.Frequency, setup, Packets(data), sample.SampleCount);
    }

    /// <summary>FMOD's packet stream: [uint16 size][packet] ... until a size of 0 or the end.</summary>
    public static IEnumerable<byte[]> Packets(byte[] data)
    {
        int o = 0;
        while (o + 2 <= data.Length)
        {
            int size = data[o] | data[o + 1] << 8;
            o += 2;
            if (size == 0 || o + size > data.Length) yield break;
            yield return data[o..(o + size)];
            o += size;
        }
    }

    public static byte[] BuildOgg(int channels, int rate, Setup setup, IEnumerable<byte[]> packets, long totalSamples)
    {
        var ogg = new OggWriter(0x4B414C4F); // "KALO"
        ogg.Packet(IdHeader(channels, rate, setup), 0, bos: true, flush: true);
        ogg.Packet(CommentHeader(), 0);
        ogg.Packet(setup.Packet, 0, flush: true);

        int modeBits = 0;
        while ((1 << modeBits) < setup.ModeLong.Length) modeBits++;
        var list = packets.ToList();
        long granule = 0;
        int previous = 0;
        for (int i = 0; i < list.Count; i++)
        {
            byte[] p = list[i];
            int block = setup.BlockShort;
            if (p.Length > 0 && (p[0] & 1) == 0 && setup.ModeLong.Length > 0)
            {
                int mode = (p[0] >> 1) & ((1 << modeBits) - 1);
                if (mode < setup.ModeLong.Length && setup.ModeLong[mode] != 0) block = setup.BlockLong;
            }
            granule = previous == 0 ? 0 : granule + (block + previous) / 4;
            previous = block;
            bool last = i == list.Count - 1;
            // The last page's granule position trims the decoded sound to its real length.
            long g = last && totalSamples > 0 ? Math.Min(totalSamples, granule) : granule;
            ogg.Packet(p, g, eos: last, flush: g > 0 || last); // the first audio packet (granule 0) shares the next page
        }
        return ogg.ToArray();
    }

    private static byte[] IdHeader(int channels, int rate, Setup setup)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)1);
        w.Write(Encoding.ASCII.GetBytes("vorbis"));
        w.Write(0); // version
        w.Write((byte)channels);
        w.Write(rate);
        w.Write(0); w.Write(0); w.Write(0); // bitrates
        w.Write((byte)(Log2(setup.BlockShort) | Log2(setup.BlockLong) << 4));
        w.Write((byte)1); // framing
        return ms.ToArray();
    }

    private static byte[] CommentHeader()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)3);
        w.Write(Encoding.ASCII.GetBytes("vorbis"));
        byte[] vendor = Encoding.ASCII.GetBytes("Kalo");
        w.Write(vendor.Length);
        w.Write(vendor);
        w.Write(0); // no comments
        w.Write((byte)1); // framing
        return ms.ToArray();
    }

    private static int Log2(int v)
    {
        int n = 0;
        while ((1 << n) < v) n++;
        return n;
    }

    /// <summary>Minimal Ogg page writer.</summary>
    private sealed class OggWriter(int serial)
    {
        private readonly MemoryStream _out = new();
        private readonly List<byte> _body = new();
        private readonly List<byte> _lacing = new();
        private int _sequence;
        private bool _bos;
        private bool _continued;
        private long _granule = -1;

        public void Packet(byte[] packet, long granule, bool bos = false, bool eos = false, bool flush = false)
        {
            if (bos) _bos = true;
            int o = 0;
            while (true)
            {
                int seg = Math.Min(255, packet.Length - o);
                if (_lacing.Count == 255)
                {
                    // Page full in the middle of a packet: it continues on the next page (granule -1 = no packet ends here).
                    _granule = -1;
                    Page(false);
                    _continued = true;
                }
                _lacing.Add((byte)seg);
                _body.AddRange(new ArraySegment<byte>(packet, o, seg));
                o += seg;
                if (seg < 255) break;
            }
            _granule = granule;
            if (flush || eos) Page(eos);
        }

        private void Page(bool eos)
        {
            if (_lacing.Count == 0) return;
            var h = new byte[27 + _lacing.Count];
            Encoding.ASCII.GetBytes("OggS").CopyTo(h, 0);
            h[4] = 0;
            h[5] = (byte)((_continued ? 1 : 0) | (_bos ? 2 : 0) | (eos ? 4 : 0));
            BitConverter.GetBytes(_granule).CopyTo(h, 6);
            BitConverter.GetBytes(serial).CopyTo(h, 14);
            BitConverter.GetBytes(_sequence++).CopyTo(h, 18);
            h[26] = (byte)_lacing.Count;
            _lacing.CopyTo(h, 27);
            byte[] body = _body.ToArray();
            uint crc = Crc(0, h);
            crc = Crc(crc, body);
            BitConverter.GetBytes(crc).CopyTo(h, 22);
            _out.Write(h);
            _out.Write(body);
            _lacing.Clear();
            _body.Clear();
            _bos = false;
            _continued = false;
        }

        public byte[] ToArray() => _out.ToArray();

        private static readonly uint[] Table = MakeTable();

        private static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                t[i] = r;
            }
            return t;
        }

        private static uint Crc(uint crc, byte[] data)
        {
            foreach (byte b in data) crc = (crc << 8) ^ Table[((crc >> 24) ^ b) & 0xFF];
            return crc;
        }
    }
}
