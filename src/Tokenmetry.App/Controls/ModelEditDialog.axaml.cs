using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Tokenmetry.App.Controls;

public partial class ModelEditDialog : UserControl
{
    public ModelEditDialog()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => IdBox.Focus();
    }
}
