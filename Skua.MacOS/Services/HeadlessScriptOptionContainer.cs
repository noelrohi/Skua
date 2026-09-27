using Skua.Core.Interfaces;
using Skua.Core.Options;

namespace Skua.MacOS.Services;

/// <summary>
/// A Script's options with nobody to show them to: the options window Core opens at a Script's first start does nothing, so the
/// Script keeps its stored values. Core's own window resets them to the defaults and reloads them while the Script already runs.
/// </summary>
public sealed class HeadlessScriptOptionContainer : ScriptOptionContainer, IScriptOptionContainer
{
    public HeadlessScriptOptionContainer(IDialogService dialogService)
        : base(dialogService)
    {
    }

    /// <summary>Does nothing; the Control Surface sets options before a start instead.</summary>
    public new void Configure()
    {
    }
}
