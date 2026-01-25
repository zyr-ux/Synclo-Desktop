using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Synclo.Components;

public partial class ResetPasswordDialogView : Window
{
    public ResetPasswordDialogView()
    {
        InitializeComponent();
    }
    
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close(false);

        base.OnKeyDown(e);
    }
}