using Avalonia.Media;
using Skua.Avalonia.Services;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Tests;

/// <summary>How the app opens links, folders and Scripts, and the themes it reads from the Windows client's settings; nothing is launched.</summary>
public sealed class MacProcessServiceTests
{
    private const string Code = "/usr/local/bin/code";

    [Fact]
    public async Task VS_Code_opens_the_Scripts_folder_and_a_Script_through_its_code_command()
    {
        Recorder runs = new(succeeds: true);
        MacProcessService service = new(new NoDialogs(), runs.Run, () => Code);

        service.OpenVSC();
        service.OpenVSC("/tmp/Farm.cs");

        await runs.WaitAsync(2);
        Assert.Contains((Code, $"{ClientFileSources.SkuaScriptsDIR}"), runs.Lines);
        Assert.Contains((Code, $"{ClientFileSources.SkuaScriptsDIR} /tmp/Farm.cs"), runs.Lines);
    }

    [Fact]
    public async Task Without_VS_Code_a_Script_opens_in_the_text_editor_and_the_Scripts_folder_says_so()
    {
        Recorder runs = new(succeeds: false);
        NoDialogs dialogs = new();
        MacProcessService service = new(dialogs, runs.Run, () => null);

        service.OpenVSC("/tmp/Farm.cs");
        service.OpenVSC();

        await runs.WaitAsync(3);
        await WaitAsync(() => dialogs.Notices.Count == 1);
        Assert.Contains(("/usr/bin/open", "-b com.microsoft.VSCode " + ClientFileSources.SkuaScriptsDIR + " /tmp/Farm.cs"), runs.Lines);
        Assert.Contains(("/usr/bin/open", "-t /tmp/Farm.cs"), runs.Lines);
        Assert.Equal("VS Code not found", dialogs.Notices.Single());
    }

    [Fact]
    public async Task A_link_opens_with_open_and_nothing_that_looks_like_an_option_does()
    {
        Recorder runs = new(succeeds: true);
        MacProcessService service = new(new NoDialogs(), runs.Run, () => null);

        service.OpenLink("-a Calculator");
        service.OpenLink("https://discord.gg/Xz5bF6q2CX");

        await runs.WaitAsync(1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([("/usr/bin/open", "https://discord.gg/Xz5bF6q2CX")], runs.Lines);
    }

    [Theory]
    [InlineData("Skua,Dark,#FF607D8B,#FF607D8B,#FF000000,#FF000000,true,4.5,Medium,All")]
    [InlineData("RBot,Light,#FF9C934E,#FF9C934E,#FF000000,#FF000000")]
    [InlineData("Mine,Dark,#ffe91e63,#ff607d8b,#ffffffff,#ff000000,True,3,High,Primary,")]
    public void A_theme_the_Windows_client_saved_reads_and_saves_back_the_same(string saved)
    {
        SkuaTheme theme = SkuaTheme.Parse(saved)!;

        SkuaTheme again = SkuaTheme.Parse(theme.Format())!;

        Assert.Equal(saved.Split(',')[0], theme.Name);
        Assert.Equal(Color.Parse(saved.Split(',')[2]), theme.Primary);
        Assert.Equal((theme.IsDark, theme.Primary, theme.Secondary, theme.PrimaryForeground, theme.SecondaryForeground), (again.IsDark, again.Primary, again.Secondary, again.PrimaryForeground, again.SecondaryForeground));
        Assert.Equal((theme.UseColorAdjustment, theme.ContrastRatio, theme.Contrast, theme.Colors), (again.UseColorAdjustment, again.ContrastRatio, again.Contrast, again.Colors));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Broken")]
    [InlineData("Broken,Dark,not-a-colour,#FF000000,#FF000000,#FF000000")]
    public void A_theme_setting_that_cant_be_read_is_no_theme(string? saved) => Assert.Null(SkuaTheme.Parse(saved));

    private static async Task WaitAsync(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
        Assert.True(done());
    }

    private sealed class Recorder(bool succeeds)
    {
        private readonly List<(string, string)> _lines = [];

        public List<(string Program, string Arguments)> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public bool Run(string program, IReadOnlyList<string> arguments)
        {
            lock (_lines)
                _lines.Add((program, string.Join(' ', arguments)));
            return succeeds;
        }

        public Task WaitAsync(int count) => MacProcessServiceTests.WaitAsync(() => Lines.Count >= count);
    }

    private sealed class NoDialogs : IDialogService
    {
        private readonly List<string> _notices = [];

        public List<string> Notices
        {
            get
            {
                lock (_notices)
                    return [.. _notices];
            }
        }

        public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => null;

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, string title) where TViewModel : class => null;

        public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class => null;

        public void ShowMessageBox(string message, string caption)
        {
            lock (_notices)
                _notices.Add(caption);
        }

        public bool? ShowMessageBox(string message, string caption, bool yesAndNo) => null;

        public DialogResult ShowMessageBox(string message, string caption, params string[] buttons) => DialogResult.Cancelled;
    }
}
