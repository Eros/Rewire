using Avalonia.Controls;
using Avalonia.Threading;
using Rewire.ViewModels;

namespace Rewire.Views;

public partial class TrainerView : UserControl
{
    // Keeps the clock moving and ends timed drills even while no key is being pressed.
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public TrainerView()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => (DataContext as TrainerViewModel)?.Tick();
        AttachedToVisualTree += (_, _) => _clock.Start();
        DetachedFromVisualTree += (_, _) => _clock.Stop();
    }
}
