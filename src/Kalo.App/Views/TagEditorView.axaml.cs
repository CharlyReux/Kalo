using Avalonia.Controls;
using Avalonia.Input;

namespace Kalo.App.Views;

public partial class TagEditorView : UserControl
{
    public TagEditorView() => InitializeComponent();

    /// <summary>Enter commits the value (by moving focus away, which pushes the text to the field).</summary>
    private void ValueBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FieldTree.Focus();
            e.Handled = true;
        }
    }
}
