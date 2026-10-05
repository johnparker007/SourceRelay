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
        using var zip = ZipFile.OpenRead(result.ZipPath); Assert.Contains(zip.Entries, x => x.FullName == "INSTRUCTIONS.md"); Assert.Contains(zip.Entries, x => x.FullName == "manifest.json"); Assert.Contains(zip.Entries, x => x.FullName == "files/root/Assets/Scripts/a.cs"); Assert.Equal(FileMode.Editable, result.Record.Manifest.Files[0].Mode); Assert.DoesNotContain(_project, Read(zip, "manifest.json"));
        var instructions = Read(zip, "INSTRUCTIONS.md");
        Assert.Contains("complete `files/` tree", instructions); Assert.Contains("even if you did not modify it", instructions); Assert.Contains("must be returned byte-for-byte unchanged", instructions); Assert.Contains("Do not include `INSTRUCTIONS.md`", instructions);
    }

    [Theory]
    [InlineData(FileMode.Editable)]
    [InlineData(FileMode.Context)]
    public async Task MissingOriginalFileMakesReturnInvalid(FileMode mode)
    {
        var (_, zip) = await CreateAsync(mode); Remove(zip, "files/root/a.cs"); var result = await new ReturnedBundleService(_records).ValidateAsync(zip);
        Assert.False(result.IsValid); Assert.Contains(result.Changes, x => x.Path == "a.cs" && x.Kind == ChangeKind.Missing);
    }

    [Fact] public async Task CompleteUnchangedContextReturnWithoutInstructionsIsValid()
    {
        var (_, zip) = await CreateAsync(FileMode.Context); Remove(zip, "INSTRUCTIONS.md"); var result = await new ReturnedBundleService(_records).ValidateAsync(zip);
        Assert.True(result.IsValid); Assert.Equal(ChangeKind.Unchanged, Assert.Single(result.Changes).Kind);
    }

    [Fact] public async Task RecognisesReturnedModificationAndLocalConflict()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/root/a.cs", "changed"); var valid = await new ReturnedBundleService(_records).ValidateAsync(zip); Assert.Equal(ChangeKind.Modified, valid.Changes.Single().Kind);
        Write("a.cs", "local edit"); var conflict = await new ReturnedBundleService(_records).ValidateAsync(zip); Assert.Equal(ChangeKind.LocalFileChanged, conflict.Changes.Single().Kind);
    }

    [Fact] public async Task FollowUpModificationToPreviouslyAppliedFileIsDetectedAndApplied()
    {
        var (_, originalZip) = await CreateAsync(FileMode.Editable);
        var firstZip = ReturnedCopy(originalZip, "first.zip", "files/root/a.cs", "B");
        var validator = new ReturnedBundleService(_records);
        var first = await validator.ValidateAsync(firstZip);
        var firstChange = Assert.Single(first.Changes);
        Assert.Equal(ChangeKind.Modified, firstChange.Kind);
        Assert.True(firstChange.Apply);

        var history = new ApplyHistoryService(Path.Combine(_temp, "follow-up-history"), _records);
        await history.LoadAsync();
        Assert.True((await history.ApplyAsync(first, first.Changes)).Success);
        Assert.Equal("B", File.ReadAllText(Path.Combine(_project, "a.cs")));

        var secondZip = ReturnedCopy(originalZip, "second.zip", "files/root/a.cs", "C");
        var second = await validator.ValidateAsync(secondZip);
        var secondChange = Assert.Single(second.Changes);
        Assert.Equal(ChangeKind.Modified, secondChange.Kind);
        Assert.NotEqual(ChangeKind.LocalFileChanged, secondChange.Kind);
        Assert.True(secondChange.Apply);
        Assert.True((await history.ApplyAsync(second, second.Changes)).Success);
        Assert.Equal("C", File.ReadAllText(Path.Combine(_project, "a.cs")));
    }

    [Fact] public async Task PreviouslyAppliedContentReturnedAgainIsUnchangedAfterServicesReload()
    {
        var (_, originalZip) = await CreateAsync(FileMode.Editable);
        var firstZip = ReturnedCopy(originalZip, "reload-first.zip", "files/root/a.cs", "B");
        var first = await new ReturnedBundleService(_records).ValidateAsync(firstZip);
        var historyRoot = Path.Combine(_temp, "reload-history");
        var history = new ApplyHistoryService(historyRoot, _records); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(first, first.Changes)).Success);

        var reloadedRecords = new BundleRecordStore(Path.Combine(_temp, "records"));
        var reloadedHistory = new ApplyHistoryService(historyRoot, reloadedRecords); await reloadedHistory.LoadAsync();
        var repeatedZip = ReturnedCopy(originalZip, "repeated.zip", "files/root/a.cs", "B");
        var repeated = await new ReturnedBundleService(reloadedRecords).ValidateAsync(repeatedZip);
        Assert.Equal(ChangeKind.Unchanged, Assert.Single(repeated.Changes).Kind);
    }

    [Fact] public async Task ExternalEditAfterApplyStillCausesConflict()
    {
        var (_, originalZip) = await CreateAsync(FileMode.Editable);
        var firstZip = ReturnedCopy(originalZip, "external-first.zip", "files/root/a.cs", "B");
        var first = await new ReturnedBundleService(_records).ValidateAsync(firstZip);
        var history = new ApplyHistoryService(Path.Combine(_temp, "external-history"), _records); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(first, first.Changes)).Success);
        Write("a.cs", "X");

        var nextZip = ReturnedCopy(originalZip, "external-next.zip", "files/root/a.cs", "C");
        var next = await new ReturnedBundleService(_records).ValidateAsync(nextZip);
        var change = Assert.Single(next.Changes);
        Assert.Equal(ChangeKind.LocalFileChanged, change.Kind);
        Assert.False(change.Apply);
    }

    [Fact] public async Task UndoAndRedoUpdateExpectedLocalContent()
    {
        var (_, originalZip) = await CreateAsync(FileMode.Editable);
        var firstZip = ReturnedCopy(originalZip, "undo-first.zip", "files/root/a.cs", "B");
        var first = await new ReturnedBundleService(_records).ValidateAsync(firstZip);
        var history = new ApplyHistoryService(Path.Combine(_temp, "undo-baseline-history"), _records); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(first, first.Changes)).Success);
        Assert.True((await history.UndoAsync()).Success);

        var afterUndoZip = ReturnedCopy(originalZip, "after-undo.zip", "files/root/a.cs", "C");
        var afterUndo = await new ReturnedBundleService(_records).ValidateAsync(afterUndoZip);
        Assert.Equal(ChangeKind.Modified, Assert.Single(afterUndo.Changes).Kind);

        Assert.True((await history.RedoAsync()).Success);
        var afterRedoZip = ReturnedCopy(originalZip, "after-redo.zip", "files/root/a.cs", "C");
        var afterRedo = await new ReturnedBundleService(_records).ValidateAsync(afterRedoZip);
        Assert.Equal(ChangeKind.Modified, Assert.Single(afterRedo.Changes).Kind);
    }

    [Fact] public async Task RejectsContextModificationAndUnexpectedFile()
    {
        var (_, zip) = await CreateAsync(FileMode.Context); Rewrite(zip, "files/root/a.cs", "changed"); Add(zip, "files/extra.cs", "bad"); var result = await new ReturnedBundleService(_records).ValidateAsync(zip);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.ContextOnlyModified); Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Unexpected); Assert.False(result.IsValid);
    }

    [Fact] public async Task RejectsUnknownMalformedTraversalAbsoluteDuplicateAndCaseCollision()
    {
        var unknown = Path.Combine(_temp, "unknown.zip"); MakeZip(unknown, new BundleManifest { BundleId = Guid.NewGuid() }, ("files/root/a.cs", "x")); Assert.Contains("not recognised", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(unknown)).Errors));
        var malformed = Path.Combine(_temp, "malformed.zip"); using (var z = ZipFile.Open(malformed, ZipArchiveMode.Create)) AddEntry(z, "manifest.json", "no"); Assert.False((await new ReturnedBundleService(_records).ValidateAsync(malformed)).IsValid);
        foreach (var bad in new[] { "files/../evil.cs", "/files/evil.cs", "C:/evil.cs" }) { var p = Path.Combine(_temp, Guid.NewGuid() + ".zip"); using (var z = ZipFile.Open(p, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, bad, "x"); } Assert.False((await new ReturnedBundleService(_records).ValidateAsync(p)).IsValid); }
        var collision = Path.Combine(_temp, "collision.zip"); using (var z = ZipFile.Open(collision, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, "files/A.cs", "x"); AddEntry(z, "files/a.cs", "y"); } Assert.Contains("case-colliding", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(collision)).Errors));
        var duplicate = Path.Combine(_temp, "duplicate.zip"); using (var z = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { AddEntry(z, "manifest.json", "{}"); AddEntry(z, "files/root/a.cs", "x"); AddEntry(z, "files/root/a.cs", "y"); } Assert.Contains("Duplicate", string.Join(' ', (await new ReturnedBundleService(_records).ValidateAsync(duplicate)).Errors));
    }

    [Fact] public async Task ApplyUndoRedoAndExternalChangeProtectionWork()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/root/a.cs", "new"); var returned = await new ReturnedBundleService(_records).ValidateAsync(zip); var history = new ApplyHistoryService(Path.Combine(_temp, "history")); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(returned, returned.Changes)).Success); Assert.Equal("new", File.ReadAllText(Path.Combine(_project, "a.cs")));
        Assert.True((await history.UndoAsync()).Success); Assert.Equal("old", File.ReadAllText(Path.Combine(_project, "a.cs"))); Assert.True((await history.RedoAsync()).Success);
        Write("a.cs", "external"); var protectedResult = await history.UndoAsync(); Assert.True(protectedResult.Conflict); Assert.Equal("external", File.ReadAllText(Path.Combine(_project, "a.cs")));
    }

    [Fact] public async Task ReadOnlyDestinationIsNotApplied()
    {
        var (_, zip) = await CreateAsync(FileMode.Editable); Rewrite(zip, "files/root/a.cs", "new"); var returned = await new ReturnedBundleService(_records).ValidateAsync(zip); var path = Path.Combine(_project, "a.cs"); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        var history = new ApplyHistoryService(Path.Combine(_temp, "history")); await history.LoadAsync(); var result = await history.ApplyAsync(returned, returned.Changes); Assert.False(result.Success); Assert.Contains("read-only", result.Message); File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact] public async Task MultiRootManifestLayoutAndReturnMappingAreUnambiguous()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_temp, "ClientA", "Game")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_temp, "ClientB", "Game")).FullName;
        var first = Path.Combine(firstRoot, "Assets", "Same.cs"); var second = Path.Combine(secondRoot, "Assets", "Same.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!); File.WriteAllText(first, "one"); File.WriteAllText(second, "two");
        var roots = new[] { new BundleSourceRoot("one", firstRoot, "Game (ClientA)"), new BundleSourceRoot("two", secondRoot, "Game (ClientB)") };
        var result = await new BundleService(_records).CreateAsync(roots,
            [new("one", first, "Assets/Same.cs", FileMode.Editable), new("two", second, "Assets/Same.cs", FileMode.Editable)], Path.Combine(_temp, "multi"));
        Assert.Equal(2, result.Record.Manifest.Roots.Count); Assert.Equal(2, result.Record.Manifest.FormatVersion);
        using (var zip = ZipFile.OpenRead(result.ZipPath))
        {
            Assert.NotNull(zip.GetEntry("files/one/Assets/Same.cs")); Assert.NotNull(zip.GetEntry("files/two/Assets/Same.cs"));
            var manifest = Read(zip, "manifest.json"); Assert.DoesNotContain(firstRoot, manifest); Assert.DoesNotContain(secondRoot, manifest);
        }
        Rewrite(result.ZipPath, "files/one/Assets/Same.cs", "ONE"); Rewrite(result.ZipPath, "files/two/Assets/Same.cs", "TWO");
        var returned = await new ReturnedBundleService(_records).ValidateAsync(result.ZipPath); Assert.True(returned.IsValid); Assert.Equal(2, returned.Changes.Count(x => x.Kind == ChangeKind.Modified));
        var history = new ApplyHistoryService(Path.Combine(_temp, "multi-history")); await history.LoadAsync(); Assert.True((await history.ApplyAsync(returned, returned.Changes)).Success);
        Assert.Equal("ONE", File.ReadAllText(first)); Assert.Equal("TWO", File.ReadAllText(second));
    }

    [Fact] public async Task AppliedBaselinesForIdenticalMultiRootPathsAreIndependent()
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_temp, "BaselineA")).FullName;
        var secondRoot = Directory.CreateDirectory(Path.Combine(_temp, "BaselineB")).FullName;
        var firstFile = Path.Combine(firstRoot, "same.cs"); var secondFile = Path.Combine(secondRoot, "same.cs");
        File.WriteAllText(firstFile, "A1"); File.WriteAllText(secondFile, "A2");
        var made = await new BundleService(_records).CreateAsync(
            [new("one", firstRoot, "One"), new("two", secondRoot, "Two")],
            [new("one", firstFile, "same.cs", FileMode.Editable), new("two", secondFile, "same.cs", FileMode.Editable)], Path.Combine(_temp, "baseline-multi"));
        var firstReturn = ReturnedCopy(made.ZipPath, "baseline-multi-first.zip", ("files/one/same.cs", "B1"), ("files/two/same.cs", "B2"));
        var returned = await new ReturnedBundleService(_records).ValidateAsync(firstReturn);
        var history = new ApplyHistoryService(Path.Combine(_temp, "baseline-multi-history"), _records); await history.LoadAsync();
        Assert.True((await history.ApplyAsync(returned, returned.Changes)).Success);

        File.WriteAllText(secondFile, "external");
        var followUp = ReturnedCopy(made.ZipPath, "baseline-multi-next.zip", ("files/one/same.cs", "C1"), ("files/two/same.cs", "C2"));
        var validated = await new ReturnedBundleService(_records).ValidateAsync(followUp);
        Assert.Equal(ChangeKind.Modified, validated.Changes.Single(x => x.RootId == "one").Kind);
        Assert.Equal(ChangeKind.LocalFileChanged, validated.Changes.Single(x => x.RootId == "two").Kind);
    }

    [Fact] public async Task AppliedBaselineDoesNotMakeContextOnlyFileEditable()
    {
        var (record, originalZip) = await CreateAsync(FileMode.Context);
        record.SetExpectedLocalHash("root", "a.cs", HashService.Bytes(Encoding.UTF8.GetBytes("B")));
        await _records.SaveAsync(record);
        Write("a.cs", "B");
        var returnedZip = ReturnedCopy(originalZip, "context-follow-up.zip", "files/root/a.cs", "C");
        var returned = await new ReturnedBundleService(_records).ValidateAsync(returnedZip);
        var change = Assert.Single(returned.Changes);
        Assert.Equal(ChangeKind.ContextOnlyModified, change.Kind);
        Assert.False(change.Apply);
        Assert.False(returned.IsValid);
    }

    [Fact] public async Task UnknownRootAndMovingFileBetweenRootsAreRejected()
    {
        var a = Directory.CreateDirectory(Path.Combine(_temp, "A")).FullName; var b = Directory.CreateDirectory(Path.Combine(_temp, "B")).FullName;
        var file = Path.Combine(a, "same.cs"); File.WriteAllText(file, "x");
        var made = await new BundleService(_records).CreateAsync([new("a", a, "A"), new("b", b, "B")], [new("a", file, "same.cs", FileMode.Editable)], Path.Combine(_temp, "moves"));
        using (var zip = ZipFile.Open(made.ZipPath, ZipArchiveMode.Update)) { var bytes = Read(zip, "files/a/same.cs"); zip.GetEntry("files/a/same.cs")!.Delete(); AddEntry(zip, "files/b/same.cs", bytes); }
        var moved = await new ReturnedBundleService(_records).ValidateAsync(made.ZipPath); Assert.False(moved.IsValid); Assert.Contains(moved.Changes, x => x.Kind == ChangeKind.Unexpected);
        using (var zip = ZipFile.Open(made.ZipPath, ZipArchiveMode.Update)) { zip.GetEntry("files/b/same.cs")!.Delete(); AddEntry(zip, "files/unknown/same.cs", "x"); }
        var unknown = await new ReturnedBundleService(_records).ValidateAsync(made.ZipPath); Assert.False(unknown.IsValid); Assert.Contains(unknown.Changes, x => x.Message.Contains("Unknown Source Root"));
    }

    [Fact] public async Task ExistingV1BundleRecordRemainsRecognisable()
    {
        var file = Write("legacy.cs", "old"); var info = new FileInfo(file);
        var manifest = new BundleManifest { FormatVersion = 1, BundleId = Guid.NewGuid(), CreatedUtc = DateTime.UtcNow, ProjectName = "Game",
            Files = [new BundleFile { Path = "legacy.cs", Mode = FileMode.Editable, Sha256 = await HashService.FileAsync(file), Size = info.Length }] };
        await _records.SaveAsync(new BundleRecord { Manifest = manifest, ProjectRoot = _project });
        var zipPath = Path.Combine(_temp, "legacy.zip"); MakeZip(zipPath, manifest, ("files/legacy.cs", "old"));
        var returned = await new ReturnedBundleService(_records).ValidateAsync(zipPath); Assert.True(returned.IsValid); Assert.Equal(ChangeKind.Unchanged, Assert.Single(returned.Changes).Kind);
    }

    private async Task<(BundleRecord Record, string Zip)> CreateAsync(FileMode mode) { var file = Write("a.cs", "old"); var r = await new BundleService(_records).CreateAsync(_project, [new(file, "a.cs", mode)], Path.Combine(_temp, "out")); return (r.Record, r.ZipPath); }
    private string Write(string relative, string content) { var path = Path.Combine(_project, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
    private static string Read(ZipArchive z, string name) { using var r = new StreamReader(z.GetEntry(name)!.Open()); return r.ReadToEnd(); }
    private static void Rewrite(string path, string entry, string content) { using var z = ZipFile.Open(path, ZipArchiveMode.Update); z.GetEntry(entry)!.Delete(); AddEntry(z, entry, content); }
    private static void Add(string path, string entry, string content) { using var z = ZipFile.Open(path, ZipArchiveMode.Update); AddEntry(z, entry, content); }
    private static void Remove(string path, string entry) { using var z = ZipFile.Open(path, ZipArchiveMode.Update); z.GetEntry(entry)!.Delete(); }
    private static void AddEntry(ZipArchive z, string name, string text) { using var w = new StreamWriter(z.CreateEntry(name).Open()); w.Write(text); }
    private static void MakeZip(string path, BundleManifest manifest, params (string, string)[] files) { using var z = ZipFile.Open(path, ZipArchiveMode.Create); AddEntry(z, "manifest.json", JsonSerializer.Serialize(manifest, JsonStore.Options)); foreach (var f in files) AddEntry(z, f.Item1, f.Item2); }
    private string ReturnedCopy(string original, string name, string entry, string content) => ReturnedCopy(original, name, (entry, content));
    private string ReturnedCopy(string original, string name, params (string Entry, string Content)[] changes)
    {
        var path = Path.Combine(_temp, name); File.Copy(original, path);
        foreach (var change in changes) Rewrite(path, change.Entry, change.Content);
        return path;
    }
}
