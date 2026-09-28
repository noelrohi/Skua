using Avalonia.Platform.Storage;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>Core's file dialogs as macOS open and save panels, through the active window's Avalonia storage provider.</summary>
/// <remarks>Each call waits for the panel to close; without a window to show one on, it returns null as a cancelled panel does.</remarks>
public sealed class AvaloniaFileDialogService : IFileDialogService
{
    private const string TextFilter = "Text Files (*.txt)|*.txt";

    public string? OpenFile() => OpenFile(ClientFileSources.SkuaDIR, "");

    public string? OpenFile(string filters) => OpenFile(ClientFileSources.SkuaDIR, filters);

    public string? OpenFile(string initialDirectory, string filters) => UiThread.Wait(async () =>
    {
        if (Storage() is not { } storage)
            return null;
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            SuggestedStartLocation = await Folder(storage, initialDirectory),
            FileTypeFilter = FileTypes(filters),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    });

    public string? OpenFolder() => OpenFolder(ClientFileSources.SkuaDIR);

    public string? OpenFolder(string initialDirectory) => UiThread.Wait(async () =>
    {
        if (Storage() is not { } storage)
            return null;
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            SuggestedStartLocation = await Folder(storage, initialDirectory),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    });

    public IEnumerable<string>? OpenText() => OpenFile(TextFilter) is { } file ? File.ReadAllLines(file) : null;

    public string? Save() => Save(ClientFileSources.SkuaDIR, TextFilter);

    public string? Save(string filters) => Save(ClientFileSources.SkuaDIR, filters);

    public string? Save(string initialDirectory, string filters) => UiThread.Wait(async () =>
    {
        if (Storage() is not { } storage)
            return null;
        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = await Folder(storage, initialDirectory),
            FileTypeChoices = FileTypes(filters),
        });
        return file?.TryGetLocalPath();
    });

    public void SaveText(string contents)
    {
        if (Save() is { } file)
            File.WriteAllText(file, contents);
    }

    public void SaveText(IEnumerable<string> contents)
    {
        if (Save() is { } file)
            File.WriteAllLines(file, contents);
    }

    /// <summary>
    /// Reads a Windows filter string, <c>Name (*.cs)|*.cs|All|*.*</c>: pairs of a name and <c>;</c>-separated patterns.
    /// Null for no filter, which shows every file.
    /// </summary>
    public static IReadOnlyList<FilePickerFileType>? FileTypes(string filters)
    {
        string[] parts = filters.Split('|');
        List<FilePickerFileType> types = [];
        for (int i = 0; i + 1 < parts.Length; i += 2)
            types.Add(new FilePickerFileType(parts[i].Trim()) { Patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) });
        return types.Count > 0 ? types : null;
    }

    private static IStorageProvider? Storage() => Windows.Active()?.StorageProvider;

    private static async Task<IStorageFolder?> Folder(IStorageProvider storage, string path) =>
        Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : null;
}
