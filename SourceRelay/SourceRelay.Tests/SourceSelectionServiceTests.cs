using SourceRelay.Models;
using SourceRelay.Services;
using Xunit;
using FileMode = SourceRelay.Models.FileMode;

namespace SourceRelay.Tests;

public sealed class SourceSelectionServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "SourceRelaySelection", Guid.NewGuid().ToString("N"));
    private readonly SourceSelectionService _selection = new();
    public SourceSelectionServiceTests() { Directory.CreateDirectory(_temp); _selection.SetSettings(new AppSettings()); }
    public void Dispose() { try { Directory.Delete(_temp, true); } catch { } }

    [Fact] public void UnityPathInfersDirectoryAboveAssets()
    {
        var file = Write("Arcade/Assets/_Project/Foo.cs");
        _selection.AddPath(file);
        Assert.Equal(Path.Combine(_temp, "Arcade"), Assert.Single(_selection.Roots).AbsolutePath);
    }

    [Fact] public void NearestSolutionAncestorWinsWhenNotUnderAssets()
    {
        Write("Workspace/App.sln"); var file = Write("Workspace/src/Project/Foo.cs"); Write("Workspace/src/Project/App.csproj");
        Assert.Equal(Path.Combine(_temp, "Workspace"), SourceRootInference.Infer(file));
    }

    [Fact] public void NearestProjectAncestorIsUsedWithoutSolution()
    {
        Write("Workspace/src/Project/App.csproj"); var file = Write("Workspace/src/Project/Foo.cs");
        Assert.Equal(Path.Combine(_temp, "Workspace", "src", "Project"), SourceRootInference.Infer(file));
    }

    [Fact] public void ArbitraryFileFallsBackToParent()
    {
        var file = Write("misc/Foo.txt"); Assert.Equal(Path.GetDirectoryName(file), SourceRootInference.Infer(file));
    }

    [Fact] public void ExistingOwningRootIsReusedAndNestedRootIsNotCreated()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "Game")).FullName; var existing = _selection.AddRoot(root);
        var file = Write("Game/Assets/Plugins/Package/Foo.cs"); var result = _selection.AddPath(file);
        Assert.Equal(existing.Id, result.SourceRootId); Assert.Single(_selection.Roots);
    }

    [Fact] public void UnrelatedFilesCreateIndependentRoots()
    {
        _selection.AddPath(Write("Arcade/Assets/A.cs")); _selection.AddPath(Write("Hockey/Assets/B.cs"));
        Assert.Equal(2, _selection.Roots.Count);
    }

    [Fact] public void IdenticalRelativePathsInDifferentRootsRemainDistinct()
    {
        _selection.AddPath(Write("One/Assets/Scripts/GameManager.cs")); _selection.AddPath(Write("Two/Assets/Scripts/GameManager.cs"));
        Assert.Equal(2, _selection.Files.Count); Assert.Equal(2, _selection.Files.Select(x => x.SourceRootId).Distinct().Count());
        Assert.All(_selection.Files, x => Assert.Equal("Assets/Scripts/GameManager.cs", x.RelativePath));
    }

    [Fact] public void QuotedPathDuplicateModeDirectoryExclusionsAndRemovalWork()
    {
        var a = Write("Game/Assets/A.cs"); Write("Game/Assets/Sub/B.cs"); Write("Game/Assets/Sub/B.cs.meta"); Write("Game/Assets/Library/ignored.cs");
        Assert.Equal(AddPathStatus.Added, _selection.AddPath($" \"{a}\" ").Status); _selection.Files[0].Mode = FileMode.Context;
        Assert.Equal(AddPathStatus.AlreadySelected, _selection.AddPath(a).Status); Assert.Equal(FileMode.Context, _selection.Files[0].Mode);
        var directory = _selection.AddPath(Path.Combine(_temp, "Game", "Assets")); Assert.Equal(1, directory.AddedCount); Assert.Equal(2, _selection.Files.Count);
        var root = Assert.Single(_selection.Roots); Assert.True(_selection.RemoveRoot(root.Id)); Assert.Empty(_selection.Files);
    }

    [Fact] public void DuplicateDisplayNamesAreDisambiguatedWithoutIdentityCollision()
    {
        var one = Directory.CreateDirectory(Path.Combine(_temp, "ClientA", "Game")).FullName; var two = Directory.CreateDirectory(Path.Combine(_temp, "ClientB", "Game")).FullName;
        var roots = new[] { _selection.AddRoot(one), _selection.AddRoot(two) };
        Assert.NotEqual(roots[0].Id, roots[1].Id); Assert.All(roots, x => Assert.Contains("(", x.DisplayName));
    }

    private string Write(string relative, string content = "// test") { var path = Path.Combine(_temp, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path; }
}
