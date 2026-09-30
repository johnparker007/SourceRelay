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
    private readonly ApplyHistoryService _history = new(); private readonly DownloadsMonitor _monitor = new();
    private AppSettings _settings = new(); private ReturnedBundle? _returned; private ChangeItem? _selectedChange;
    private string _projectRoot = "", _status = "Choose a project folder to begin.", _lastBundlePath = "", _search = ""; private bool _busy;
    public ObservableCollection<FileNode> ProjectFiles { get; } = []; public ObservableCollection<ChangeItem> Changes { get; } = []; public ObservableCollection<string> Activity { get; } = [];
    public IReadOnlyList<HistoryEntry> History => _history.Entries.Reverse().ToList();
    public string ProjectRoot { get => _projectRoot; private set => Set(ref _projectRoot, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string LastBundlePath { get => _lastBundlePath; private set { if (Set(ref _lastBundlePath, value)) Changed(nameof(HasBundle)); } }
    public bool HasBundle => !string.IsNullOrEmpty(LastBundlePath);
    public bool HasReturn => _returned is not null;
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) ApplySearch(); } }
    public ChangeItem? SelectedChange { get => _selectedChange; set { if (Set(ref _selectedChange, value)) Changed(nameof(SelectedDiff)); } }
    public string SelectedDiff => SelectedChange?.Change.Diff ?? "Select a changed file to view its comparison.";
    public string SelectionSummary { get { var files = SelectedFiles().ToList(); return $"Editable: {files.Count(x => x.Mode == Models.FileMode.Editable)}   Context-only: {files.Count(x => x.Mode == Models.FileMode.Context)}   Size: {files.Sum(x => new FileInfo(x.FullPath).Length):N0} bytes"; } }

    public async Task InitializeAsync()
    {
        _settings = await _settingsService.LoadAsync(); await _history.LoadAsync(); Changed(nameof(History));
        if (Directory.Exists(_settings.LastProjectRoot)) LoadProject(_settings.LastProjectRoot);
        if (_settings.MonitorDownloads) { _monitor.ZipReady += async path => await Application.Current.Dispatcher.InvokeAsync(async () => { var candidate = await new ReturnedBundleService(_records).ValidateAsync(path); if (candidate.Record.ProjectRoot.Length > 0) { await LoadReturnedAsync(path); AddActivity("Matching returned bundle detected"); } }); _monitor.Start(_settings.DownloadsFolder); }
    }
    public async Task ChooseProjectAsync(string root)
    {
        LoadProject(root); _settings.LastProjectRoot = root; _settings.RecentProjectRoots.RemoveAll(x => string.Equals(x, root, StringComparison.OrdinalIgnoreCase)); _settings.RecentProjectRoots.Insert(0, root); _settings.RecentProjectRoots = _settings.RecentProjectRoots.Take(10).ToList(); await _settingsService.SaveAsync(_settings);
    }
    private void LoadProject(string root) { ProjectRoot = Path.GetFullPath(root); ProjectFiles.Clear(); foreach (var node in new ProjectScanner(_settings).Scan(ProjectRoot)) ProjectFiles.Add(node); Status = "Select only the files you intend to share."; Changed(nameof(SelectionSummary)); }
    public async Task GenerateAsync()
    {
        if (IsBusy) return; IsBusy = true;
        try { var result = await new BundleService(_records).CreateAsync(ProjectRoot, SelectedFiles(), _settings.BundleOutputFolder); LastBundlePath = result.ZipPath; Clipboard.SetText(BundleService.Handoff); Status = "Bundle created. Chat handoff message copied to clipboard."; AddActivity($"Bundle generated — {result.Record.Manifest.BundleId.ToString()[..8]}"); }
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
    private IEnumerable<SelectedFile> SelectedFiles() => ProjectFiles.SelectMany(Flatten).Where(x => !x.IsDirectory && x.Included == true).Select(x => new SelectedFile(x.FullPath, x.RelativePath, x.Mode));
    private static IEnumerable<FileNode> Flatten(FileNode node) { yield return node; if (node.IsDirectory) { if (node.Included == true) node.LoadChildren(); foreach (var child in node.Children) foreach (var nested in Flatten(child)) yield return nested; } }
    private void ApplySearch() { if (string.IsNullOrWhiteSpace(Search)) { Status = "Select only the files you intend to share."; return; } var count = ProjectFiles.SelectMany(Flatten).Count(x => !x.IsDirectory && x.RelativePath.Contains(Search, StringComparison.OrdinalIgnoreCase)); Status = $"{count} file(s) match ‘{Search}’. Expand folders to inspect matches."; }
    private void AddActivity(string text) { Activity.Insert(0, $"{DateTime.Now:t}  {text}"); }
    public void Dispose() => _monitor.Dispose();
}
