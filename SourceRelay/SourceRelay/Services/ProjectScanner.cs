using System.IO;
using SourceRelay.Models;
using SourceRelay.ViewModels;

namespace SourceRelay.Services;

public sealed class ProjectScanner(AppSettings settings, SourceSelectionService? selection = null)
{
    private static readonly string[] SensitiveExtensions = [".pfx", ".p12", ".pem", ".key", ".cer", ".crt", ".jks", ".keystore"];
    public FileNode ScanRoot(SourceRoot root)
    {
        var node = new FileNode { Name = root.DisplayName, FullPath = root.AbsolutePath, RelativePath = "", SourceRootId = root.Id, SourceRootPath = root.AbsolutePath, IsSourceRoot = true, IsDirectory = true, Selection = selection, Loader = Children };
        node.Children.Add(new FileNode { Name = "Loading…", FullPath = root.AbsolutePath, RelativePath = "" }); return node;
    }
    public IEnumerable<FileNode> Scan(string root) => Children(new FileNode { FullPath = root, SourceRootPath = root, RelativePath = "", IsDirectory = true, Selection = selection });
    private IEnumerable<FileNode> Children(FileNode parent)
    {
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFileSystemEntries(parent.FullPath).OrderByDescending(Directory.Exists).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch { yield break; }
        foreach (var path in paths)
        {
            var directory = Directory.Exists(path); var name = Path.GetFileName(path);
            if (directory && settings.ExcludedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!directory && IsExcludedFile(name)) continue;
            var relative = Path.GetRelativePath(parent.SourceRootPath, path).Replace('\\', '/');
            var node = new FileNode { Name = name, FullPath = path, RelativePath = relative, SourceRootId = parent.SourceRootId, SourceRootPath = parent.SourceRootPath, IsDirectory = directory, Loader = Children, Parent = parent, Selection = selection };
            if (directory) node.Children.Add(new FileNode { Name = "Loading…", FullPath = path, RelativePath = relative });
            yield return node;
        }
    }
    private static bool IsExcludedFile(string name)
    {
        var lower = name.ToLowerInvariant(); var extension = Path.GetExtension(lower);
        return extension == ".meta" || SensitiveExtensions.Contains(extension) || lower == ".env" || lower.Contains("credential") || lower.Contains("secret") || lower.StartsWith("id_rsa");
    }

    public static bool IsExcluded(string path, string root, AppSettings settings, out string reason)
    {
        var relative = Path.GetRelativePath(root, path);
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).SkipLast(File.Exists(path) ? 1 : 0))
            if (settings.ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase)) { reason = $"directory ‘{part}’"; return true; }
        if (File.Exists(path) && IsExcludedFile(Path.GetFileName(path))) { reason = $"file rule for ‘{Path.GetFileName(path)}’"; return true; }
        reason = ""; return false;
    }
}
