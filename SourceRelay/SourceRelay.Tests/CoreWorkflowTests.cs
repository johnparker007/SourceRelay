using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SourceRelay.Models;
using SourceRelay.Services;
using Xunit;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Tests;

public sealed class CoreWorkflowTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "SourceRelayTests", Guid.NewGuid().ToString("N"));
    private readonly string _project; private readonly BundleRecordStore _records;
    public CoreWorkflowTests() { _project = Path.Combine(_temp, "Game"); Directory.CreateDirectory(_project); _records = new(Path.Combine(_temp, "records")); }
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact] public async Task ManifestRoundTripsAndHashIsStable()
    {
        var m = new BundleManifest { BundleId = Guid.NewGuid(), Files = [new() { Path = "Assets/a.cs", Mode = FileMode.Context, Sha256 = "abc", Size = 3 }] };
        var json = JsonSerializer.Serialize(m, JsonStore.Options); var copy = JsonSerializer.Deserialize<BundleManifest>(json, JsonStore.Options)!;
        Assert.Equal(m.BundleId, copy.BundleId); Assert.Equal(FileMode.Context, copy.Files[0].Mode); Assert.Equal(HashService.Bytes(Encoding.UTF8.GetBytes("abc")), HashService.Bytes(Encoding.UTF8.GetBytes("abc")));
    }

    [Fact] public async Task BundlePreservesPathsModesAndProtocolFiles()
    {
        var path = Write("Assets/Scripts/a.cs", "old"); var result = await new BundleService(_records).CreateAsync(_project, [new(path, "Assets/Scripts/a.cs", FileMode.Editable)], Path.Combine(_temp, "out"));
        using var zip = ZipFile.OpenRead(result.ZipPath); Assert.Contains(zip.Entries, x => x.FullName == "INSTRUCTIONS.md"); Assert.Contains(zip.Entries, x => x.FullName == "manifest.json"); Assert.Contains(zip.Entries, x => x.FullName == "files/Assets/Scripts/a.cs"); Assert.Equal(FileMode.Editable, result.Record.Manifest.Files[0].Mode); Assert.DoesNotContain(_project, Read(zip, "manifest.json"));
    }

    [Fact] public async Task RecognisesReturnedModificationAndLocalConflict()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/a.cs", "changed"); var valid = await new ReturnedBundleService(_records).ValidateAsync(zip); Assert.Equal(ChangeKind.Modified, valid.Changes.Single().Kind);
        Write("a.cs", "local edit"); var conflict = await new ReturnedBundleService(_records).ValidateAsync(zip); Assert.Equal(ChangeKind.LocalFileChanged, conflict.Changes.Single().Kind);
    }

    [Fact] public async Task RejectsContextModificationAndUnexpectedFile()
    {
        var (_, zip) = await CreateAsync(FileMode.Context); Rewrite(zip, "files/a.cs", "changed"); Add(zip, "files/extra.cs", "bad"); var result = await new ReturnedBundleService(_records).ValidateAsync(zip);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.ContextOnlyModified); Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Unexpected); Assert.False(result.IsValid);
    }

    [Fact] public async Task RejectsUnknownMalformedTraversalAbsoluteDuplicateAndCaseCollision()
    {
        var unknown = Path.Combine(_temp, "unknown.zip"); MakeZip(unknown, new BundleManifest { BundleId = Guid.NewGuid() }, ("files/a.cs", "x")); Assert.Contains("not recognised", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(unknown)).Errors));
        var malformed = Path.Combine(_temp, "malformed.zip"); using (var z = ZipFile.Open(malformed, ZipArchiveMode.Create)) AddEntry(z, "manifest.json", "no"); Assert.False((await new ReturnedBundleService(_records).ValidateAsync(malformed)).IsValid);
        foreach (var bad in new[] { "files/../evil.cs", "/files/evil.cs", "C:/evil.cs" }) { var p = Path.Combine(_temp, Guid.NewGuid() + ".zip"); using (var z = ZipFile.Open(p, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, bad, "x"); } Assert.False((await new ReturnedBundleService(_records).ValidateAsync(p)).IsValid); }
        var collision = Path.Combine(_temp, "collision.zip"); using (var z = ZipFile.Open(collision, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, "files/A.cs", "x"); AddEntry(z, "files/a.cs", "y"); } Assert.Contains("case-colliding", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(collision)).Errors));
        var duplicate = Path.Combine(_temp, "duplicate.zip"); using (var z = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, "files/a.cs", "x"); AddEntry(z, "files/a.cs", "y"); } Assert.Contains("Duplicate", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(duplicate)).Errors));
    }

    [Fact] public async Task ApplyUndoRedoAndExternalChangeProtectionWork()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/a.cs", "new"); var returned = await new ReturnedBundleService(_records).ValidateAsync(zip); var history = new ApplyHistoryService(Path.Combine(_temp, "history")); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(returned, returned.Changes)).Success); Assert.Equal("new", File.ReadAllText(Path.Combine(_project, "a.cs")));
        Assert.True((await history.UndoAsync()).Success); Assert.Equal("old", File.ReadAllText(Path.Combine(_project, "a.cs"))); Assert.True((await history.RedoAsync()).Success);
        Write("a.cs", "external"); var protectedResult = await history.UndoAsync(); Assert.True(protectedResult.Conflict); Assert.Equal("external", File.ReadAllText(Path.Combine(_project, "a.cs")));
    }

    [Fact] public async Task ReadOnlyDestinationIsNotApplied()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/a.cs", "new"); var returned = await new ReturnedBundleService(_records).ValidateAsync(zip); var path = Path.Combine(_project, "a.cs"); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        var history = new ApplyHistoryService(Path.Combine(_temp, "history")); await history.LoadAsync(); var result = await history.ApplyAsync(returned, returned.Changes); Assert.False(result.Success); Assert.Contains("read-only", result.Message); File.SetAttributes(path, FileAttributes.Normal);
    }

    private async Task<(BundleRecord Record, string Zip)> CreateAsync(FileMode mode) { var file = Write("a.cs", "old"); var r = await new BundleService(_records).CreateAsync(_project, [new(file, "a.cs", mode)], Path.Combine(_temp, "out")); return (r.Record, r.ZipPath); }
    private string Write(string relative, string content) { var path = Path.Combine(_project, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    private static string Read(ZipArchive z, string name) { using var r = new StreamReader(z.GetEntry(name)!.Open()); return r.ReadToEnd(); }
    private static void Rewrite(string path, string entry, string content) { using var z = ZipFile.Open(path, ZipArchiveMode.Update); z.GetEntry(entry)!.Delete(); AddEntry(z, entry, content); }
    private static void Add(string path, string entry, string content) { using var z = ZipFile.Open(path, ZipArchiveMode.Update); AddEntry(z, entry, content); }
    private static void AddEntry(ZipArchive z, string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
    private static void MakeZip(string path, BundleManifest manifest, params (string, string)[] files) { using var z = ZipFile.Open(path, ZipArchiveMode.Create); AddEntry(z, "manifest.json", JsonSerializer.Serialize(manifest, JsonStore.Options)); foreach (var f in files) AddEntry(z, f.Item1, f.Item2); }
}
