using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Synclo.ViewModels;

namespace Synclo.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void ServerUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox && DataContext is SettingsViewModel vm)
        {
            if (textBox.Text != vm.ServerUrl)
            {
                await vm.UpdateServerUrlAsync();
            }
        }
    }
}