using System.Collections.ObjectModel;
using System.IO;
using SourceRelay.Models;
using SourceRelay.ViewModels;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Services;

public enum AddPathStatus { Added, AlreadySelected, Invalid, Missing, OutsideRoot, Excluded }
public sealed record AddPathResult(AddPathStatus Status, string Message, string? NavigationPath = null, int AddedCount = 0);

/// <summary>The single source of truth for files included in the next bundle.</summary>
public sealed class SourceSelectionService
{
    private readonly Dictionary<string, SelectedFileItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private string _root = "";
    private AppSettings _settings = new();
    public ObservableCollection<SelectedFileItem> Files { get; } = [];
    public event EventHandler? Changed;

    public void Configure(string root, AppSettings settings) { _root = Path.GetFullPath(root); _settings = settings; _byPath.Clear(); Files.Clear(); Changed?.Invoke(this, EventArgs.Empty); }
    public bool Contains(string path) => _byPath.ContainsKey(Path.GetFullPath(path));
    public SelectedFileItem? Find(string path) => _byPath.GetValueOrDefault(Path.GetFullPath(path));

    public AddPathResult AddPath(string input)
    {
        var normalized = Normalize(input);
        if (normalized is null) return new(AddPathStatus.Invalid, "Enter an absolute file or directory path.");
        if (!File.Exists(normalized) && !Directory.Exists(normalized)) return new(AddPathStatus.Missing, "Path does not exist.");
        if (!IsUnderRoot(normalized)) return new(AddPathStatus.OutsideRoot, "Path is outside the current project root.");
        if (ProjectScanner.IsExcluded(normalized, _root, _settings, out var reason)) return new(AddPathStatus.Excluded, $"This path is excluded by the current SourceRelay rules ({reason}).");
        if (File.Exists(normalized))
        {
            if (_byPath.ContainsKey(normalized)) return new(AddPathStatus.AlreadySelected, "Already selected — navigated to file.", normalized);
            AddFile(normalized, FileMode.Editable); return new(AddPathStatus.Added, $"Added {Path.GetFileName(normalized)}.", normalized, 1);
        }
        var count = 0;
        foreach (var file in EligibleFiles(normalized))
            if (!_byPath.ContainsKey(file)) { AddFile(file, FileMode.Editable); count++; }
        return new(AddPathStatus.Added, $"Added {count} files from directory.", normalized, count);
    }

    public SelectedFileItem AddFile(string path, FileMode mode)
    {
        path = Path.GetFullPath(path);
        if (_byPath.TryGetValue(path, out var existing)) return existing;
        var item = new SelectedFileItem(path, Path.GetRelativePath(_root, path).Replace('\\', '/'), mode, this);
        _byPath.Add(path, item); Files.Add(item); Changed?.Invoke(this, EventArgs.Empty); return item;
    }
    public void Remove(string path) { path = Path.GetFullPath(path); if (!_byPath.Remove(path, out var item)) return; Files.Remove(item); Changed?.Invoke(this, EventArgs.Empty); }
    internal void ItemChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public IReadOnlyList<SelectedFile> Snapshot() => Files.Select(x => new SelectedFile(x.FullPath, x.RelativePath, x.Mode)).ToList();
    public string? Normalize(string input)
    {
        var value = input.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try { return Path.GetFullPath(value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)); } catch { return null; }
    }
    private bool IsUnderRoot(string path) { var relative = Path.GetRelativePath(_root, path); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathFullyQualified(relative); }
    private IEnumerable<string> EligibleFiles(string directory)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); } catch { yield break; }
        foreach (var entry in entries)
        {
            if (ProjectScanner.IsExcluded(entry, _root, _settings, out _)) continue;
            if (Directory.Exists(entry)) { foreach (var file in EligibleFiles(entry)) yield return file; }
            else if (File.Exists(entry)) yield return entry;
        }
    }
}

public sealed class SelectedFileItem : Bindable
{
    private FileMode _mode; private readonly SourceSelectionService _owner;
    internal SelectedFileItem(string fullPath, string relativePath, FileMode mode, SourceSelectionService owner) { FullPath = fullPath; RelativePath = relativePath; _mode = mode; _owner = owner; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public string Name => Path.GetFileName(FullPath);
    public string DirectoryContext => Path.GetDirectoryName(RelativePath)?.Replace('\\', '/') ?? "";
    public FileMode Mode { get => _mode; set { if (Set(ref _mode, value)) _owner.ItemChanged(); } }
}
