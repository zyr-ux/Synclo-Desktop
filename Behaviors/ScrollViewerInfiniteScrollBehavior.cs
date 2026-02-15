using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace Synclo.Behaviors;

public class ScrollViewerInfiniteScrollBehavior : Behavior<Control>
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ScrollViewerInfiniteScrollBehavior, ICommand?>(nameof(Command));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private ScrollViewer? _scroller;
    private IDisposable? _scrollSubscription;

    protected override void OnAttached()
    {
        base.OnAttached();
        
        if (AssociatedObject is ScrollViewer sv)
        {
            _scroller = sv;
        }
        else if (AssociatedObject != null)
        {
            // If attached to ListBox/ItemsControl, try to find the internal ScrollViewer
            // We might need to wait for template application or layout
            _scroller = AssociatedObject.FindDescendantOfType<ScrollViewer>();
            
            if (_scroller == null)
            {
                // If not found immediately (e.g. before template applied), wait for Loaded
                AssociatedObject.Loaded += OnAssociatedObjectLoaded;
            }
        }

        if (_scroller != null)
        {
            SubscribeToScroll();
        }
    }

    private void OnAssociatedObjectLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            control.Loaded -= OnAssociatedObjectLoaded;
            _scroller = control.FindDescendantOfType<ScrollViewer>();
            SubscribeToScroll();
        }
    }

    private void SubscribeToScroll()
    {
        if (_scroller != null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        
        if (_scroller != null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
            _scroller = null;
        }
        
        if (AssociatedObject != null)
        {
            AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller == null || Command == null) return;

        var verticalOffset = _scroller.Offset.Y;
        var maxVerticalOffset = _scroller.Extent.Height - _scroller.Viewport.Height;

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
