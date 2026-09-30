using System.Collections.ObjectModel;
using System.IO;
using SourceRelay.Models;
using SourceRelay.ViewModels;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Services;

public enum AddPathStatus { Added, AlreadySelected, Invalid, Missing, OutsideRoot, Excluded }
public sealed record AddPathResult(AddPathStatus Status, string Message, string? NavigationPath = null, int AddedCount = 0, string? SourceRootId = null);

public sealed class SourceRoot
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string AbsolutePath { get; init; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>Owns the independent roots and the unambiguous (root ID, relative path) selection.</summary>
public sealed class SourceSelectionService
{
    private readonly Dictionary<string, SelectedFileItem> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceRoot> _roots = [];
    private AppSettings _settings = new();
    public ObservableCollection<SelectedFileItem> Files { get; } = [];
    public IReadOnlyList<SourceRoot> Roots => _roots;
    public event EventHandler? Changed;
    public event EventHandler? RootsChanged;

    public void SetSettings(AppSettings settings) => _settings = settings;
    public void Configure(string root, AppSettings settings) { Clear(); _settings = settings; AddRoot(root); }
    public void Clear() { _byPath.Clear(); Files.Clear(); _roots.Clear(); Changed?.Invoke(this, EventArgs.Empty); RootsChanged?.Invoke(this, EventArgs.Empty); }

    public SourceRoot AddRoot(string path, string? displayName = null, bool allowOverlap = true)
    {
        path = Canonical(path);
        var existing = _roots.FirstOrDefault(x => Same(x.AbsolutePath, path));
        if (existing is not null) return existing;
        var root = new SourceRoot { AbsolutePath = path, DisplayName = displayName ?? new DirectoryInfo(path).Name };
        _roots.Add(root); DisambiguateNames(); RootsChanged?.Invoke(this, EventArgs.Empty); return root;
    }

    public bool RemoveRoot(string id)
    {
        var root = Root(id); if (root is null) return false;
        foreach (var item in Files.Where(x => x.SourceRootId == id).ToList()) { _byPath.Remove(item.FullPath); Files.Remove(item); }
        _roots.Remove(root); DisambiguateNames(); RootsChanged?.Invoke(this, EventArgs.Empty); Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    public SourceRoot? Root(string id) => _roots.FirstOrDefault(x => x.Id == id);
    public SourceRoot? OwningRoot(string path) => _roots.Where(x => IsUnder(x.AbsolutePath, path)).OrderByDescending(x => x.AbsolutePath.Length).FirstOrDefault();
    public bool Contains(string path) => _byPath.ContainsKey(Canonical(path));
    public SelectedFileItem? Find(string path) => _byPath.GetValueOrDefault(Canonical(path));

    public AddPathResult AddPath(string input)
    {
        var normalized = Normalize(input);
        if (normalized is null) return new(AddPathStatus.Invalid, "Enter an absolute file or directory path.");
        var isFile = File.Exists(normalized); var isDirectory = Directory.Exists(normalized);
        if (!isFile && !isDirectory) return new(AddPathStatus.Missing, "Path does not exist.");
        var root = OwningRoot(normalized) ?? AddRoot(SourceRootInference.Infer(normalized, isFile));
        if (ProjectScanner.IsExcluded(normalized, root.AbsolutePath, _settings, out var reason)) return new(AddPathStatus.Excluded, $"This path is excluded by the current SourceRelay rules ({reason}).", SourceRootId: root.Id);
        if (isFile)
        {
            if (_byPath.ContainsKey(normalized)) return new(AddPathStatus.AlreadySelected, "Already selected — navigated to file.", normalized, SourceRootId: root.Id);
            AddFile(root.Id, normalized, FileMode.Editable); return new(AddPathStatus.Added, $"Added {Path.GetFileName(normalized)}.", normalized, 1, root.Id);
        }
        var count = 0;
        foreach (var file in EligibleFiles(normalized, root)) if (!_byPath.ContainsKey(file)) { AddFile(root.Id, file, FileMode.Editable); count++; }
        return new(AddPathStatus.Added, $"Added {count} files from directory.", normalized, count, root.Id);
    }

    public SelectedFileItem AddFile(string rootId, string path, FileMode mode)
    {
        path = Canonical(path); var root = Root(rootId) ?? throw new ArgumentException("Unknown Source Root.", nameof(rootId));
        if (!IsUnder(root.AbsolutePath, path)) throw new InvalidOperationException("File is outside its Source Root.");
        if (_byPath.TryGetValue(path, out var existing)) return existing;
        var item = new SelectedFileItem(root.Id, root.DisplayName, path, Path.GetRelativePath(root.AbsolutePath, path).Replace('\\', '/'), mode, this);
        _byPath.Add(path, item); Files.Add(item); Changed?.Invoke(this, EventArgs.Empty); return item;
    }
    public SelectedFileItem AddFile(string path, FileMode mode) { var root = OwningRoot(path) ?? throw new InvalidOperationException("No Source Root owns this file."); return AddFile(root.Id, path, mode); }
    public void Remove(string path) { path = Canonical(path); if (!_byPath.Remove(path, out var item)) return; Files.Remove(item); Changed?.Invoke(this, EventArgs.Empty); }
    internal void ItemChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public IReadOnlyList<SelectedFile> Snapshot() => Files.Select(x => new SelectedFile(x.SourceRootId, x.FullPath, x.RelativePath, x.Mode)).ToList();
    public string? Normalize(string input)
    {
        var value = input.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1].Trim();
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return null;
        try { return Canonical(value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)); } catch { return null; }
    }
    private IEnumerable<string> EligibleFiles(string directory, SourceRoot root)
    {
        IEnumerable<string> entries; try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); } catch { yield break; }
        foreach (var entry in entries)
        {
            if (!IsUnder(root.AbsolutePath, entry) || ProjectScanner.IsExcluded(entry, root.AbsolutePath, _settings, out _)) continue;
            if (Directory.Exists(entry)) { foreach (var file in EligibleFiles(entry, root)) yield return file; } else if (File.Exists(entry)) yield return Canonical(entry);
        }
    }
    private void DisambiguateNames()
    {
        foreach (var group in _roots.GroupBy(x => new DirectoryInfo(x.AbsolutePath).Name, StringComparer.OrdinalIgnoreCase))
            foreach (var root in group) root.DisplayName = group.Count() == 1 ? new DirectoryInfo(root.AbsolutePath).Name : $"{new DirectoryInfo(root.AbsolutePath).Name} ({Directory.GetParent(root.AbsolutePath)?.FullName})";
        foreach (var file in Files) file.RefreshRootName(Root(file.SourceRootId)?.DisplayName ?? "");
    }
    internal static bool IsUnder(string root, string path) { var relative = Path.GetRelativePath(Canonical(root), Canonical(path)); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathFullyQualified(relative); }
    internal static string Canonical(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Same(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);
}

