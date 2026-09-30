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
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes project root.");
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

public sealed record SelectedFile(string FullPath, string RelativePath, FileMode Mode);
public sealed record BundleResult(BundleRecord Record, string ZipPath);

public sealed class BundleService(BundleRecordStore records)
{
    public const string Handoff = "Apply the changes we agreed above to the attached SourceRelay bundle. Follow INSTRUCTIONS.md for the bundle and return format.";
    public const string Instructions =
        "# SourceRelay Bundle Instructions\n\n" +
        "This archive was created by SourceRelay.\n\n" +
        "The user's conversation describes the coding task. These instructions describe how this archive must be handled.\n\n" +
        "## Files\n\n" +
        "Files are under `files/`.\n\n" +
        "The manifest records each file as either:\n\n" +
        "- `editable`\n" +
        "- `context`\n\n" +
        "Only files whose manifest entry has `\"mode\": \"editable\"` may be modified.\n\n" +
        "Files whose manifest entry has `\"mode\": \"context\"` are reference-only and must be returned byte-for-byte unchanged.\n\n" +
        "## Rules\n\n" +
        "1. Do not rename files.\n" +
        "2. Do not move files.\n" +
        "3. Do not modify context-only files.\n" +
        "4. Do not create additional source files.\n" +
        "5. Preserve all relative paths exactly.\n" +
        "6. Do not modify `manifest.json`.\n" +
        "7. If the requested change requires another source file that is not in the bundle, ask the user to provide it rather than inventing its contents.\n" +
        "8. Do not remove any files from the bundle.\n\n" +
        "## Return format\n\n" +
        "When finished, return a ZIP containing:\n\n" +
        "- the original `manifest.json`, unchanged\n" +
        "- the complete `files/` tree from the original bundle\n\n" +
        "Include every original bundled file in the returned ZIP, even if you did not modify it.\n\n" +
        "Do not add, remove, rename, or move files.\n\n" +
        "Do not include `INSTRUCTIONS.md` in the returned ZIP.\n\n" +
        "The returned archive must preserve the original SourceRelay bundle ID and all relative file paths so SourceRelay can validate it.\n";

    public async Task<BundleResult> CreateAsync(string projectRoot, IEnumerable<SelectedFile> selection, string outputFolder, CancellationToken ct = default)
    {
        var files = selection.ToList();
        if (files.Count == 0) throw new InvalidOperationException("Select at least one file.");
        var manifest = new BundleManifest { BundleId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow, ProjectName = new DirectoryInfo(projectRoot).Name };
        foreach (var selected in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = SafePath.NormalizeRelative(selected.RelativePath);
            var expected = SafePath.UnderRoot(projectRoot, relative);
            if (!string.Equals(Path.GetFullPath(selected.FullPath), expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected)) throw new InvalidOperationException($"Invalid selection: {relative}");
            var info = new FileInfo(expected);
            manifest.Files.Add(new() { Path = relative, Mode = selected.Mode, Size = info.Length, Sha256 = await HashService.FileAsync(expected, ct) });
        }
        if (manifest.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count) throw new InvalidOperationException("Selection contains duplicate paths.");
        Directory.CreateDirectory(outputFolder);
        var zipPath = Path.Combine(outputFolder, $"SourceRelay_{manifest.BundleId}.zip");
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                WriteText(zip, "INSTRUCTIONS.md", Instructions);
                WriteText(zip, "manifest.json", System.Text.Json.JsonSerializer.Serialize(manifest, JsonStore.Options));
                foreach (var file in manifest.Files) zip.CreateEntryFromFile(SafePath.UnderRoot(projectRoot, file.Path), "files/" + file.Path, CompressionLevel.Optimal);
            }
            var record = new BundleRecord { Manifest = manifest, ProjectRoot = Path.GetFullPath(projectRoot), ArchivePath = zipPath };
            await records.SaveAsync(record);
            return new(record, zipPath);
        }
        catch { if (File.Exists(zipPath)) File.Delete(zipPath); throw; }
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
