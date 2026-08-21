using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Newtonsoft.Json.Linq;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the data-folder-asset stamp contract: assets a render pulled from the
/// data folder because they weren't in the depicted mod's Corresponding Mod
/// Folders (engine-order Tier 2 loose / Tier 3 broadcast hits, minus vanilla
/// paths) are informational ONLY — a keep-activated runtime dependency, not a
/// defect in the render. They must round-trip through their own metadata key,
/// must never surface as missing assets (base or outfit), must never make
/// <see cref="InternalMugshotMetadata.RecordsMissingInstallableAssets"/> true,
/// and must never perturb the settings hash — i.e. nothing about them may ever
/// re-stale a cached mugshot.
/// </summary>
public class DataFolderAssetMetadataTests
{
    private static readonly FormKey Npc = FormKey.Factory("01326F:Skyrim.esm");

    private static string BuildJson(IReadOnlyList<string>? dataFolderAssets) =>
        InternalMugshotMetadata.Build(
            Npc, new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: true, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            dataFolderAssets: dataFolderAssets);

    [Fact]
    public void Assets_RoundTrip_ThroughOwnKey()
    {
        var assets = new List<string>
        {
            @"textures\ks hairdos\hair\female\hair021.dds",
            @"meshes\actors\character\hair\ks_hair021.nif",
        };
        var json = BuildJson(assets);

        InternalMugshotMetadata.TryReadDataFolderAssets(json)
            .Should().BeEquivalentTo(assets);
    }

    [Fact]
    public void Assets_DoNotCountAsMissingAssets()
    {
        var json = BuildJson(new[] { @"textures\some\dependency.dds" });

        InternalMugshotMetadata.TryReadMissingAssets(json, out var meshes, out var textures);
        meshes.Should().BeEmpty();
        textures.Should().BeEmpty();
        InternalMugshotMetadata.TryReadMissingOutfitAssets(json).Should().BeEmpty();
    }

    [Fact]
    public void Assets_DoNotMakeTheRenderLookRepairable()
    {
        // The AG button scopes forced re-renders to PNGs whose stamps record
        // INSTALLABLE missing assets. A data-folder dependency is present and
        // rendering correctly — re-rendering it changes nothing.
        InternalMugshotMetadata.RecordsMissingInstallableAssets(
                BuildJson(new[] { @"textures\some\dependency.dds" }))
            .Should().BeFalse();
    }

    [Fact]
    public void Assets_DoNotPerturbSettingsHash()
    {
        // The staleness checker compares the stamped settings_hash against a
        // recomputed one — if the asset list leaked into the hash, every
        // stamped dependency would re-stale its mugshot each session.
        var withAssets = JObject.Parse(BuildJson(new[] { @"textures\a.dds" }));
        var without = JObject.Parse(BuildJson(null));

        withAssets["settings_hash"]!.Value<string>()
            .Should().Be(without["settings_hash"]!.Value<string>());
    }

    [Fact]
    public void Assets_EmptyList_OmitsKeyEntirely()
    {
        // Matches the other per-render arrays: the common success case stays
        // small and older readers see no unfamiliar key.
        JObject.Parse(BuildJson(new List<string>()))
            .Should().NotContainKey("data_folder_assets");
        JObject.Parse(BuildJson(null))
            .Should().NotContainKey("data_folder_assets");
    }

    [Fact]
    public void Read_AbsentKeyOrMalformedJson_YieldsEmpty()
    {
        // Pre-existing PNGs stamped before this key existed.
        InternalMugshotMetadata.TryReadDataFolderAssets(BuildJson(null))
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadDataFolderAssets("not json {{{")
            .Should().BeEmpty();
        InternalMugshotMetadata.TryReadDataFolderAssets("")
            .Should().BeEmpty();
    }

    [Fact]
    public void OtherStampArrays_DoNotLeakIntoDataFolderAssets()
    {
        var json = InternalMugshotMetadata.Build(
            Npc, new InternalMugshotSettings(),
            effectiveIncludeDefaultOutfit: true, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            missingMeshes: new[] { @"meshes\missing.nif" },
            missingTextures: new[] { @"textures\missing.dds" },
            physicsConfigNotices: new[] { "a physics notice" },
            missingOutfitAssets: new[] { "an outfit asset" });

        InternalMugshotMetadata.TryReadDataFolderAssets(json).Should().BeEmpty();
    }
}
