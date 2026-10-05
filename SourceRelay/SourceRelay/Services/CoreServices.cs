using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SourceRelay.Models;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Services;

public static class HashService
{
    public static async Task<string> FileAsync(string path, CancellationToken ct = default)
    { await using var s = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(s, ct)).ToLowerInvariant(); }
    public static string Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class Utf8BomService
{
    private static readonly byte[] Bom = [0xef, 0xbb, 0xbf];

    public static bool HasBom(byte[] bytes) => bytes.AsSpan().StartsWith(Bom);

    public static byte[] Preserve(byte[] localBytes, byte[] returnedBytes)
    {
        var localHasBom = HasBom(localBytes);
        var returnedHasBom = HasBom(returnedBytes);
        if (localHasBom == returnedHasBom) return returnedBytes;
        if (!localHasBom) return returnedBytes[Bom.Length..];

        var result = new byte[Bom.Length + returnedBytes.Length];
        Bom.CopyTo(result, 0);
        returnedBytes.CopyTo(result, Bom.Length);
        return result;
    }
}

public static class SafePath
{
    public static string NormalizeRelative(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || Path.IsPathRooted(path) || path.Contains(':')) throw new InvalidDataException("Absolute paths are not allowed.");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(x => x is "." or "..")) throw new InvalidDataException("Path traversal is not allowed.");
        return string.Join('/', parts);
    }

    public static string UnderRoot(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes Source Root.");
        return full;
    }
}

public sealed class SettingsService
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SourceRelay");
    private string PathName => Path.Combine(DataRoot, "settings.json");
    public async Task<AppSettings> LoadAsync() => await JsonStore.ReadAsync<AppSettings>(PathName) ?? new();
    public Task SaveAsync(AppSettings settings) => JsonStore.WriteAtomicAsync(PathName, settings);
}

public sealed class BundleRecordStore
{
    private readonly string _folder;
    public BundleRecordStore(string? folder = null) => _folder = folder ?? Path.Combine(SettingsService.DataRoot, "Bundles");
    public Task SaveAsync(BundleRecord record) => JsonStore.WriteAtomicAsync(Path.Combine(_folder, record.Manifest.BundleId + ".json"), record);
    public Task<BundleRecord?> FindAsync(Guid id) => JsonStore.ReadAsync<BundleRecord>(Path.Combine(_folder, id + ".json"));
}

public sealed record SelectedFile(string SourceRootId, string FullPath, string RelativePath, FileMode Mode)
{
    public SelectedFile(string fullPath, string relativePath, FileMode mode) : this("", fullPath, relativePath, mode) { }
}
public sealed record BundleSourceRoot(string Id, string AbsolutePath, string DisplayName);
public sealed record BundleResult(BundleRecord Record, string ZipPath);

public sealed class BundleService(BundleRecordStore records)
{
    public const string Handoff = "Apply the changes we agreed above to the attached SourceRelay bundle. Follow INSTRUCTIONS.md for the bundle and return format.";
    public const string Instructions =
        "# SourceRelay Bundle Instructions\n\n" +
        "This archive was created by SourceRelay.\n\n" +
        "The user's conversation describes the coding task. These instructions describe how this archive must be handled.\n\n" +
        "## Files\n\n" +
        "Source files are stored under:\n\n" +
        "`files/<rootId>/<relative path>`\n\n" +
        "The manifest defines each Source Root and every allowed file.\n\n" +
        "Only files whose manifest entry has `\"mode\": \"editable\"` may be modified.\n\n" +
        "Files whose manifest entry has `\"mode\": \"context\"` are reference-only and must be returned byte-for-byte unchanged.\n\n" +
        "## Rules\n\n" +
        "1. Do not rename files.\n" +
        "2. Do not move files.\n" +
        "3. Do not move files between Source Roots.\n" +
        "4. Do not modify context-only files.\n" +
        "5. Do not create additional source files.\n" +
        "6. Preserve root IDs and relative paths exactly.\n" +
        "7. Do not modify `manifest.json`.\n" +
        "8. If the requested task requires another source file that is not included, ask the user to provide it.\n" +
        "9. Do not remove files from the bundle.\n\n" +
        "## Return format\n\n" +
        "When finished, return a ZIP containing:\n\n" +
        "- the original `manifest.json`, unchanged\n" +
        "- the complete `files/` tree from the original bundle\n\n" +
        "Include every original bundled file in the returned ZIP, even if you did not modify it.\n\n" +
        "Do not add, remove, rename, or move files.\n\n" +
        "Do not include `INSTRUCTIONS.md` in the returned ZIP.\n\n" +
        "The returned archive must preserve the original SourceRelay bundle ID and all relative file paths so SourceRelay can validate it.\n";

