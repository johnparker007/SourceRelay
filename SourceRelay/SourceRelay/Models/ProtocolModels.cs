using System.IO;
using System.Text.Json.Serialization;

namespace SourceRelay.Models;

public enum FileMode { Editable, Context }

public sealed class BundleFile
{
    public string Path { get; set; } = "";
    public FileMode Mode { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class BundleManifest
{
    public string Format { get; set; } = "SourceRelay";
    public int FormatVersion { get; set; } = 1;
    public Guid BundleId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string ProjectName { get; set; } = "";
    public List<BundleFile> Files { get; set; } = [];
}

public enum BundleState { Created, AwaitingReturn, ReturnDetected, Reviewed, Applied, PartiallyApplied, Dismissed }

public sealed class BundleRecord
{
    public BundleManifest Manifest { get; set; } = new();
    public string ProjectRoot { get; set; } = "";
    public BundleState State { get; set; } = BundleState.AwaitingReturn;
    public string? ArchivePath { get; set; }
}

public enum ChangeKind { Modified, Unchanged, LocalFileChanged, Missing, Unexpected, Invalid, ContextOnlyModified, ReadOnly }

public sealed class ReturnedChange
{
    public string Path { get; set; } = "";
    public ChangeKind Kind { get; set; }
    public string Message { get; set; } = "";
    [JsonIgnore] public byte[]? ReturnedBytes { get; set; }
    [JsonIgnore] public string Diff { get; set; } = "";
    [JsonIgnore] public bool Apply { get; set; }
    [JsonIgnore] public bool CanApply => Kind is ChangeKind.Modified or ChangeKind.LocalFileChanged;
}

public sealed class ReturnedBundle
{
    public Guid BundleId { get; set; }
    public string ArchivePath { get; set; } = "";
    public BundleRecord Record { get; set; } = new();
    public List<ReturnedChange> Changes { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed class AppSettings
{
    public string LastProjectRoot { get; set; } = "";
    public List<string> RecentProjectRoots { get; set; } = [];
    public string BundleOutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SourceRelay");
    public bool MonitorDownloads { get; set; } = true;
    public string DownloadsFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int MaxHistoryEntries { get; set; } = 20;
    public List<string> ExcludedDirectories { get; set; } = [".git", ".vs", "Library", "Temp", "Logs", "obj", "bin"];
}

public sealed class HistoryFile
{
    public string RelativePath { get; set; } = "";
    public string BeforeFile { get; set; } = "";
    public string AfterFile { get; set; } = "";
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BundleId { get; set; }
    public string ProjectRoot { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool IsUndone { get; set; }
    public List<HistoryFile> Files { get; set; } = [];
}
