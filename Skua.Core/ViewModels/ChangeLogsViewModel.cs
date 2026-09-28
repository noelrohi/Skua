using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Utils;
using System.Diagnostics;

namespace Skua.Core.ViewModels;

public class ChangeLogsViewModel : BotControlViewModelBase
{
    private string _markDownContent = "Loading content...";

    public ChangeLogsViewModel() : base("Change Logs", 460, 500)
    {
        _markDownContent = string.Empty;

        Task.Run(async () => await GetChangeLogsContent());

        OpenDonationLink = new RelayCommand(() => Ioc.Default.GetRequiredService<IProcessService>().OpenLink("https://ko-fi.com/sharpthenightmare"));
        NavigateCommand = new RelayCommand<string>(NavigateToUrl);
    }

    public IRelayCommand OpenDonationLink { get; }
    public IRelayCommand NavigateCommand { get; }

    public string MarkdownDoc
    {
        get => _markDownContent; set => SetProperty(ref _markDownContent, value);
    }

    private async Task GetChangeLogsContent()
    {
        // The Mac App shows its own change log first, then upstream's; Windows shows upstream's alone.
        if (OperatingSystem.IsMacOS())
        {
            MarkdownDoc = await MacChangeLogs.GetAsync(HttpClients.GitHubRaw).ConfigureAwait(false);
            return;
        }

        try
        {
            MarkdownDoc = await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, "auqw/Skua/refs/heads/master/changelogs.md").ConfigureAwait(false);
        }
        catch
        {
            MarkdownDoc = "### No content found. Please check your internet connection.";
        }
    }

    private void NavigateToUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;

        // On macOS links open through the app's process service, which opens them as Finder would.
        if (OperatingSystem.IsMacOS())
        {
            Ioc.Default.GetRequiredService<IProcessService>().OpenLink(url.StartsWith("./") ? $"https://github.com/auqw/Skua/blob/master/{url.Substring(2)}" : url);
            return;
        }

        try
        {
            if (url.StartsWith("./"))
            {
                Process.Start(new ProcessStartInfo($"https://github.com/auqw/Skua/blob/master/{url.Substring(2)}") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
        }
        catch
        {
            /* ignored */
        }
    }
}
