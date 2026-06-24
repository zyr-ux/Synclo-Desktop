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

    private async void ServerUrl_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && DataContext is SettingsViewModel vm)
        {
            if (textBox.Text != vm.ServerUrl)
            {
                try
                {
                    await vm.UpdateServerUrlAsync(textBox.Text);
                }
                catch
                {
                    // Revert the TextBox to the last committed value on unexpected failure
                }
                finally
                {
                    textBox.Text = vm.ServerUrl;
                }
            }
        }
    }

    private async void ServerUrl_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox textBox && DataContext is SettingsViewModel vm)
        {
            if (textBox.Text != vm.ServerUrl)
            {
                try
                {
                    await vm.UpdateServerUrlAsync(textBox.Text);
                }
                catch
                {
                    // Revert the TextBox to the last committed value on unexpected failure
                }
                finally
                {
                    textBox.Text = vm.ServerUrl;
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
                }
            }
        }
    }
}