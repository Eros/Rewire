using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rewire.ViewModels;

namespace Rewire.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnelling sees the key before focus navigation can swallow Tab or the arrows.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.KeyReleased(e.PhysicalKey))
            e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.KeyPressed(e.PhysicalKey, e.KeyModifiers))
            e.Handled = true;
    }
}
