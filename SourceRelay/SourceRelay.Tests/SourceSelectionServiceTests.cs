using SourceRelay.Models;
using SourceRelay.Services;
using Xunit;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Tests;

public sealed class SourceSelectionServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "SourceRelaySelection", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly SourceSelectionService _selection = new();
    public SourceSelectionServiceTests() { _root = Path.Combine(_temp, "Game"); Directory.CreateDirectory(_root); _selection.Configure(_root, new AppSettings()); }
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact] public void AbsoluteFileResolvesToRelativePathAndQuotedPathWorks()
    {
        var file = Write("Assets/Foo.cs");
        Assert.Equal(AddPathStatus.Added, _selection.AddPath($"  \"{file}\"  ").Status);
        Assert.Equal("Assets/Foo.cs", Assert.Single(_selection.Files).RelativePath);
    }

    [Fact] public void OutsideMissingAndExcludedPathsAreRejectedWithoutChangingSelection()
    {
        var outside = Path.Combine(_temp, "outside.cs"); File.WriteAllText(outside, "x");
        Assert.Equal(AddPathStatus.OutsideRoot, _selection.AddPath(outside).Status);
        Assert.Equal(AddPathStatus.Missing, _selection.AddPath(Path.Combine(_root, "missing.cs")).Status);
        Assert.Equal(AddPathStatus.Excluded, _selection.AddPath(Write("Assets/secret-token.txt")).Status);
        Assert.Empty(_selection.Files);
    }

    [Fact] public void DuplicateAddIsIdempotentAndPreservesMode()
    {
        var file = Write("Assets/Foo.cs"); _selection.AddPath(file); _selection.Files[0].Mode = FileMode.Context;
        Assert.Equal(AddPathStatus.AlreadySelected, _selection.AddPath(file).Status);
        Assert.Single(_selection.Files); Assert.Equal(FileMode.Context, _selection.Files[0].Mode);
    }

    [Fact] public void DirectoryAddSelectsEligibleDescendantsOnly()
    {
        Write("Assets/A.cs"); Write("Assets/Sub/B.cs"); Write("Assets/Sub/B.cs.meta"); Write("Assets/Library/ignored.cs");
        var result = _selection.AddPath(Path.Combine(_root, "Assets"));
        Assert.Equal(2, result.AddedCount); Assert.Equal(["Assets/A.cs", "Assets/Sub/B.cs"], _selection.Files.Select(x => x.RelativePath).Order().ToArray());
    }

    [Fact] public void RemoveAndModeChangesUpdateSharedState()
    {
        var file = Write("A.cs"); var changes = 0; _selection.Changed += (_, _) => changes++;
        _selection.AddPath(file); var item = Assert.Single(_selection.Files); item.Mode = FileMode.Context;
        Assert.Equal(FileMode.Context, Assert.Single(_selection.Snapshot()).Mode);
        _selection.Remove(file); Assert.Empty(_selection.Snapshot()); Assert.True(changes >= 3);
    }

    [Fact] public void DuplicateFileNamesRemainDistinct()
    {
        _selection.AddPath(Write("Assets/Foo/Controller.cs")); _selection.AddPath(Write("Assets/Bar/Controller.cs"));
        Assert.Equal(2, _selection.Files.Count); Assert.Equal(2, _selection.Files.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact] public void RelativeChainCanBeDerivedWithoutScanningTree()
    {
        var file = Write("Assets/Hockey/Scripts/State.cs");
        Assert.Equal(["Assets", "Hockey", "Scripts", "State.cs"], Path.GetRelativePath(_root, file).Split(Path.DirectorySeparatorChar));
    }

    private string Write(string relative) { var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "// test"); return path; }
}
