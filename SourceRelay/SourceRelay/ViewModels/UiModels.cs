using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SourceRelay.Models;
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
    private bool? _included = false; private bool _expanded; private bool _loaded; private FileMode _mode;
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public ObservableCollection<FileNode> Children { get; } = [];
    public bool? Included { get => _included; set { if (!Set(ref _included, value)) return; if (IsDirectory && value.HasValue) foreach (var child in Children) child.Included = value; } }
    public FileMode Mode { get => _mode; set => Set(ref _mode, value); }
    public bool IsExpanded { get => _expanded; set { if (Set(ref _expanded, value) && value) LoadChildren(); } }
    public Func<FileNode, IEnumerable<FileNode>>? Loader { get; init; }
    public void LoadChildren() { if (_loaded || !IsDirectory) return; _loaded = true; Children.Clear(); foreach (var child in Loader?.Invoke(this) ?? []) { if (_included.HasValue) child.Included = _included; Children.Add(child); } }
    public IEnumerable<FileNode> Descendants() { LoadChildren(); foreach (var child in Children) { yield return child; if (child.IsDirectory) foreach (var nested in child.Descendants()) yield return nested; } }
}

public sealed class ChangeItem : Bindable
{
    private bool _apply;
    public required ReturnedChange Change { get; init; }
    public string Path => Change.Path; public string Classification => Change.Kind.ToString(); public string Message => Change.Message;
    public bool Apply { get => _apply; set { if (Set(ref _apply, value)) Change.Apply = value; } }
    public bool CanApply => Change.CanApply;
}
