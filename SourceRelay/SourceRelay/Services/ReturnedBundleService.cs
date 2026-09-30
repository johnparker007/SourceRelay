using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SourceRelay.Models;

namespace SourceRelay.Services;

public sealed class ReturnedBundleService(BundleRecordStore records)
{
    public const int MaxEntries = 5000;
    public const long MaxExpandedBytes = 250L * 1024 * 1024;

    public async Task<ReturnedBundle> ValidateAsync(string zipPath, CancellationToken ct = default)
    {
        var result = new ReturnedBundle { ArchivePath = zipPath };
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Count > MaxEntries || zip.Entries.Sum(x => x.Length) > MaxExpandedBytes) return Error(result, "Archive exceeds safety limits.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var normalized = SafePath.NormalizeRelative(entry.FullName);
                if (!names.Add(normalized)) return Error(result, "Duplicate or case-colliding archive path: " + normalized);
            }
            var manifests = zip.Entries.Where(x => string.Equals(x.FullName.Replace('\\', '/'), "manifest.json", StringComparison.OrdinalIgnoreCase)).ToList();
            if (manifests.Count != 1) return Error(result, "A single manifest.json is required.");
            var manifestBytes = await ReadLimitedAsync(manifests[0], ct);
            var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestBytes, JsonStore.Options) ?? throw new InvalidDataException("Manifest is empty.");
            if (manifest.Format != "SourceRelay" || manifest.FormatVersion != 1 || manifest.BundleId == Guid.Empty) return Error(result, "Unsupported SourceRelay manifest.");
            result.BundleId = manifest.BundleId;
            result.Record = await records.FindAsync(manifest.BundleId) ?? new();
            if (string.IsNullOrEmpty(result.Record.ProjectRoot)) return Error(result, "Bundle ID is not recognised on this computer.");
            if (!ManifestMatches(manifest, result.Record.Manifest)) return Error(result, "Returned manifest does not match the original bundle.");
            var originalManifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result.Record.Manifest, JsonStore.Options));
            if (!manifestBytes.SequenceEqual(originalManifestBytes)) return Error(result, "Returned manifest.json is not byte-for-byte unchanged.");
            var expected = result.Record.Manifest.Files.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var returnedEntries = zip.Entries.Where(x => x.FullName.Replace('\\', '/').StartsWith("files/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Name)).ToList();
            foreach (var entry in returnedEntries)
            {
                var relative = SafePath.NormalizeRelative(entry.FullName.Replace('\\', '/')[6..]);
                if (!expected.TryGetValue(relative, out var original)) { result.Changes.Add(new() { Path = relative, Kind = ChangeKind.Unexpected, Message = "File was not in the original bundle." }); continue; }
                if (!string.Equals(relative, original.Path, StringComparison.Ordinal)) { result.Changes.Add(new() { Path = relative, Kind = ChangeKind.Unexpected, Message = "File path casing does not match the original bundle." }); continue; }
                var bytes = await ReadLimitedAsync(entry, ct);
                var returnedHash = HashService.Bytes(bytes);
                var localPath = SafePath.UnderRoot(result.Record.ProjectRoot, relative);
                var localBytes = File.Exists(localPath) ? await File.ReadAllBytesAsync(localPath, ct) : [];
                var localHash = HashService.Bytes(localBytes);
                var kind = returnedHash == original.Sha256 ? ChangeKind.Unchanged
                    : original.Mode == Models.FileMode.Context ? ChangeKind.ContextOnlyModified
                    : localHash != original.Sha256 ? ChangeKind.LocalFileChanged : ChangeKind.Modified;
                result.Changes.Add(new() { Path = original.Path, Kind = kind, ReturnedBytes = bytes, Apply = kind == ChangeKind.Modified, Diff = DiffService.Create(localBytes, bytes), Message = Describe(kind) });
            }
            foreach (var missing in expected.Values.Where(x => !returnedEntries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), "files/" + x.Path, StringComparison.Ordinal))))
                result.Changes.Add(new() { Path = missing.Path, Kind = ChangeKind.Missing, Message = "File is missing from the returned bundle." });
            if (result.Changes.Any(x => x.Kind is ChangeKind.Unexpected or ChangeKind.ContextOnlyModified or ChangeKind.Missing)) result.Errors.Add("Returned bundle contains prohibited or missing files.");
            result.Record.State = BundleState.Reviewed;
            await records.SaveAsync(result.Record);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
        { Error(result, "Invalid returned ZIP: " + ex.Message); }
        return result;
    }

    private static bool ManifestMatches(BundleManifest a, BundleManifest b) => a.BundleId == b.BundleId && a.Format == b.Format && a.FormatVersion == b.FormatVersion &&
        a.Files.Count == b.Files.Count && a.Files.All(x => b.Files.Any(y => string.Equals(x.Path, y.Path, StringComparison.Ordinal) && x.Mode == y.Mode && x.Sha256 == y.Sha256 && x.Size == y.Size));
    private static ReturnedBundle Error(ReturnedBundle value, string error) { value.Errors.Add(error); return value; }
    private static string Describe(ChangeKind kind) => kind switch { ChangeKind.Modified => "Ready to apply.", ChangeKind.Unchanged => "Unchanged.", ChangeKind.LocalFileChanged => "Local file changed since export; confirmation is required.", ChangeKind.ContextOnlyModified => "Context-only files cannot be modified.", _ => kind.ToString() };
    private static async Task<byte[]> ReadLimitedAsync(ZipArchiveEntry entry, CancellationToken ct)
    { if (entry.Length > MaxExpandedBytes) throw new InvalidDataException("Entry is too large."); await using var input = entry.Open(); using var output = new MemoryStream(); await input.CopyToAsync(output, ct); return output.ToArray(); }
}
