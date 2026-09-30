namespace SourceRelay.Services;

public sealed class DownloadsMonitor : IDisposable
{
    private FileSystemWatcher? _watcher; private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    public event Func<string, Task>? ZipReady;
    public void Start(string folder)
    {
        Dispose(); if (!Directory.Exists(folder)) return;
        _watcher = new(folder, "*.zip") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
        _watcher.Created += OnChanged; _watcher.Renamed += OnChanged;
    }
    private async void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (!_seen.Add(e.FullPath)) return;
        long previous = -1;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(750);
            try { var length = new FileInfo(e.FullPath).Length; using var stream = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read); if (length == previous) { if (ZipReady is not null) await ZipReady(e.FullPath); return; } previous = length; }
            catch (IOException) { }
        }
        _seen.Remove(e.FullPath);
    }
    public void Dispose() { if (_watcher is not null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; } }
}
