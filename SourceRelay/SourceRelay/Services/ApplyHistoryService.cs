using System.IO;
using SourceRelay.Models;

namespace SourceRelay.Services;

public sealed record OperationResult(bool Success, bool Conflict, string Message, int FileCount = 0);

public sealed class ApplyHistoryService
{
    private readonly string _root;
    private readonly string _index;
    private readonly BundleRecordStore? _records;
    private List<HistoryEntry> _entries = [];
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public ApplyHistoryService(string? root = null, BundleRecordStore? records = null) { _root = root ?? Path.Combine(SettingsService.DataRoot, "History"); _index = Path.Combine(_root, "history.json"); _records = records; }
    public async Task LoadAsync() => _entries = await JsonStore.ReadAsync<List<HistoryEntry>>(_index) ?? [];

    public async Task<OperationResult> ApplyAsync(ReturnedBundle bundle, IEnumerable<ReturnedChange> requested, int retention = 20)
    {
        var selected = requested.Where(x => x.Apply && x.CanApply && x.ReturnedBytes is not null).ToList();
        if (!bundle.IsValid || selected.Count == 0) return new(false, false, "No valid changes selected.");
        foreach (var change in selected)
        {
            var root = bundle.Record.RootPath(change.RootId); if (string.IsNullOrEmpty(root)) return new(false, false, $"Source Root is not mapped: {change.RootId}");
            var destination = SafePath.UnderRoot(root, change.Path);
            if (!File.Exists(destination)) return new(false, false, $"Destination is missing: {change.Path}");
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) return new(false, false, $"{change.Path} is read-only. Check it out in Perforce before applying.");
        }
        var entry = new HistoryEntry { BundleId = bundle.BundleId, ProjectRoot = bundle.Record.ProjectRoot };
        var folder = Path.Combine(_root, entry.Id.ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            foreach (var change in selected)
            {
                var absoluteRoot = bundle.Record.RootPath(change.RootId)!; var destination = SafePath.UnderRoot(absoluteRoot, change.Path); var before = await File.ReadAllBytesAsync(destination); var after = Utf8BomService.Preserve(before, change.ReturnedBytes!);
                var item = new HistoryFile { RootId = change.RootId, AbsoluteRoot = absoluteRoot, RelativePath = change.Path, BeforeFile = Path.Combine(folder, entry.Files.Count + ".before"), AfterFile = Path.Combine(folder, entry.Files.Count + ".after"), BeforeHash = HashService.Bytes(before), AfterHash = HashService.Bytes(after) };
                await File.WriteAllBytesAsync(item.BeforeFile, before); await File.WriteAllBytesAsync(item.AfterFile, after); await ReplaceAsync(destination, after); entry.Files.Add(item);
                bundle.Record.SetExpectedLocalHash(change.RootId, change.Path, item.AfterHash);
                if (_records is not null) await _records.SaveAsync(bundle.Record);
            }
            _entries.RemoveAll(x => x.IsUndone); _entries.Add(entry);
            while (_entries.Count > retention) { var old = _entries[0]; _entries.RemoveAt(0); TryDelete(Path.GetDirectoryName(old.Files.FirstOrDefault()?.BeforeFile)); }
            await JsonStore.WriteAtomicAsync(_index, _entries);
            return new(true, false, $"Applied {entry.Files.Count} file(s).", entry.Files.Count);
        }
        catch (Exception ex) { if (entry.Files.Count > 0) { _entries.Add(entry); await JsonStore.WriteAtomicAsync(_index, _entries); } return new(false, false, $"Apply partially failed after {entry.Files.Count} file(s): {ex.Message}", entry.Files.Count); }
    }

    public Task<OperationResult> UndoAsync(bool overwriteConflict = false) => MoveAsync(undo: true, overwriteConflict);
    public Task<OperationResult> RedoAsync(bool overwriteConflict = false) => MoveAsync(undo: false, overwriteConflict);
    private async Task<OperationResult> MoveAsync(bool undo, bool overwrite)
    {
        var entry = undo ? _entries.LastOrDefault(x => !x.IsUndone) : _entries.FirstOrDefault(x => x.IsUndone);
        if (entry is null) return new(false, false, undo ? "Nothing to undo." : "Nothing to redo.");
        foreach (var file in entry.Files)
        {
            var destination = SafePath.UnderRoot(string.IsNullOrEmpty(file.AbsoluteRoot) ? entry.ProjectRoot : file.AbsoluteRoot, file.RelativePath);
            if (!File.Exists(destination)) return new(false, true, $"File is missing: {file.RelativePath}");
            var expected = undo ? file.AfterHash : file.BeforeHash;
            if (!string.Equals(await HashService.FileAsync(destination), expected, StringComparison.OrdinalIgnoreCase) && !overwrite) return new(false, true, $"{file.RelativePath} changed externally. Confirm overwrite to continue.");
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) return new(false, false, $"{file.RelativePath} is read-only.");
        }
        foreach (var file in entry.Files)
        {
            await ReplaceAsync(SafePath.UnderRoot(string.IsNullOrEmpty(file.AbsoluteRoot) ? entry.ProjectRoot : file.AbsoluteRoot, file.RelativePath), await File.ReadAllBytesAsync(undo ? file.BeforeFile : file.AfterFile));
            await SetExpectedLocalHashAsync(entry.BundleId, file, undo ? file.BeforeHash : file.AfterHash);
        }
        entry.IsUndone = undo; await JsonStore.WriteAtomicAsync(_index, _entries);
        return new(true, false, $"{(undo ? "Undo" : "Redo")} completed.", entry.Files.Count);
    }
    private static async Task ReplaceAsync(string destination, byte[] bytes)
    { var temp = destination + ".sourcerelay.tmp"; await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, destination, true); }
    private async Task SetExpectedLocalHashAsync(Guid bundleId, HistoryFile file, string hash)
    {
        if (_records is null) return;
        var record = await _records.FindAsync(bundleId);
        if (record is null) return;
        record.SetExpectedLocalHash(file.RootId, file.RelativePath, hash);
        await _records.SaveAsync(record);
    }
    private static void TryDelete(string? path) { try { if (path is not null) Directory.Delete(path, true); } catch { } }
}
