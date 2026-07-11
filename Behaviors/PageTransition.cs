using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace Synclo.Behaviors;

public class PageTransition : AvaloniaObject, IPageTransition
{
    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<PageTransition, TimeSpan>(
            nameof(Duration),
            TimeSpan.FromMilliseconds(250));

    public static readonly StyledProperty<double> SlideDistanceProperty =
        AvaloniaProperty.Register<PageTransition, double>(
            nameof(SlideDistance),
            30.0);

    public static readonly StyledProperty<Easing> SlideEaseProperty =
        AvaloniaProperty.Register<PageTransition, Easing>(
            nameof(SlideEase),
            new CubicEaseOut());

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double SlideDistance
    {
        get => GetValue(SlideDistanceProperty);
        set => SetValue(SlideDistanceProperty, value);
    }

    public Easing SlideEase
    {
        get => GetValue(SlideEaseProperty);
        set => SetValue(SlideEaseProperty, value);
    }

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || to is null)
            return;

        to.Opacity = 1.0;

        if (from is not null)
        {
            from.Opacity = 0.0;

            // Restoring Opacity inside finally causes a visible flicker since the control
            // is still in the visual tree for a frame. Instead, restore it safely only
            // after it has been detached from the UI tree.
            EventHandler<VisualTreeAttachmentEventArgs>? onDetached = null;
            onDetached = (sender, args) =>
            {
                from.DetachedFromVisualTree -= onDetached;
                from.Opacity = 1.0;
            };
            from.DetachedFromVisualTree += onDetached;
        }

        var translate = to.RenderTransform as TranslateTransform;

        if (translate is null)
        {
            translate = new TranslateTransform();
            to.RenderTransform = translate;
        }

        translate.Y = forward ? SlideDistance : -SlideDistance;

        var animation = new Animation
        {
            Duration = Duration,
            FillMode = FillMode.Forward,
            Easing = SlideEase,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(TranslateTransform.YProperty, translate.Y)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(TranslateTransform.YProperty, 0d)
                    }
                }
            }
        };

        try
        {
            await animation.RunAsync(to, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                translate.Y = 0;
        }
    }
}