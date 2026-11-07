namespace Kalo.Core.Scripting;

/// <summary>
/// Code Kalo writes into a script for you (zone actions in a gametype, volume actions and patrols in a mission script):
/// a block that starts with a comment line holding its mark and ends at the first blank line. Writing it again replaces
/// it; everything else in the script stays as written.
/// </summary>
public static class WrittenBlocks
{
    /// <summary>The script with <paramref name="code"/> at its end, replacing the block that has the same mark.</summary>
    public static string Put(string script, string mark, string code)
    {
        string nl = script.Contains("\r\n") ? "\r\n" : "\n";
        string text = Remove(script.Replace("\r\n", "\n"), mark).TrimEnd('\n');
        return (text + "\n\n" + code.Replace("\r\n", "\n").TrimEnd('\n') + "\n").Replace("\n", nl);
    }

    /// <summary>The script without the block whose first (comment) line contains <paramref name="mark"/>.</summary>
    public static string Remove(string script, string mark)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n').ToList();
        int start = lines.FindIndex(l => IsComment(l) && l.Contains(mark, StringComparison.Ordinal));
        if (start < 0) return script;
        int end = start + 1;
        while (end < lines.Count && lines[end].Trim().Length > 0) end++;
        lines.RemoveRange(start, end - start);
        // Don't leave two blank lines where the block was.
        if (start < lines.Count && start > 0 && lines[start].Trim().Length == 0 && lines[start - 1].Trim().Length == 0) lines.RemoveAt(start);
        return string.Join("\n", lines);
    }

    private static bool IsComment(string line)
    {
        string t = line.TrimStart();
        return t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith('#') || t.StartsWith(';');
    }
}
