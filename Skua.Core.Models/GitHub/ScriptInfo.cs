using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace Skua.Core.Models.GitHub;

public class ScriptInfo
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; }

    [JsonProperty("tags")]
    public string[] Tags { get; set; }

    [JsonProperty("path")]
    public string FilePath { get; set; }

    [JsonProperty("size")]
    public int Size { get; set; }

    [JsonProperty("sha256")]
    public string? Sha256 { get; set; }

    [JsonProperty("creationDate")]
    public DateTime? CreationDate { get; set; }

    [JsonProperty("fileName")]
    public string FileName { get; set; }

    [JsonProperty("downloadUrl")]
    public string DownloadUrl { get; set; }

    public string RelativePath => FilePath == FileName ? "Scripts/" : $"Scripts/{FilePath.Replace(FileName, "")}";

    public string LocalFile => Path.Combine(ClientFileSources.SkuaScriptsDIR, FilePath);

    public string ManagerLocalFile => Path.Combine(ClientFileSources.SkuaScriptsDIR, FilePath);

    public bool Downloaded => File.Exists(LocalFile);

    /// <summary>The local file's size as <c>scripts.json</c> measures it (see <see cref="Measure"/>); 0 when it isn't downloaded or can't be read.</summary>
    public int LocalSize => MeasureLocal()?.Size ?? 0;

    /// <summary>The local file's SHA-256 as <c>scripts.json</c> measures it (see <see cref="Measure"/>); null when it isn't downloaded or can't be read.</summary>
    public string? LocalSha256 => MeasureLocal()?.Sha256;

    /// <summary>Whether the local file differs from <c>scripts.json</c>'s entry, in size or, when the entry has one, in SHA-256.</summary>
    public bool Outdated => Downloaded && (MeasureLocal() is not { } local || local.Size != Size || (!string.IsNullOrEmpty(Sha256) && local.Sha256 != Sha256));

    /// <summary>Forgets the cached measure, for a caller that just rewrote <see cref="LocalFile"/>.</summary>
    public void InvalidateLocalHash() => _localMeasure = null;

    /// <summary>
    /// A Script file's size and SHA-256 as the Scripts generator writes them into <c>scripts.json</c>: those of its UTF-8 text without
    /// U+200B and U+FEFF, so a file with a byte order mark or a zero-width space matches its entry.
    /// </summary>
    private static (int Size, string Sha256) Measure(byte[] file)
    {
        byte[] text = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(file).Replace("\u200B", "").Replace("\uFEFF", ""));
        return (text.Length, Convert.ToHexStringLower(SHA256.HashData(text)));
    }

    private sealed record LocalMeasure(long Length, DateTime LastWriteUtc, int Size, string Sha256);

    // Measuring every downloaded Script is slow, so reuse the last measure until the file's length or write time changes.
    private LocalMeasure? _localMeasure;

    private (int Size, string Sha256)? MeasureLocal()
    {
        try
        {
            FileInfo file = new(LocalFile);
            if (!file.Exists)
                return null;

            LocalMeasure? cached = _localMeasure;
            if (cached is not null && cached.Length == file.Length && cached.LastWriteUtc == file.LastWriteTimeUtc)
                return (cached.Size, cached.Sha256);

            // Share write and delete so a download or delete during a background count doesn't fail.
            byte[] bytes;
            using (FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream copy = new())
            {
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }

            (int size, string sha256) = Measure(bytes);
            _localMeasure = new(file.Length, file.LastWriteTimeUtc, size, sha256);
            return (size, sha256);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public override string ToString()
    {
        return FileName;
    }
}