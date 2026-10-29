namespace Kalo.Core;

/// <summary>Line diff (longest common subsequence) for scripts and other text files compared with vanilla.</summary>
public static class TextDiff
{
    public sealed record Line(char Op, int? OldNumber, int? NewNumber, string Text);

    /// <summary>Lines with ' ' (same), '-' (only in old) or '+' (only in new). Unchanged runs longer than 2×context are folded.</summary>
    public static List<Line> Compare(string[] a, string[] b, int context = 3)
    {
        // Trim the common head and tail, then LCS on the middle.
        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        int n = a.Length - head - tail, m = b.Length - head - tail;
        var ops = new List<Line>();
        for (int i = 0; i < head; i++) ops.Add(new Line(' ', i + 1, i + 1, a[i]));
        if ((long)n * m > 16_000_000)
        {
            for (int i = 0; i < n; i++) ops.Add(new Line('-', head + i + 1, null, a[head + i]));
            for (int j = 0; j < m; j++) ops.Add(new Line('+', null, head + j + 1, b[head + j]));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[head + i] == b[head + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[head + x] == b[head + y]) { ops.Add(new Line(' ', head + x + 1, head + y + 1, a[head + x])); x++; y++; }
                else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) { ops.Add(new Line('+', null, head + y + 1, b[head + y])); y++; }
                else { ops.Add(new Line('-', head + x + 1, null, a[head + x])); x++; }
            }
        }
        for (int i = 0; i < tail; i++) ops.Add(new Line(' ', a.Length - tail + i + 1, b.Length - tail + i + 1, a[a.Length - tail + i]));

        // Keep only changes with a little context around them.
        var keep = new bool[ops.Count];
        for (int i = 0; i < ops.Count; i++)
            if (ops[i].Op != ' ')
                for (int k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++) keep[k] = true;
        var result = new List<Line>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (keep[i]) result.Add(ops[i]);
            else if (result.Count == 0 || result[^1].Op != '…') result.Add(new Line('…', null, null, ""));
        }
        return result;
    }
}
