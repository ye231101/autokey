using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace AutoKey;

/// <summary>
/// A small always-on-top status panel. On Windows it never takes keyboard focus, so its buttons can be clicked
/// without pulling focus away from the window being typed into.
/// </summary>
public partial class OverlayWindow : Window
{
    private bool _positioned;

    public OverlayWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Dispatcher.UIThread.Post(PlaceInCorner, DispatcherPriority.Loaded);
    }

    /// <summary>Bottom-right corner of the primary screen, the first time only (afterwards it stays where it was dragged).</summary>
    private void PlaceInCorner()
    {
        if (_positioned || Screens.Primary is not { } screen)
        {
            return;
        }

        PixelRect area = screen.WorkingArea;
        int width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        int height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        Position = new PixelPoint(area.Right - width - 8, area.Bottom - height - 8);
        _positioned = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
