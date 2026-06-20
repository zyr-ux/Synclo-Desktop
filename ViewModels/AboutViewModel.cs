using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Synclo.Services.Utilities;

namespace Synclo.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    private readonly IUtils _utils;

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public AboutViewModel(IUtils utils)
    {
        _utils = utils;
    }

    [RelayCommand]
    private void OpenGitHub()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop");
    }

    [RelayCommand]
    private void OpenIssues()
    {
        _utils.OpenUrl("https://github.com/zyr-ux/Synclo-Desktop/issues");
    }
}