using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Kalo.App.ViewModels;

namespace Kalo.App.Views;

/// <summary>One error or warning placed on the text; its offsets follow the edits until the next check.</summary>
public sealed class ErrorMarker : TextSegment
{
    /// <summary>The first error of its line (or its first warning).</summary>
    public required ScriptMessage Message { get; init; }
    /// <summary>The line's other messages (often knock-on errors of the first), shown with it on hover.</summary>
    public IReadOnlyList<ScriptMessage> Others { get; init; } = Array.Empty<ScriptMessage>();
    public bool IsError => Message.IsError;
    public IEnumerable<ScriptMessage> All => new[] { Message }.Concat(Others);
}

/// <summary>
/// Red (errors) and yellow (warnings) wavy underlines in a code editor, like an IDE, plus a dot in the margin of
/// each line that has one. <see cref="Set"/> places the compiler's messages on the text.
/// </summary>
public sealed class ErrorMarkers : IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private TextSegmentCollection<ErrorMarker> _markers;
    public static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#F14C4C"));
    public static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#CCA700"));

    public ErrorMarkers(TextEditor editor)
    {
        _editor = editor;
        _markers = new TextSegmentCollection<ErrorMarker>(editor.Document);
        editor.TextArea.TextView.BackgroundRenderers.Add(this);
        Margin = new ErrorMargin(this);
        editor.TextArea.LeftMargins.Insert(0, Margin);
        editor.DocumentChanged += (_, _) => Clear();
    }

    public ErrorMargin Margin { get; }
    public KnownLayer Layer => KnownLayer.Selection;
    public IEnumerable<ErrorMarker> All => _markers;
    public int Count => _markers.Count;

    public void Clear()
    {
        _markers = new TextSegmentCollection<ErrorMarker>(_editor.Document);
        Redraw();
    }

    /// <summary>
    /// Places messages checked on <paramref name="checkedText"/>. When the text changed since (typing went on),
    /// lines are matched by number as long as they still exist.
    /// </summary>
    public void Set(IEnumerable<ScriptMessage> messages, string checkedText)
    {
        var doc = _editor.Document;
        _markers = new TextSegmentCollection<ErrorMarker>(doc);
        bool same = doc.TextLength == checkedText.Length && doc.Text == checkedText;
        // One underline per line: its first error (or first warning). The compiler often adds knock-on errors
        // for the same mistake; they are listed with it. Bungie's localization warnings (literal strings) do not
        // matter for mods and are not underlined.
        var wanted = messages.Where(m => m.Line >= 1 && !m.Message.Contains("enforces localization", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var group in wanted.GroupBy(m => Math.Min(m.Line, Math.Max(1, doc.LineCount))))
        {
            var ordered = group.Where(m => m.IsError).Concat(group.Where(m => !m.IsError)).ToList();
            var m = ordered[0];
            if (m.Line < 1 || doc.LineCount == 0) continue;
            // "end of file" errors can point one line past the end.
            var line = doc.GetLineByNumber(Math.Min(m.Line, doc.LineCount));
            string text = doc.GetText(line.Offset, line.Length);
            int start, length;
            if (same && m.Start >= line.Offset && m.Start <= line.EndOffset && m.Length > 0)
            {
                // Megalo: the compiler's own token offsets.
                start = m.Start;
                length = Math.Min(m.Length, line.EndOffset - start);
            }
            else if (m.Column > 0)
            {
                // Kalo Script: the word (or symbol) at the column.
                int col = Math.Min(m.Column - 1, text.Length);
                (int a, int b) = TokenAt(text, col);
                start = line.Offset + a;
                length = b - a;
            }
            else if (!string.IsNullOrEmpty(m.Token) && FindWord(text, m.Token) is int at and >= 0)
            {
                start = line.Offset + at;
                length = m.Token.Length;
            }
            else
            {
                // Only the line is known: underline its code (not the indentation or a trailing comment).
                int a = 0;
                while (a < text.Length && char.IsWhiteSpace(text[a])) a++;
                int b = CodeEnd(text);
                if (b <= a) { a = 0; b = text.Length; }
                start = line.Offset + a;
                length = b - a;
            }
            if (length <= 0)
            {
                // At the end of a line (something missing): mark the last character, or a small space after it.
                start = Math.Max(line.Offset, Math.Min(start, line.EndOffset) - (line.Length > 0 ? 1 : 0));
                length = line.Length > 0 ? 1 : 0;
            }
            _markers.Add(new ErrorMarker { StartOffset = start, Length = length, Message = m, Others = ordered.Skip(1).ToList() });
        }
        Redraw();
    }

    private static (int, int) TokenAt(string text, int col)
    {
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';
        if (col >= text.Length) return (Math.Max(0, text.Length - 1), text.Length);
        if (text[col] == '"')
        {
            int end = text.IndexOf('"', col + 1);
            return (col, end < 0 ? CodeEnd(text) : end + 1);
        }
        static bool Op(char c) => "=+-*/%<>!|&^~".IndexOf(c) >= 0;
        if (Op(text[col]))
        {
            int e = col;
            while (e < text.Length && Op(text[e])) e++;
            return (col, e);
        }
        if (!Word(text[col])) return (col, col + 1);
        int a = col, b = col;
        while (a > 0 && Word(text[a - 1])) a--;
        while (b < text.Length && Word(text[b])) b++;
        return (a, b);
    }

    private static int? FindWord(string text, string word)
    {
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            bool before = i == 0 || !(char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_');
            int e = i + word.Length;
            bool after = e >= text.Length || !(char.IsLetterOrDigit(text[e]) || text[e] == '_');
            if (before && after) return i;
        }
        return null;
    }

    /// <summary>Where the code of a line ends: before a -- or ; comment outside a string, trailing spaces trimmed.</summary>
    private static int CodeEnd(string text)
    {
        bool inString = false;
        int end = text.Length;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') inString = !inString;
            else if (!inString && (c == ';' || (c == '-' && i + 1 < text.Length && text[i + 1] == '-'))) { end = i; break; }
        }
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    /// <summary>The markers under a text offset (for the hover popup).</summary>
    public IReadOnlyList<ErrorMarker> At(int offset) =>
        _markers.FindOverlappingSegments(Math.Max(0, offset - 1), 2).Where(m => offset >= m.StartOffset && offset <= m.EndOffset).ToList();

    /// <summary>The markers on a line (1-based).</summary>
    public IReadOnlyList<ErrorMarker> OnLine(int line)
    {
        if (line < 1 || line > _editor.Document.LineCount) return Array.Empty<ErrorMarker>();
        var l = _editor.Document.GetLineByNumber(line);
        return _markers.FindOverlappingSegments(l.Offset, l.TotalLength).ToList();
    }

    private void Redraw()
    {
        _editor.TextArea.TextView.InvalidateLayer(Layer);
        Margin.InvalidateVisual();
    }

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_markers.Count == 0 || !textView.VisualLinesValid) return;
        var lines = textView.VisualLines;
        if (lines.Count == 0) return;
        int from = lines[0].FirstDocumentLine.Offset, to = lines[^1].LastDocumentLine.EndOffset;
        // Warnings first, so an error on the same text is drawn on top.
        foreach (var marker in _markers.FindOverlappingSegments(from, to - from).OrderBy(m => m.IsError))
        {
            var pen = new Pen(marker.IsError ? ErrorBrush : WarningBrush, 1.2);
            var segment = marker.Length > 0 ? (ISegment)marker : new TextSegment { StartOffset = marker.StartOffset, Length = 0 };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                double width = Math.Max(rect.Width, 6);
                double y = rect.Bottom - 1.5;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    const double step = 2.5;
                    g.BeginFigure(new Point(rect.Left, y), false);
                    bool up = true;
                    for (double x = rect.Left + step; x <= rect.Left + width + 0.01; x += step, up = !up)
                        g.LineTo(new Point(x, up ? y - step * 0.9 : y));
                    g.EndFigure(false);
                }
                dc.DrawGeometry(null, pen, geometry);
            }
        }
    }
}

