using System.Text.RegularExpressions;

namespace Kalo.Core.Scripting;

/// <summary>
/// The game type's category: the folder it is listed in by the game's menus (Slayer, Oddball, Action Sack...; MCC only
/// shows the folders it knows: Community and Insane don't appear). It is engine_data's "category" (names from strings\engine_category_strings.txt, which must be
/// included before it: included later, the compiler rejects the category).
/// </summary>
public static class MegaloCategory
{
    public const string Include = "include \"strings/engine_category_strings.txt\"";

    /// <summary>
    /// The Megalo text with its category set to <paramref name="category"/> (e.g. "action_sack"), and how many lines
    /// were added at the top (compiler line numbers past them are that much higher than in the original).
    /// </summary>
    public static (string Text, int LinesAddedAtTop) Set(string megalo, string category)
    {
        string nl = megalo.Contains("\r\n") ? "\r\n" : "\n";
        var top = new List<string>();
        if (!megalo.Contains("engine_category_strings.txt", StringComparison.OrdinalIgnoreCase)) top.Add(Include);
        var block = Regex.Match(megalo, @"(?ms)^[ \t]*engine_data[ \t]*\r?\n(.*?)^[ \t]*end\b");
        string text = megalo;
        if (block.Success)
        {
            string body = block.Groups[1].Value;
            string newBody = Regex.IsMatch(body, @"(?m)^[ \t]*category\b")
                ? Regex.Replace(body, @"(?m)^([ \t]*)category[ \t]+\S+", "${1}category " + category)
                : body + "\tcategory " + category + nl;
            text = megalo[..block.Groups[1].Index] + newBody + megalo[(block.Groups[1].Index + block.Groups[1].Length)..];
        }
        else
        {
            top.Add("engine_data");
            top.Add("\tcategory " + category);
            top.Add("end");
        }
        return (top.Count == 0 ? text : string.Join(nl, top) + nl + text, top.Count);
    }
}
