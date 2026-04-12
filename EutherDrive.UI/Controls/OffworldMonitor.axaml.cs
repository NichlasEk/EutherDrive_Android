using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace EutherDrive.UI.Controls;

public partial class OffworldMonitor : UserControl
{
    public OffworldMonitor()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
