using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Kalo.App.Views;

/// <summary>
/// Drag a number field left/right to change it (like Blender): click and drag horizontally; Shift = fine, Ctrl = coarse.
/// A plain click still places the caret to type. The value is committed when the drag ends (as if the box lost focus).
/// <code>&lt;TextBox views:NumberDrag.Step="0.1" /&gt;</code> (setting Step turns it on; 0 = off.)
/// </summary>
public static class NumberDrag
{
    /// <summary>Change per 8 pixels of drag. Integer steps keep whole numbers.</summary>
    public static readonly AttachedProperty<double> StepProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Step", typeof(NumberDrag));

    /// <summary>Lowest / highest value (optional).</summary>
    public static readonly AttachedProperty<double> MinimumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Minimum", typeof(NumberDrag), double.NegativeInfinity);
    public static readonly AttachedProperty<double> MaximumProperty =
        AvaloniaProperty.RegisterAttached<TextBox, double>("Maximum", typeof(NumberDrag), double.PositiveInfinity);

    public static double GetStep(TextBox box) => box.GetValue(StepProperty);
    public static void SetStep(TextBox box, double value) => box.SetValue(StepProperty, value);
    public static double GetMinimum(TextBox box) => box.GetValue(MinimumProperty);
    public static void SetMinimum(TextBox box, double value) => box.SetValue(MinimumProperty, value);
    public static double GetMaximum(TextBox box) => box.GetValue(MaximumProperty);
    public static void SetMaximum(TextBox box, double value) => box.SetValue(MaximumProperty, value);

    private sealed class State
    {
        public Point Start;
        public double StartValue;
        public bool Pressed;
        public bool Dragging;
        public bool Integer;
        public int Decimals;
        public double Step;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<TextBox, State?>("DragState", typeof(NumberDrag));

    static NumberDrag()
    {
        StepProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            bool on = e.NewValue is double d && d > 0;
            bool was = e.OldValue is double o && o > 0;
            if (on && !was)
            {
                box.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, true);
                box.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
                box.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel, true);
                if (ToolTip.GetTip(box) == null) ToolTip.SetTip(box, "Drag left/right to change (Shift: fine, Ctrl: coarse), or type a value");
            }
            else if (!on && was)
            {
                box.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
                box.RemoveHandler(InputElement.PointerMovedEvent, Moved);
                box.RemoveHandler(InputElement.PointerReleasedEvent, Released);
            }
        });
    }

    private static bool TryParse(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private static void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBox box || !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
        if (!TryParse(box.Text, out double v)) { box.SetValue(StateProperty, null); return; }
        string t = (box.Text ?? "").Trim();
        int dot = t.IndexOfAny(new[] { '.', ',' });
        double step = GetStep(box);
        // A whole-number step on a decimal value moves by tenths.
        if (step >= 1 && dot >= 0) step = 0.1;
        box.SetValue(StateProperty, new State
        {
            Step = step,
            Start = e.GetPosition(box),
            StartValue = v,
            Pressed = true,
            Integer = dot < 0 && step >= 1,
            Decimals = Math.Max(dot < 0 ? 0 : t.Length - dot - 1, step >= 1 ? 0 : Math.Max(1, (int)Math.Ceiling(-Math.Log10(step)))),
        });
    }

    private static void Moved(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBox box || box.GetValue(StateProperty) is not { Pressed: true } s) return;
        var p = e.GetPosition(box);
        double dx = p.X - s.Start.X;
        if (!s.Dragging)
        {
            if (Math.Abs(dx) < 4) return;
            s.Dragging = true;
            e.Pointer.Capture(box);
            box.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }
        double step = s.Step;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) step *= 0.1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) step *= 10;
        double v = s.StartValue + Math.Round(dx / 8.0) * step;
        v = Math.Clamp(v, GetMinimum(box), GetMaximum(box));
        box.Text = s.Integer ? Math.Round(v).ToString(CultureInfo.InvariantCulture) : Math.Round(v, Math.Min(s.Decimals + (step < 0.1 ? 1 : 0), 4)).ToString(CultureInfo.InvariantCulture);
        box.ClearSelection();
        e.Handled = true;
    }

    private static void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TextBox box || box.GetValue(StateProperty) is not { } s) return;
        box.SetValue(StateProperty, null);
        if (!s.Dragging) return; // a click: type
        e.Pointer.Capture(null);
        box.Cursor = null;
        e.Handled = true;
        // Commit like leaving the box (bindings update on LostFocus).
        TopLevel.GetTopLevel(box)?.FocusManager?.ClearFocus();
    }
}
