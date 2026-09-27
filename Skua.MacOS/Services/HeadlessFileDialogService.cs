using System.Diagnostics;
using Skua.Core.Interfaces;

namespace Skua.MacOS.Services;

/// <summary>File dialogs are never surfaced headless; every one is cancelled.</summary>
public sealed class HeadlessFileDialogService : IFileDialogService
{
    public string? OpenFile() => Cancelled<string>();

    public string? OpenFile(string filters) => Cancelled<string>();

    public string? OpenFile(string initialDirectory, string filters) => Cancelled<string>();

    public string? OpenFolder() => Cancelled<string>();

    public string? OpenFolder(string initialDirectory) => Cancelled<string>();

    public IEnumerable<string>? OpenText() => Cancelled<IEnumerable<string>>();

    public string? Save() => Cancelled<string>();

    public string? Save(string filters) => Cancelled<string>();

    public string? Save(string initialDirectory, string filters) => Cancelled<string>();

    public void SaveText(string contents) => Cancelled<string>();

    public void SaveText(IEnumerable<string> contents) => Cancelled<string>();

    private static T? Cancelled<T>() where T : class
    {
        Trace.WriteLine("A file dialog isn't surfaced headless; it was cancelled.");
        return null;
    }
}
