using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SourceRelay.Models;
using SourceRelay.Services;

namespace SourceRelay.ViewModels;

public sealed class MainViewModel : Bindable, IDisposable
{
    private readonly SettingsService _settingsService = new(); private readonly BundleRecordStore _records = new();
    private readonly ApplyHistoryService _history; private readonly DownloadsMonitor _monitor = new();
    private AppSettings _settings = new(); private ReturnedBundle? _returned; private ChangeItem? _selectedChange;
    private readonly SourceSelectionService _selection = new();
    private string _status = "Paste an absolute file or folder path to begin.", _lastBundlePath = "", _search = "", _sourcePath = ""; private bool _busy;
    public ObservableCollection<FileNode> ProjectFiles { get; } = []; public ObservableCollection<ChangeItem> Changes { get; } = []; public ObservableCollection<string> Activity { get; } = [];
    public ObservableCollection<SelectedFileItem> SelectedFileItems => _selection.Files;
    public event EventHandler<string>? NavigationRequested;
    public IReadOnlyList<HistoryEntry> History => _history.Entries.Reverse().ToList();
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LastBundlePath { get => _lastBundlePath; private set { if (Set(ref _lastBundlePath, value)) Changed(nameof(HasBundle)); } }
    public bool HasBundle => !string.IsNullOrEmpty(LastBundlePath);
    public bool HasReturn => _returned is not null;
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplySearch(); } }
    public string SourcePath { get => _sourcePath; set => Set(ref _sourcePath, value); }
    public string SelectedFilesHeading => $"Selected files ({SelectedFileItems.Count})";
    public ChangeItem? SelectedChange { get => _selectedChange; set { if (Set(ref _selectedChange, value)) Changed(nameof(SelectedDiff)); } }
    public string SelectedDiff => SelectedChange?.Change.Diff ?? "Select a changed file to view its comparison.";
    public string SelectionSummary { get { var files = SelectedFiles().ToList(); return $"Editable: {files.Count(x => x.Mode == Models.FileMode.Editable)}   Context-only: {files.Count(x => x.Mode == Models.FileMode.Context)}   Size: {files.Sum(x => new FileInfo(x.FullPath).Length):N0} bytes"; } }

    public MainViewModel()
    {
        _history = new ApplyHistoryService(records: _records);
        _selection.Changed += (_, _) => { foreach (var root in ProjectFiles) root.RefreshFromSelection(); Changed(nameof(SelectedFilesHeading)); Changed(nameof(SelectionSummary)); };
        _selection.RootsChanged += (_, _) => RebuildRoots();
    }

    public async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync(); await _history.LoadAsync(); Changed(nameof(History));
        _selection.SetSettings(_settings);
        IEnumerable<PersistedSourceRoot> persisted = _settings.SourceRoots.Count > 0 ? _settings.SourceRoots : Directory.Exists(_settings.LastProjectRoot) ? new[] { new PersistedSourceRoot { AbsolutePath = _settings.LastProjectRoot } } : [];
        foreach (var root in persisted.Where(x => Directory.Exists(x.AbsolutePath))) _selection.AddRoot(root.AbsolutePath, string.IsNullOrWhiteSpace(root.DisplayName) ? null : root.DisplayName);
        if (_settings.MonitorDownloads) { _monitor.ZipReady += async path => await Application.Current.Dispatcher.InvokeAsync(async () => { var candidate = await new ReturnedBundleService(_records).ValidateAsync(path); if (candidate.Record.Manifest.BundleId != Guid.Empty) { await LoadReturnedAsync(path); AddActivity("Matching returned bundle detected"); } }); _monitor.Start(_settings.DownloadsFolder); }
    }
    public async Task AddSourceRootAsync(string root)
    {
        _selection.AddRoot(root); await PersistRootsAsync(); Status = "Source Root added.";
    }
    public Task ChooseProjectAsync(string root) => AddSourceRootAsync(root);
    private void RebuildRoots() { ProjectFiles.Clear(); foreach (var root in _selection.Roots) ProjectFiles.Add(new ProjectScanner(_settings, _selection).ScanRoot(root)); Changed(nameof(SelectionSummary)); }
    private async Task PersistRootsAsync() { _settings.SourceRoots = _selection.Roots.Select(x => new PersistedSourceRoot { AbsolutePath = x.AbsolutePath, DisplayName = x.DisplayName }).ToList(); await _settingsService.SaveAsync(_settings); }
    public bool AddSourcePath()
    {
        var result = _selection.AddPath(SourcePath); Status = result.Message;
        if (result.NavigationPath is not null) NavigateTo(result.NavigationPath, result.SourceRootId);
        if (result.Status is AddPathStatus.Added or AddPathStatus.AlreadySelected) { SourcePath = ""; _ = PersistRootsAsync(); return true; }
        return false;
    }
    public void NavigateTo(string path) => NavigateTo(path, null);
    private void NavigateTo(string path, string? sourceRootId)
    {
        var owner = sourceRootId is null ? _selection.OwningRoot(path) : _selection.Root(sourceRootId); if (owner is null) return;
        Search = ""; var relative = Path.GetRelativePath(owner.AbsolutePath, Path.GetFullPath(path));
        var rootNode = ProjectFiles.FirstOrDefault(x => x.SourceRootId == owner.Id); if (rootNode is null) return; rootNode.IsExpanded = true;
        var parts = relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); var nodes = rootNode.Children; FileNode? target = rootNode;
        foreach (var part in parts) { target = nodes.FirstOrDefault(x => string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase)); if (target is null) return; if (target.IsDirectory) { target.IsExpanded = true; nodes = target.Children; } }
        foreach (var root in ProjectFiles) ClearTreeSelection(root); target!.IsSelected = true; NavigationRequested?.Invoke(this, target.FullPath);
    }
    public void NavigateTo(SelectedFileItem? item) { if (item is not null) NavigateTo(item.FullPath, item.SourceRootId); }
    public void RemoveSelected(SelectedFileItem? item) { if (item is not null) _selection.Remove(item.FullPath); }
    public int SelectedCountForRoot(FileNode? node) => node is null ? 0 : SelectedFileItems.Count(x => x.SourceRootId == node.SourceRootId);
    public async Task RemoveSourceRootAsync(FileNode? node) { if (node is null || !node.IsSourceRoot) return; _selection.RemoveRoot(node.SourceRootId); await PersistRootsAsync(); Status = "Source Root removed from the workspace."; }
    private static void ClearTreeSelection(FileNode node) { node.IsSelected = false; foreach (var child in node.Children) ClearTreeSelection(child); }
    public async Task GenerateAsync()
    {
        if (IsBusy) return; IsBusy = true;
        try { var roots = _selection.Roots.Select(x => new BundleSourceRoot(x.Id, x.AbsolutePath, x.DisplayName)); var result = await new BundleService(_records).CreateAsync(roots, SelectedFiles(), _settings.BundleOutputFolder); LastBundlePath = result.ZipPath; Clipboard.SetText(BundleService.Handoff); Status = "Bundle created. Chat handoff message copied to clipboard."; AddActivity($"Bundle generated — {result.Record.Manifest.BundleId.ToString()[..8]}"); }
        catch (Exception ex) { Status = "Bundle generation failed: " + ex.Message; } finally { IsBusy = false; }
    }
    public async Task LoadReturnedAsync(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)) { Status = "Drop or open a ZIP file."; return; }
        IsBusy = true; try { _returned = await new ReturnedBundleService(_records).ValidateAsync(path); Changes.Clear(); foreach (var change in _returned.Changes) Changes.Add(new ChangeItem { Change = change, Apply = change.Apply }); Changed(nameof(HasReturn)); Status = _returned.IsValid ? $"{Changes.Count(x => x.CanApply)} file(s) ready to review." : string.Join(" ", _returned.Errors); AddActivity("Returned bundle opened — " + Path.GetFileName(path)); } finally { IsBusy = false; }
    }
    public async Task ApplyAsync()
    {
        if (_returned is null) return; foreach (var item in Changes) item.Change.Apply = item.Apply;
        var result = await _history.ApplyAsync(_returned, _returned.Changes, _settings.MaxHistoryEntries); Status = result.Message; AddActivity(result.Success ? $"Apply completed — {result.FileCount} file(s)" : result.Message); Changed(nameof(History));
    }
    public async Task UndoAsync(bool force = false) { var r = await _history.UndoAsync(force); Status = r.Message; AddActivity(r.Message); Changed(nameof(History)); }
    public async Task RedoAsync(bool force = false) { var r = await _history.RedoAsync(force); Status = r.Message; AddActivity(r.Message); Changed(nameof(History)); }
    public void Dismiss() { _returned = null; Changes.Clear(); Changed(nameof(HasReturn)); Status = "Returned bundle dismissed."; }
    public void OpenOutputFolder() { if (File.Exists(LastBundlePath)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastBundlePath}\"") { UseShellExecute = true }); }
    public void RefreshSelection() => Changed(nameof(SelectionSummary));
    private IEnumerable<SelectedFile> SelectedFiles() => _selection.Snapshot();
    private static IEnumerable<FileNode> Flatten(FileNode node) { yield return node; if (node.IsDirectory) { if (node.Included == true) node.LoadChildren(); foreach (var child in node.Children) foreach (var nested in Flatten(child)) yield return nested; } }
    private void ApplySearch() { if (string.IsNullOrWhiteSpace(Search)) { Status = "Select only the files you intend to share."; return; } var count = ProjectFiles.SelectMany(Flatten).Count(x => !x.IsDirectory && x.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase)); Status = $"{count} file(s) match ‘{Search}’. Expand folders to inspect matches."; }
    private void AddActivity(string text) { Activity.Insert(0, $"{DateTime.Now:t}  {text}"); }
    public void Dispose() => _monitor.Dispose();
}
