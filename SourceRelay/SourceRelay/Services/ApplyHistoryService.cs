using System.IO;
using SourceRelay.Models;

namespace SourceRelay.Services;

public sealed record OperationResult(bool Success, bool Conflict, string Message, int FileCount = 0);

public sealed class ApplyHistoryService
{
    private readonly string _root;
    private readonly string _index;
    private List<HistoryEntry> _entries = [];
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public ApplyHistoryService(string? root = null) { _root = root ?? Path.Combine(SettingsService.DataRoot, "History"); _index = Path.Combine(_root, "history.json"); }
    public async Task LoadAsync() => _entries = await JsonStore.ReadAsync<List<HistoryEntry>>(_index) ?? [];

    public async Task<OperationResult> ApplyAsync(ReturnedBundle bundle, IEnumerable<ReturnedChange> requested, int retention = 20)
    {
        var selected = requested.Where(x => x.Apply && x.CanApply && x.ReturnedBytes is not null).ToList();
        if (!bundle.IsValid || selected.Count == 0) return new(false, false, "No valid changes selected.");
        foreach (var change in selected)
        {
            var destination = SafePath.UnderRoot(bundle.Record.ProjectRoot, change.Path);
            if (!File.Exists(destination)) return new(false, false, $"Destination is missing: {change.Path}");
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) return new(false, false, $"{change.Path} is read-only. Check it out in Perforce before applying.");
        }
        var entry = new HistoryEntry { BundleId = bundle.BundleId, ProjectRoot = bundle.Record.ProjectRoot };
        var folder = Path.Combine(_root, entry.Id.ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            foreach (var change in selected)
            {
                var destination = SafePath.UnderRoot(entry.ProjectRoot, change.Path); var before = await File.ReadAllBytesAsync(destination); var after = change.ReturnedBytes!;
                var item = new HistoryFile { RelativePath = change.Path, BeforeFile = Path.Combine(folder, entry.Files.Count + ".before"), AfterFile = Path.Combine(folder, entry.Files.Count + ".after"), BeforeHash = HashService.Bytes(before), AfterHash = HashService.Bytes(after) };
                await File.WriteAllBytesAsync(item.BeforeFile, before); await File.WriteAllBytesAsync(item.AfterFile, after); await ReplaceAsync(destination, after); entry.Files.Add(item);
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
            var destination = SafePath.UnderRoot(entry.ProjectRoot, file.RelativePath);
            if (!File.Exists(destination)) return new(false, true, $"File is missing: {file.RelativePath}");
            var expected = undo ? file.AfterHash : file.BeforeHash;
            if (!string.Equals(await HashService.FileAsync(destination), expected, StringComparison.OrdinalIgnoreCase) && !overwrite) return new(false, true, $"{file.RelativePath} changed externally. Confirm overwrite to continue.");
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0) return new(false, false, $"{file.RelativePath} is read-only.");
        }
        foreach (var file in entry.Files) await ReplaceAsync(SafePath.UnderRoot(entry.ProjectRoot, file.RelativePath), await File.ReadAllBytesAsync(undo ? file.BeforeFile : file.AfterFile));
        entry.IsUndone = undo; await JsonStore.WriteAtomicAsync(_index, _entries);
        return new(true, false, $"{(undo ? "Undo" : "Redo")} completed.", entry.Files.Count);
    }
    private static async Task ReplaceAsync(string destination, byte[] bytes)
    { var temp = destination + ".sourcerelay.tmp"; await File.WriteAllBytesAsync(temp, bytes); File.Move(temp, destination, true); }
    private static void TryDelete(string? path) { try { if (path is not null) Directory.Delete(path, true); } catch { } }
}
