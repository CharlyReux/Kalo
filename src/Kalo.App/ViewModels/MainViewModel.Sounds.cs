using Kalo.Core.Audio;

namespace Kalo.App.ViewModels;

/// <summary>Sound previews: hear a gametype sound (flag_captured, slayer...) from the code editor.</summary>
public partial class MainViewModel
{
    private SoundLibrary? _sounds;
    private Dictionary<string, string>? _megaloSounds;
    private readonly SemaphoreSlim _soundGate = new(1, 1);

    /// <summary>The game's sound banks: MCC (voices in your language, effects), then HREK (effects).</summary>
    public SoundLibrary? Sounds => _sounds ??= Paths == null ? null : new SoundLibrary(new[]
    {
        System.IO.Path.Combine(Paths.Mcc ?? "", "haloreach", "fmod", "pc"),
        System.IO.Path.Combine(Paths.Hrek, "fmod", "pc"),
    }, System.IO.Path.Combine(Paths.KaloData, "cache", "sounds"));

    /// <summary>The sound tag a Megalo sound name plays (multiplayer\megalo\english.megalogamengine_sounds), or null.</summary>
    public async Task<string?> MegaloSoundTagAsync(string name)
    {
        if (_megaloSounds == null && Host != null)
        {
            const string tag = @"multiplayer\megalo\english.megalogamengine_sounds";
            var doc = await Host.OpenTagAsync(tag);
            _megaloSounds = (doc.Fields ?? new()).Where(f => !string.IsNullOrEmpty(f.Name) && !string.IsNullOrEmpty(f.Value))
                .GroupBy(f => f.Name!).ToDictionary(g => g.Key, g => g.First().Value!, StringComparer.Ordinal);
            try { await Host.CloseTagAsync(tag); } catch { /* still open elsewhere */ }
        }
        return _megaloSounds != null && _megaloSounds.TryGetValue(name, out var t) ? t : null;
    }

    /// <summary>The .wav of a Megalo sound (decoded once, then cached), or null with the reason.</summary>
    public async Task<(string? Wav, string Message)> PrepareMegaloSoundAsync(string name)
    {
        await _soundGate.WaitAsync();
        try
        {
            var library = Sounds;
            if (library == null || !library.HasBanks) return (null, "No sound banks found (MCC haloreach\\fmod\\pc or HREK\\fmod\\pc).");
            string? tag = await MegaloSoundTagAsync(name);
            if (tag == null) return (null, $"'{name}' is not a gametype sound.");
            var found = await Task.Run(() => library.Find(tag));
            if (found == null) return (null, $"No audio found for {tag}.");
            string wav = await Task.Run(() => library.Wav(found.Value.Bank, found.Value.Index));
            return (wav, $"{name}: {tag}");
        }
        catch (Exception ex)
        {
            return (null, $"Could not play {name}: {ex.Message}");
        }
        finally
        {
            _soundGate.Release();
        }
    }

    /// <summary>Plays a Megalo sound (stops the one playing). Returns what happened, for the status line.</summary>
    public async Task<string> PlayMegaloSoundAsync(string name)
    {
        var (wav, message) = await PrepareMegaloSoundAsync(name);
        if (wav != null) SoundLibrary.Play(wav);
        else Log(message);
        return (wav != null ? "▶ " : "") + message;
    }

    public bool IsMegaloSound(string word) => MegaloCatalog?.Lists != null && MegaloCatalog.Lists.TryGetValue("sounds", out var l) && l.Contains(word);
}