    public async Task<BundleResult> CreateAsync(IEnumerable<BundleSourceRoot> sourceRoots, IEnumerable<SelectedFile> selection, string outputFolder, CancellationToken ct = default)
    {
        var files = selection.ToList(); var roots = sourceRoots.ToList();
        if (files.Count == 0) throw new InvalidOperationException("Select at least one file.");
        var usedIds = files.Select(x => x.SourceRootId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        roots = roots.Where(x => usedIds.Contains(x.Id)).ToList();
        if (roots.Count != usedIds.Count || roots.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != roots.Count) throw new InvalidOperationException("Selection references an invalid Source Root.");
        if (roots.Any(x => SafePath.NormalizeRelative(x.Id) != x.Id || x.Id.Contains('/'))) throw new InvalidOperationException("Source Root IDs must be safe single path segments.");
        var manifest = new BundleManifest { FormatVersion = 2, BundleId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow,
            Roots = roots.Select(x => new BundleRoot { Id = x.Id, DisplayName = x.DisplayName }).ToList() };
        foreach (var selected in files)
        {
            ct.ThrowIfCancellationRequested();
            var root = roots.Single(x => x.Id == selected.SourceRootId);
            var relative = SafePath.NormalizeRelative(selected.RelativePath);
            var expected = SafePath.UnderRoot(root.AbsolutePath, relative);
            if (!string.Equals(Path.GetFullPath(selected.FullPath), expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected)) throw new InvalidOperationException($"Invalid selection: {relative}");
            var info = new FileInfo(expected);
            manifest.Files.Add(new() { RootId = root.Id, Path = relative, Mode = selected.Mode, Size = info.Length, Sha256 = await HashService.FileAsync(expected, ct) });
        }
        if (manifest.Files.Select(x => x.RootId + "\0" + x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count) throw new InvalidOperationException("Selection contains duplicate paths within a Source Root.");
        Directory.CreateDirectory(outputFolder);
        var zipPath = Path.Combine(outputFolder, $"SourceRelay_{manifest.BundleId}.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteText(zip, "INSTRUCTIONS.md", Instructions);
                WriteText(zip, "manifest.json", System.Text.Json.JsonSerializer.Serialize(manifest, JsonStore.Options));
                foreach (var file in manifest.Files) { var root = roots.Single(x => x.Id == file.RootId); zip.CreateEntryFromFile(SafePath.UnderRoot(root.AbsolutePath, file.Path), $"files/{file.RootId}/{file.Path}", CompressionLevel.Optimal); }
            }
            var record = new BundleRecord { Manifest = manifest, Roots = roots.Select(x => new LocalBundleRoot { Id = x.Id, AbsolutePath = Path.GetFullPath(x.AbsolutePath) }).ToList(), ArchivePath = zipPath };
            await records.SaveAsync(record);
            return new(record, zipPath);
        }
        catch { if (File.Exists(zipPath)) File.Delete(zipPath); throw; }
    }

    // Source-compatible convenience for integrations which previously supplied one root.
    public Task<BundleResult> CreateAsync(string root, IEnumerable<SelectedFile> selection, string outputFolder, CancellationToken ct = default)
    {
        const string id = "root";
        return CreateAsync([new(id, root, new DirectoryInfo(root).Name)], selection.Select(x => x with { SourceRootId = string.IsNullOrEmpty(x.SourceRootId) ? id : x.SourceRootId }), outputFolder, ct);
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(text); }
}

public static class DiffService
{
    public static string Create(byte[] current, byte[] returned)
    {
        if (current.Contains((byte)0) || returned.Contains((byte)0)) return "Binary file — textual comparison unavailable.";
        try
        {
            var left = Encoding.UTF8.GetString(current).Replace("\r\n", "\n").Split('\n');
            var right = Encoding.UTF8.GetString(returned).Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            { var a = i < left.Length ? left[i] : null; var b = i < right.Length ? right[i] : null; if (a == b) sb.AppendLine("  " + a); else { if (a is not null) sb.AppendLine("- " + a); if (b is not null) sb.AppendLine("+ " + b); } }
            return sb.ToString();
        }
        catch { return "Textual comparison unavailable."; }
    }
}
