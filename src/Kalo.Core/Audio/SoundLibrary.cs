using System.Runtime.InteropServices;
using System.Text;

namespace Kalo.Core.Audio;

/// <summary>
/// Plays Reach's sounds for previews: finds a sound tag's audio in the game's FMOD banks (MCC haloreach\fmod\pc:
/// english.fsb for voices, sfx.fsb for effects; HREK\fmod\pc has sfx.fsb too), decodes it to a .wav in Kalo's cache
/// and plays it. Read only: the banks are never changed.
/// </summary>
public sealed class SoundLibrary
{
    private sealed class Bank
    {
        public required string Fsb { get; init; }
        public required string Tag { get; init; }
        public List<string> Paths { get; } = new();
        public Fsb5? Toc;
    }

    private readonly List<Bank> _banks = new();
    private readonly string _cache;
    private readonly object _lock = new();

    /// <param name="folders">Folders holding .fsb + .fsb.info pairs, searched in this order.</param>
    /// <param name="cache">Where decoded .wav files are kept.</param>
    public SoundLibrary(IEnumerable<string> folders, string cache)
    {
        _cache = cache;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders.Where(Directory.Exists))
            foreach (string name in new[] { "english.fsb", "sfx.fsb" })
            {
                string fsb = Path.Combine(folder, name);
                if (!File.Exists(fsb) || !File.Exists(fsb + ".info") || !seen.Add(name)) continue;
                _banks.Add(new Bank { Fsb = fsb, Tag = Path.GetFileNameWithoutExtension(name) });
            }
    }

    public bool HasBanks => _banks.Count > 0;
    public string Banks => string.Join(", ", _banks.Select(b => b.Fsb));

    /// <summary>The paths of a bank's sounds, in bank order (.info: 280-byte records, the source file path at +24).</summary>
    private static void LoadInfo(Bank bank)
    {
        if (bank.Paths.Count > 0) return;
        byte[] d = File.ReadAllBytes(bank.Fsb + ".info");
        for (int o = 0; o + 280 <= d.Length; o += 280)
        {
            int end = Array.IndexOf(d, (byte)0, o + 24, 256);
            bank.Paths.Add(Encoding.ASCII.GetString(d, o + 24, (end < 0 ? o + 280 : end) - (o + 24)));
        }
    }

    /// <summary>
    /// Where a sound tag's audio is: "sound\dialog\multiplayer\ctf\flag_captured.sound" → the bank sound made from
    /// data\sound\dialog\multiplayer\ctf\flag_captured.aif (or its first permutation).
    /// </summary>
    public (string Bank, int Index, string Source)? Find(string soundTag)
    {
        string stem = "data\\" + Path.ChangeExtension(soundTag.Replace('/', '\\').Trim(), null);
        lock (_lock)
        {
            foreach (var bank in _banks)
            {
                LoadInfo(bank);
                int best = -1, rank = int.MaxValue;
                for (int i = 0; i < bank.Paths.Count; i++)
                {
                    string p = bank.Paths[i];
                    if (!p.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = p[stem.Length..];
                    int r = rest.StartsWith('.') ? 0 : rest.StartsWith('\\') ? 1 : 2; // exact file, its folder, a numbered take
                    if (r < rank) { rank = r; best = i; }
                }
                if (best >= 0) return (bank.Tag, best, bank.Paths[best]);
            }
        }
        return null;
    }

    /// <summary>The sound decoded to a .wav file in the cache (made once).</summary>
    public string Wav(string bankTag, int index)
    {
        var bank = _banks.First(b => b.Tag == bankTag);
        string file = Path.Combine(_cache, $"{bank.Tag}_{index}_v2.wav"); // v2: data offsets read as python-fsb5 does
        if (File.Exists(file)) return file;
        byte[] ogg;
        lock (_lock)
        {
            using var stream = File.OpenRead(bank.Fsb);
            bank.Toc ??= Fsb5.Read(stream);
            ogg = FsbVorbis.ToOgg(stream, bank.Toc.Samples[index]);
        }
        Directory.CreateDirectory(_cache);
        File.WriteAllBytes(file + ".tmp", OggToWav(ogg));
        File.Move(file + ".tmp", file, overwrite: true);
        return file;
    }

    /// <summary>Decodes Ogg Vorbis to a 16-bit PCM .wav.</summary>
    public static byte[] OggToWav(byte[] ogg)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        int channels = reader.Channels, rate = reader.SampleRate;
        var pcm = new MemoryStream();
        var buffer = new float[rate * channels];
        int n;
        while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp((int)Math.Round(buffer[i] * 32767f), short.MinValue, short.MaxValue);
                pcm.WriteByte((byte)s);
                pcm.WriteByte((byte)(s >> 8));
            }
        var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((int)(36 + pcm.Length));
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((int)pcm.Length);
        pcm.Position = 0;
        pcm.CopyTo(wav);
        return wav.ToArray();
    }

    // ---- playback (Windows) ----

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndFilename = 0x00020000;

    /// <summary>Plays a .wav (stops the one playing). Windows only; does nothing elsewhere.</summary>
    public static void Play(string wav)
    {
        if (OperatingSystem.IsWindows()) PlaySound(wav, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault);
    }

    public static void Stop()
    {
        if (OperatingSystem.IsWindows()) PlaySound(null, IntPtr.Zero, 0);
    }
}
