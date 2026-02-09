using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace Synclo.Behaviors;

public class ScrollViewerInfiniteScrollBehavior : Behavior<ScrollViewer>
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ScrollViewerInfiniteScrollBehavior, ICommand?>(nameof(Command));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.ScrollChanged += OnScrollChanged;
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.ScrollChanged -= OnScrollChanged;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (AssociatedObject == null || Command == null) return;

        var verticalOffset = AssociatedObject.Offset.Y;
        var maxVerticalOffset = AssociatedObject.Extent.Height - AssociatedObject.Viewport.Height;

        // Trigger when within 100 pixels of the bottom
        if (maxVerticalOffset > 0 && verticalOffset >= maxVerticalOffset - 100)
        {
            if (Command.CanExecute(null))
            {
                Command.Execute(null);
            }
        }
    }
}
