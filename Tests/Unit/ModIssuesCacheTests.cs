using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the Mod Issues cache contract: validity is (version match) AND
/// (ModStateSnapshot value-equality) AND (loose-asset-tree aggregate equality)
/// AND (scan ran to completion); entries round-trip through JSON; pruning
/// drops entries for mods that no longer exist.
/// </summary>
public class ModIssuesCacheTests
{
    private static ModStateSnapshot MakeSnapshot(string pluginName = "Test.esp",
        long size = 100, int fileCount = 3)
    {
        return new ModStateSnapshot
        {
            PluginSnapshots = new List<FileSnapshot>
            {
                new() { FileName = pluginName, FileSize = size, LastWriteTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
            },
            BsaSnapshots = new List<FileSnapshot>(),
            DirectorySnapshots = new List<DirectorySnapshot>
            {
                new() { Path = @"meshes\actors\character\facegendata\facegeom\Test.esp", FileCount = fileCount, LastWriteTimeUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc) },
            },
        };
    }

    private static List<LooseAssetTreeSnapshot> MakeTrees(long bytes = 1234)
    {
        return new List<LooseAssetTreeSnapshot>
        {
            new() { Root = @"C:\Mods\Test\meshes", FileCount = 10, TotalBytes = bytes, MaxLastWriteUtc = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc) },
            new() { Root = @"C:\Mods\Test\textures", FileCount = 20, TotalBytes = 999, MaxLastWriteUtc = new DateTime(2026, 2, 4, 0, 0, 0, DateTimeKind.Utc) },
        };
    }

    private static ModIssueScanResult MakeResult(ModStateSnapshot? snapshot = null,
        List<LooseAssetTreeSnapshot>? trees = null, bool completed = true)
    {
        return new ModIssueScanResult
        {
            ScanTimeUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            Snapshot = snapshot ?? MakeSnapshot(),
            LooseAssetTrees = trees ?? MakeTrees(),
            ScanCompleted = completed,
            ScannedNpcCount = 5,
            Issues = new List<ModIssue>
            {
                new()
                {
                    Type = ModIssueType.MissingNifTexture,
                    NpcFormKey = FormKey.Factory("000ABC:Test.esp"),
                    NpcDisplayName = "Test NPC",
                    AffectedPath = @"textures\actors\character\missing.dds",
                    NifPath = @"meshes\armor\test_1.nif",
                    ShapeName = "Cuirass",
                },
            },
        };
    }

    [Fact]
    public void EqualSnapshotAndTrees_IsValid()
    {
        Assert.True(ModIssuesCache.IsEntryValid(MakeResult(), MakeSnapshot(), MakeTrees()));
    }

    [Fact]
    public void PluginDrift_Invalidates()
    {
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(), MakeSnapshot(size: 101), MakeTrees()));
    }

    [Fact]
    public void FaceGenDirectoryDrift_Invalidates()
    {
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(), MakeSnapshot(fileCount: 4), MakeTrees()));
    }

    [Fact]
    public void LooseTreeDrift_Invalidates()
    {
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(), MakeSnapshot(), MakeTrees(bytes: 4321)));
    }

    [Fact]
    public void TreeCountChange_Invalidates()
    {
        var fewerTrees = MakeTrees().Take(1).ToList();
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(), MakeSnapshot(), fewerTrees));
    }

    [Fact]
    public void IncompleteScan_NeverValid()
    {
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(completed: false), MakeSnapshot(), MakeTrees()));
    }

    [Fact]
    public void SnapshotPresenceMismatch_Invalidates()
    {
        // Entry has a snapshot but the mod's folders are now gone (null current).
        Assert.False(ModIssuesCache.IsEntryValid(MakeResult(), null, MakeTrees()));

        // Entry had no snapshot but one exists now.
        var noSnapshotEntry = MakeResult();
        noSnapshotEntry.Snapshot = null;
        Assert.False(ModIssuesCache.IsEntryValid(noSnapshotEntry, MakeSnapshot(), MakeTrees()));
    }

    [Fact]
    public async Task RoundTrip_PreservesEntriesAndValidity()
    {
        using var tempDir = new TempDir("ModIssuesCache");
        var path = tempDir.Combine("ModIssuesCache.json");

        var cache = new ModIssuesCache(path);
        cache.Load();
        cache.Store("My Mod", MakeResult());
        await cache.SaveAsync();

        var reloaded = new ModIssuesCache(path);
        reloaded.Load();

        Assert.True(reloaded.TryGetValid("My Mod", MakeSnapshot(), MakeTrees(), out var entry));
        Assert.Single(entry.Issues);
        var issue = entry.Issues[0];
        Assert.Equal(ModIssueType.MissingNifTexture, issue.Type);
        Assert.Equal(FormKey.Factory("000ABC:Test.esp"), issue.NpcFormKey);
        Assert.Equal(@"textures\actors\character\missing.dds", issue.AffectedPath);
        Assert.Equal("Cuirass", issue.ShapeName);
        Assert.Equal(5, entry.ScannedNpcCount);
    }

    [Fact]
    public async Task VersionMismatch_DropsCacheOnLoad()
    {
        using var tempDir = new TempDir("ModIssuesCacheVer");
        var path = tempDir.Combine("ModIssuesCache.json");

        var cache = new ModIssuesCache(path);
        cache.Load();
        cache.Store("My Mod", MakeResult());
        await cache.SaveAsync();

        // Simulate a rule change: bump the version number inside the file.
        var text = File.ReadAllText(path);
        text = text.Replace($"\"Version\": {ModIssuesCacheFile.CurrentVersion}",
            $"\"Version\": {ModIssuesCacheFile.CurrentVersion + 1000}");
        File.WriteAllText(path, text);

        var reloaded = new ModIssuesCache(path);
        reloaded.Load();
        Assert.Empty(reloaded.GetAllRaw());
    }

    [Fact]
    public void Prune_DropsDeadEntries_KeepsLive()
    {
        using var tempDir = new TempDir("ModIssuesCachePrune");
        var cache = new ModIssuesCache(tempDir.Combine("ModIssuesCache.json"));
        cache.Load();
        cache.Store("Alive Mod", MakeResult());
        cache.Store("Dead Mod", MakeResult());

        cache.Prune(new[] { "Alive Mod" });

        var raw = cache.GetAllRaw();
        Assert.True(raw.ContainsKey("Alive Mod"));
        Assert.False(raw.ContainsKey("Dead Mod"));
    }

    [Fact]
    public void KeyLookup_IsCaseInsensitive()
    {
        using var tempDir = new TempDir("ModIssuesCacheCase");
        var cache = new ModIssuesCache(tempDir.Combine("ModIssuesCache.json"));
        cache.Load();
        cache.Store("My Mod", MakeResult());
        Assert.True(cache.TryGetValid("my mod", MakeSnapshot(), MakeTrees(), out _));
    }

    [Fact]
    public void CorruptFile_YieldsEmptyCache()
    {
        using var tempDir = new TempDir("ModIssuesCacheCorrupt");
        var path = tempDir.WriteText("ModIssuesCache.json", "{ not valid json !!");
        var cache = new ModIssuesCache(path);
        cache.Load();
        Assert.Empty(cache.GetAllRaw());
    }

    [Fact]
    public void BuildLooseAssetTrees_AggregatesMeshesAndTextures()
    {
        using var tempDir = new TempDir("ModIssuesTrees");
        var modFolder = tempDir.Dir("MyMod");
        tempDir.WriteText(@"MyMod\meshes\armor\a.nif", "aaaa");
        tempDir.WriteText(@"MyMod\meshes\armor\b.nif", "bb");
        tempDir.WriteText(@"MyMod\textures\armor\a.dds", "cccccc");
        tempDir.WriteText(@"MyMod\scripts\ignored.pex", "x"); // outside meshes/textures

        var trees = ModIssuesCache.BuildLooseAssetTrees(new[] { modFolder });

        Assert.Equal(2, trees.Count);
        var meshes = trees.Single(t => t.Root.EndsWith("meshes", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, meshes.FileCount);
        Assert.Equal(6, meshes.TotalBytes);
        var textures = trees.Single(t => t.Root.EndsWith("textures", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, textures.FileCount);

        // Editing a file changes the aggregates (invalidation signal).
        tempDir.WriteText(@"MyMod\meshes\armor\a.nif", "aaaaaaaa");
        var treesAfter = ModIssuesCache.BuildLooseAssetTrees(new[] { modFolder });
        Assert.False(treesAfter.Single(t => t.Root.EndsWith("meshes", StringComparison.OrdinalIgnoreCase))
            .Equals(meshes));
    }

    [Fact]
    public void BuildLooseAssetTrees_MissingFolder_YieldsNoEntries()
    {
        var trees = ModIssuesCache.BuildLooseAssetTrees(new[] { @"C:\Definitely\Not\A\Real\Folder\XYZ" });
        Assert.Empty(trees);
    }
}
