using System;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <see cref="MugshotFolderPruner"/> — the folder cleanup behind the NPCs-menu
/// "Delete Mugshot" command. It deletes directories on the user's disk, so the two
/// safety properties are pinned first and hardest: the configured root is never
/// deleted, and the walk never climbs above it. A regression in either turns a
/// one-image delete into a directory wipe.
///
/// Each test builds a throwaway tree under the OS temp folder and removes it in
/// <see cref="Dispose"/>; nothing here needs a game install or the app's settings.
/// </summary>
public sealed class MugshotFolderPrunerTests : IDisposable
{
    private readonly string _root;

    public MugshotFolderPrunerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NPC2_PrunerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leaked temp folder is not worth failing a test run over.
        }
    }

    /// <summary>Builds &lt;root&gt;\Mods\&lt;mod&gt;\&lt;plugin&gt; and returns the leaf.</summary>
    private string MakeMugshotTree(string modName, string pluginName)
    {
        var leaf = Path.Combine(_root, "Mugshots", modName, pluginName);
        Directory.CreateDirectory(leaf);
        return leaf;
    }

    private string MugshotsRoot => Path.Combine(_root, "Mugshots");

    // ---------------------------------------------------------------------
    // Safety properties
    // ---------------------------------------------------------------------

    [Fact]
    public void Prune_NeverDeletesTheRootItself()
    {
        // The whole tree is empty, so an unguarded walk would consume the root
        // and keep going. It must stop with the root still standing.
        var leaf = MakeMugshotTree("Bijin", "Skyrim.esm");

        MugshotFolderPruner.Prune(leaf, MugshotsRoot);

        Directory.Exists(MugshotsRoot).Should().BeTrue("the configured mugshots root is never a prune target");
        Directory.Exists(_root).Should().BeTrue();
    }

    [Fact]
    public void Prune_RefusesAFolderOutsideTheRoot()
    {
        // The classic corruption shape: the image turned out to live somewhere the
        // root doesn't cover (moved drive, hand-edited path). Deleting nothing is
        // the only safe answer — the walk must not treat an unrelated parent chain
        // as prunable.
        var outside = Path.Combine(_root, "SomewhereElse", "Skyrim.esm");
        Directory.CreateDirectory(outside);

        var deleted = MugshotFolderPruner.Prune(outside, MugshotsRoot);

        deleted.Should().BeEmpty();
        Directory.Exists(outside).Should().BeTrue();
    }

    [Fact]
    public void Prune_StopsAtTheFirstNonEmptyFolder()
    {
        // Another NPC's mugshot still lives in the plugin folder, so nothing above
        // it may go either.
        var leaf = MakeMugshotTree("Bijin", "Skyrim.esm");
        File.WriteAllText(Path.Combine(leaf, "0001A6D2.png"), "not really a png");

        var deleted = MugshotFolderPruner.Prune(leaf, MugshotsRoot);

        deleted.Should().BeEmpty();
        Directory.Exists(leaf).Should().BeTrue();
    }

    [Fact]
    public void Prune_KeepsTheModFolderWhenAnotherPluginRemains()
    {
        // Deleting the last image for one plugin must not take the sibling plugin's
        // folder with it.
        var emptied = MakeMugshotTree("Bijin", "Skyrim.esm");
        var sibling = MakeMugshotTree("Bijin", "Dawnguard.esm");
        File.WriteAllText(Path.Combine(sibling, "0001A6D2.png"), "x");

        var deleted = MugshotFolderPruner.Prune(emptied, MugshotsRoot);

        deleted.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(emptied));
        Directory.Exists(sibling).Should().BeTrue();
        Directory.Exists(Path.Combine(MugshotsRoot, "Bijin")).Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // The intended cleanup
    // ---------------------------------------------------------------------

    [Fact]
    public void Prune_RemovesPluginThenModFolderWhenBothAreEmptied()
    {
        // The layout is <Root>\<Mod>\<Plugin>\<FormID>.png, so the last image for a
        // mod strands two levels. Both go, outermost last.
        var leaf = MakeMugshotTree("Bijin", "Skyrim.esm");
        var modFolder = Path.Combine(MugshotsRoot, "Bijin");

        var deleted = MugshotFolderPruner.Prune(leaf, MugshotsRoot);

        deleted.Should().Equal(Path.GetFullPath(leaf), Path.GetFullPath(modFolder));
        Directory.Exists(leaf).Should().BeFalse();
        Directory.Exists(modFolder).Should().BeFalse();
        Directory.Exists(MugshotsRoot).Should().BeTrue();
    }

    [Fact]
    public void Prune_ToleratesATrailingSeparatorOnTheRoot()
    {
        // Folder paths reach us from user settings and from MugShotFolderPaths, and
        // both sources sometimes carry a trailing slash. It must not turn the root
        // guard into a mismatch (which would silently disable pruning).
        var leaf = MakeMugshotTree("Bijin", "Skyrim.esm");

        var deleted = MugshotFolderPruner.Prune(leaf, MugshotsRoot + Path.DirectorySeparatorChar);

        deleted.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null, "C:\\x")]
    [InlineData("C:\\x", null)]
    [InlineData("", "")]
    public void Prune_NullOrEmptyInputs_DeleteNothing(string? startDir, string? root)
    {
        MugshotFolderPruner.Prune(startDir, root).Should().BeEmpty();
    }

    [Fact]
    public void Prune_MissingStartFolder_DeletesNothing()
    {
        var missing = Path.Combine(MugshotsRoot, "Bijin", "Skyrim.esm");

        MugshotFolderPruner.Prune(missing, MugshotsRoot).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Containment helper (drives the curated prune-root choice)
    // ---------------------------------------------------------------------

    [Fact]
    public void Contains_IsStrict_AFolderIsNotInsideItself()
    {
        // This strictness is what makes the root un-deletable in Prune.
        MugshotFolderPruner.Contains(MugshotsRoot, MugshotsRoot).Should().BeFalse();
    }

    [Fact]
    public void Contains_RecognisesADescendantFile()
    {
        var file = Path.Combine(MugshotsRoot, "Bijin", "Skyrim.esm", "0001A6D2.png");
        MugshotFolderPruner.Contains(MugshotsRoot, file).Should().BeTrue();
    }

    [Fact]
    public void Contains_RejectsASiblingWithASharedNamePrefix()
    {
        // "Mugshots2" starts with "Mugshots" as a string but is a different folder;
        // a naive prefix test would let a delete walk into it.
        var sibling = Path.Combine(_root, "Mugshots2", "Bijin", "x.png");
        MugshotFolderPruner.Contains(MugshotsRoot, sibling).Should().BeFalse();
    }

    [Fact]
    public void Contains_IsCaseInsensitive()
    {
        var file = Path.Combine(MugshotsRoot.ToUpperInvariant(), "Bijin", "x.png");
        MugshotFolderPruner.Contains(MugshotsRoot, file).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "C:\\x\\y.png")]
    [InlineData("C:\\x", null)]
    public void Contains_NullInputs_AreFalse(string? folder, string? candidate)
    {
        MugshotFolderPruner.Contains(folder, candidate).Should().BeFalse();
    }
}
