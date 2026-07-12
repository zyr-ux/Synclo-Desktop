using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Synclo.Models;
using Synclo.ViewModels;

namespace Synclo.Views;

public partial class HomeView : UserControl
{
    private bool _isContextMenuOpen;

    public HomeView()
    {
        InitializeComponent();
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is HomeViewModel vm && vm.RightClickAction != "ContextMenu")
        {
            e.Cancel = true;
            return;
        }
        _isContextMenuOpen = true;
    }

    private void OnContextMenuClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Defer setting to false so the click that closed the menu is ignored by the underlying card.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _isContextMenuOpen = false);
    }

    private void OnHistoryItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_isContextMenuOpen)
            return;

        if (sender is not Border { DataContext: HistoryItemModel entry } border)
            return;

        var pointerKind = e.GetCurrentPoint(border).Properties.PointerUpdateKind;
        if (pointerKind == PointerUpdateKind.LeftButtonPressed)
        {
            if (DataContext is HomeViewModel vm)
            {
                vm.ItemClickedCommand.Execute(entry);
            }
            e.Handled = true;
        }
        else if (pointerKind == PointerUpdateKind.RightButtonPressed)
        {
            if (DataContext is HomeViewModel vm)
            {
                if (vm.RightClickAction != "ContextMenu")
                {
                    vm.ItemRightClickedCommand.Execute(entry);
                    e.Handled = true;
                }
            }
        }
    }
}