public static class SourceRootInference
{
    public static string Infer(string path, bool? isFile = null)
    {
        path = Path.GetFullPath(path); var file = isFile ?? File.Exists(path); var start = file ? Path.GetDirectoryName(path)! : path;
        var ancestors = Ancestors(start).ToList();
        var assets = ancestors.FirstOrDefault(x => string.Equals(Path.GetFileName(x), "Assets", StringComparison.OrdinalIgnoreCase));
        if (assets is not null && Directory.GetParent(assets) is { } unity) return unity.FullName;
        var solution = ancestors.FirstOrDefault(x => Contains(x, "*.sln")); if (solution is not null) return solution;
        var project = ancestors.FirstOrDefault(x => Contains(x, "*.csproj")); if (project is not null) return project;
        return start;
    }
    private static IEnumerable<string> Ancestors(string start) { for (var current = new DirectoryInfo(start); current is not null; current = current.Parent) yield return current.FullName; }
    private static bool Contains(string directory, string pattern) { try { return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any(); } catch { return false; } }
}

public sealed class SelectedFileItem : Bindable
{
    private FileMode _mode; private string _rootName; private readonly SourceSelectionService _owner;
    internal SelectedFileItem(string sourceRootId, string rootName, string fullPath, string relativePath, FileMode mode, SourceSelectionService owner) { SourceRootId = sourceRootId; _rootName = rootName; FullPath = fullPath; RelativePath = relativePath; _mode = mode; _owner = owner; }
    public string SourceRootId { get; }
    public string RootName => _rootName;
    public string FullPath { get; }
    public string RelativePath { get; }
    public string Name => Path.GetFileName(FullPath);
    public string DirectoryContext => $"{RootName} / {RelativePath}";
    public FileMode Mode { get => _mode; set { if (Set(ref _mode, value)) _owner.ItemChanged(); } }
    internal void RefreshRootName(string value) { _rootName = value; Changed(nameof(RootName)); Changed(nameof(DirectoryContext)); }
}
