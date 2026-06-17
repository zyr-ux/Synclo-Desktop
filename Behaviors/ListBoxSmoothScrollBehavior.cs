using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Xaml.Behaviors.Interactions.Animated;

namespace Synclo.Behaviors;

public class ListBoxSmoothScrollBehavior : Behavior<ListBox>
{
    private ScrollViewer? _scroller;
    private VerticalScrollViewerAnimatedBehavior? _smoothBehavior;

    protected override void OnAttached()
    {
        base.OnAttached();
        
        if (AssociatedObject != null)
        {
            _scroller = AssociatedObject.FindDescendantOfType<ScrollViewer>();
            
            if (_scroller == null)
            {
                AssociatedObject.Loaded += OnAssociatedObjectLoaded;
            }
            else
            {
                AttachSmoothScrolling();
            }
        }
    }

    private void OnAssociatedObjectLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject != null)
        {
            AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
            _scroller = AssociatedObject.FindDescendantOfType<ScrollViewer>();
            AttachSmoothScrolling();
        }
    }

    private void AttachSmoothScrolling()
    {
        if (_scroller != null)
        {
            // Create a new instance for this specific ScrollViewer
            _smoothBehavior = new VerticalScrollViewerAnimatedBehavior();
            Interaction.GetBehaviors(_scroller).Add(_smoothBehavior);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        
        if (_scroller != null && _smoothBehavior != null)
        {
            Interaction.GetBehaviors(_scroller).Remove(_smoothBehavior);
            _smoothBehavior = null;
            _scroller = null;
        }
        
        if (AssociatedObject != null)
        {
            AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
        }
    }
}
