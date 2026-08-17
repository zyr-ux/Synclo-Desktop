using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Synclo.Models;
using Synclo.ViewModels;

namespace Synclo.Behaviors;

public class HistoryItemInteractionBehavior : Behavior<Border>
{
    private bool _isContextMenuOpen;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.PointerPressed += OnPointerPressed;
            AssociatedObject.Loaded += OnLoaded;

            if (AssociatedObject.ContextMenu != null)
            {
                AttachContextMenuEvents(AssociatedObject.ContextMenu);
            }
        }
    }

    private void OnLoaded(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (AssociatedObject?.ContextMenu != null)
        {
            AttachContextMenuEvents(AssociatedObject.ContextMenu);
        }
    }

    private void AttachContextMenuEvents(ContextMenu menu)
    {
        menu.Opening -= OnContextMenuOpening;
        menu.Closing -= OnContextMenuClosing;
        menu.Opening += OnContextMenuOpening;
        menu.Closing += OnContextMenuClosing;
    }

    private void DetachContextMenuEvents(ContextMenu menu)
    {
        menu.Opening -= OnContextMenuOpening;
        menu.Closing -= OnContextMenuClosing;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.PointerPressed -= OnPointerPressed;
            AssociatedObject.Loaded -= OnLoaded;
            if (AssociatedObject.ContextMenu != null)
            {
                DetachContextMenuEvents(AssociatedObject.ContextMenu);
            }
        }
    }

    private void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var vm = GetHomeViewModel();
        if (vm != null && vm.RightClickAction != "ContextMenu")
        {
            e.Cancel = true;
            return;
        }
        _isContextMenuOpen = true;
    }

    private void OnContextMenuClosing(object? sender, CancelEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isContextMenuOpen = false;
            var listBox = AssociatedObject?.FindAncestorOfType<ListBox>();
            if (listBox != null)
            {
                listBox.SelectedItem = null;
            }
        });
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isContextMenuOpen)
            return;

        if (sender is not Border { DataContext: HistoryItemModel entry } border)
            return;

        var vm = GetHomeViewModel();
        if (vm == null) return;

        var pointerKind = e.GetCurrentPoint(border).Properties.PointerUpdateKind;
        if (pointerKind == PointerUpdateKind.LeftButtonPressed)
        {
            vm.ItemClickedCommand.Execute(entry);
            e.Handled = true;
        }
        else if (pointerKind == PointerUpdateKind.RightButtonPressed)
        {
            if (vm.RightClickAction != "ContextMenu")
            {
                vm.ItemRightClickedCommand.Execute(entry);
                e.Handled = true;
            }
        }
    }

    private HomeViewModel? GetHomeViewModel()
    {
        var parent = AssociatedObject?.FindAncestorOfType<UserControl>();
        return parent?.DataContext as HomeViewModel;
    }
}
