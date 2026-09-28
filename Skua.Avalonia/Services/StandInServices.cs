using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>Core's hotkeys before the Mac App has its own (#82): none are bound, so no key is taken from the game.</summary>
public sealed class NoHotKeyService : IHotKeyService
{
    public void Reload()
    {
    }

    public List<T> GetHotKeys<T>() where T : IHotKey, new() => [];

    public HotKey? ParseToHotKey(string keyGesture) => null;
}
