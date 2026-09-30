using System.IO;
using SourceRelay.Models;
using SourceRelay.ViewModels;

namespace SourceRelay.Services;

public sealed class ProjectScanner(AppSettings settings)
{
    private static readonly string[] SensitiveExtensions = [".pfx", ".p12", ".pem", ".key", ".cer", ".crt", ".jks", ".keystore"];
    public IEnumerable<FileNode> Scan(string root) => Children(new FileNode { FullPath = root, RelativePath = "", IsDirectory = true });
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
            var relative = Path.GetRelativePath(Root(parent), path).Replace('\\', '/');
            var node = new FileNode { Name = name, FullPath = path, RelativePath = relative, IsDirectory = directory, Loader = Children };
            if (directory) node.Children.Add(new FileNode { Name = "Loading…", FullPath = path, RelativePath = relative });
            yield return node;
        }
    }
    private static string Root(FileNode node) { var path = node.FullPath; var relative = node.RelativePath.Replace('/', Path.DirectorySeparatorChar); return relative.Length == 0 ? path : path[..^(relative.Length + 1)]; }
    private static bool IsExcludedFile(string name)
    {
        var lower = name.ToLowerInvariant(); var extension = Path.GetExtension(lower);
        return extension == ".meta" || SensitiveExtensions.Contains(extension) || lower == ".env" || lower.Contains("credential") || lower.Contains("secret") || lower.StartsWith("id_rsa");
    }
}
