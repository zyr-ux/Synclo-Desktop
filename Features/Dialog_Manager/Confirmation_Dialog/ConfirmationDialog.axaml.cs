using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Synclo.Features.Dialog_Manager.Confirmation_Dialog;

public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
        // Allows moving the window since SystemDecorations="None"
        //PointerPressed += (s, e) => BeginMoveDrag(e);
    }

    public ConfirmationDialog(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool isDangerous = false) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = confirmText;
        NoButton.Content = cancelText;

        if (isDangerous)
        {
            YesButton.Classes.Add("dangerous");
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close(false);

        base.OnKeyDown(e);
    }

    private void OnYesClick(object? sender, RoutedEventArgs e) => Close(true);
    private void OnNoClick(object? sender, RoutedEventArgs e) => Close(false);
}