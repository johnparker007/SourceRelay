using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SourceRelay.Models;
using SourceRelay.Services;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.ViewModels;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class FileNode : Bindable
{
    private bool? _included = false; private bool _expanded; private bool _loaded; private FileMode _mode; private bool _selected; private bool _updating;
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string SourceRootId { get; init; } = "";
    public string SourceRootPath { get; init; } = "";
    public bool IsSourceRoot { get; init; }
    public bool IsDirectory { get; init; }
    public FileNode? Parent { get; init; }
    public SourceSelectionService? Selection { get; init; }
    public ObservableCollection<FileNode> Children { get; } = [];
    public bool? Included { get => IsDirectory ? _included : Selection?.Contains(FullPath) ?? _included; set { if (_updating || !value.HasValue) return; if (IsDirectory) { LoadChildren(); _updating = true; foreach (var file in Descendants().Where(x => !x.IsDirectory)) file.Included = value; _updating = false; Recalculate(); } else { if (value.Value) Selection?.AddFile(SourceRootId, FullPath, Mode); else Selection?.Remove(FullPath); Changed(); Parent?.Recalculate(); } } }
    public FileMode Mode { get => Selection?.Find(FullPath)?.Mode ?? _mode; set { _mode = value; var item = Selection?.Find(FullPath); if (item is not null) item.Mode = value; Changed(); } }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value) && value) LoadChildren(); } }
    public Func<FileNode, IEnumerable<FileNode>>? Loader { get; init; }
    public void LoadChildren() { if (_loaded || !IsDirectory) return; _loaded = true; Children.Clear(); foreach (var child in Loader?.Invoke(this) ?? []) Children.Add(child); Recalculate(); }
    public IEnumerable<FileNode> Descendants() { LoadChildren(); foreach (var child in Children) { yield return child; if (child.IsDirectory) foreach (var nested in child.Descendants()) yield return nested; } }
    public void RefreshFromSelection() { if (!IsDirectory) { Changed(nameof(Included)); Changed(nameof(Mode)); } else { foreach (var child in Children) child.RefreshFromSelection(); Recalculate(); } }
    private void Recalculate() { if (!IsDirectory || Children.Count == 0) return; var states = Children.Select(x => x.Included).ToList(); var next = states.All(x => x == true) ? true : states.All(x => x == false) ? false : null; Set(ref _included, next, nameof(Included)); Parent?.Recalculate(); }
}

public sealed class ChangeItem : Bindable
{
    private bool _apply;
    public required ReturnedChange Change { get; init; }
    public string Path => Change.DisplayPath; public string Classification => Change.Kind.ToString(); public string Message => Change.Message;
    public bool Apply { get => _apply; set { if (Set(ref _apply, value)) Change.Apply = value; } }
    public bool CanApply => Change.CanApply;
}