/// <summary>A narrow margin left of the line numbers: a red (or yellow) dot on each line with an error (or warning).</summary>
public sealed class ErrorMargin : AbstractMargin
{
    private readonly ErrorMarkers _owner;

    public ErrorMargin(ErrorMarkers owner) => _owner = owner;

    protected override Size MeasureOverride(Size availableSize) => new(12, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= Changed;
        if (newTextView != null) newTextView.VisualLinesChanged += Changed;
        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void Changed(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        string? tip = TipAt(e.GetPosition(this));
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
    }

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var view = TextView;
        if (view == null || !view.VisualLinesValid || _owner.Count == 0) return;
        foreach (var line in view.VisualLines)
        {
            var markers = _owner.OnLine(line.FirstDocumentLine.LineNumber);
            if (markers.Count == 0) continue;
            bool error = markers.Any(m => m.IsError);
            double y = line.VisualTop - view.VerticalOffset + line.Height / 2;
            dc.DrawEllipse(error ? ErrorMarkers.ErrorBrush : ErrorMarkers.WarningBrush, null, new Point(6, y), 3.5, 3.5);
        }
    }

    /// <summary>The messages of the line at a point of this margin (for its tooltip).</summary>
    public string? TipAt(Point p)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid) return null;
        var line = view.GetVisualLineFromVisualTop(p.Y + view.VerticalOffset);
        if (line == null) return null;
        var markers = _owner.OnLine(line.FirstDocumentLine.LineNumber);
        return markers.Count == 0 ? null : string.Join("\n", markers.SelectMany(m => m.All).Select(m => (m.IsError ? "Error: " : "Warning: ") + m.Display));
    }
}
