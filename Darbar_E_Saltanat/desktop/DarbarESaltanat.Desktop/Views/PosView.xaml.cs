using System.Windows.Controls;
using System.Windows.Input;

namespace DarbarESaltanat.Desktop.Views;

public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Ctrl+F or Esc: jump to search (barcode scanners type into the focused box).
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused) { SearchBox.Clear(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
