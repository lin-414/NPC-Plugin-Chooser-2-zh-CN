using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins the once-per-SESSION missing-asset re-render claim in
/// <see cref="MugshotStalenessChecker"/>. The missing-asset staleness gates exist
/// so a NEW session can pick up files the user installed since the PNG was
/// stamped — but the Mod Issues tab rebuilds its tile set on every filter tweak,
/// and an unlatched gate re-rendered every stamped tile per tweak
/// (user-reported 2026-08-21). A stamped PNG must grade STALE exactly once per
/// session, the claim is shared between the NPC and outfit stamps (one render
/// re-resolves both), and the explicit Generate-All reset re-arms it.
/// </summary>
public class MugshotStalenessMissingAssetClaimTests
{
    /// <summary>Writes a real 1×1 PNG via the SAME Pngcs library
    /// <see cref="MugshotPngMetadata.InjectParameters"/> reads with — its reader is
    /// stricter than WPF's decoder, so a hand-rolled minimal PNG won't round-trip.</summary>
    private static void WriteOnePixelPng(string path)
    {
        var imgInfo = new Hjg.Pngcs.ImageInfo(1, 1, 8, true);
        using var fs = File.Create(path);
        var writer = new Hjg.Pngcs.PngWriter(fs, imgInfo);
        writer.WriteRow(new Hjg.Pngcs.ImageLine(imgInfo), 0);
        writer.End();
    }

    private static readonly FormKey Npc = FormKey.Factory("000ABC:Test.esp");

    /// <summary>A checker whose settings neutralize every OTHER staleness gate, so
    /// the missing-asset gates are the only thing under test.</summary>
    private static MugshotStalenessChecker MakeChecker(out Settings settings)
    {
        settings = new Settings
        {
            SelectedRenderer = MugshotRenderer.Internal,
            AutoUpdateMugshotsWithMissingAssets = true,
            AutoUpdateMugshotsWithMissingOutfitAssets = true,
            AutoUpdateOldMugshots = false,
            AutoUpdateStaleMugshots = false,
        };
        settings.InternalMugshot.IncludeDefaultOutfit = false;
        settings.InternalMugshot.IncludeHeadgear = false;
        settings.InternalMugshot.ShowMissingNpcAssetsIcon = true;
        settings.InternalMugshot.ShowMissingOutfitAssetsIcon = true;
        settings.InternalMugshot.ShowDataFolderAssetsIcon = true;
        // PortraitCreator is only consulted on the Legacy branch, which
        // SelectedRenderer=Internal never takes.
        return new MugshotStalenessChecker(settings, Reflect.Uninitialized<PortraitCreator>());
    }

    private static string WriteStampedPng(TempDir tmp, Settings settings, string name,
        IReadOnlyList<string>? missingTextures = null,
        IReadOnlyList<string>? missingOutfitAssets = null)
    {
        string path = Path.Combine(tmp.Path, name);
        WriteOnePixelPng(path);
        string json = InternalMugshotMetadata.Build(
            Npc, settings.InternalMugshot,
            effectiveIncludeDefaultOutfit: false, effectiveIncludeHeadgear: false,
            effectiveOutfitIdentity: "none",
            missingTextures: missingTextures,
            missingOutfitAssets: missingOutfitAssets);
        MugshotPngMetadata.InjectParameters(path, json);
        return path;
    }

    [Fact]
    public void MissingAssetStamp_GradesStaleOncePerSession()
    {
        using var tmp = new TempDir();
        var checker = MakeChecker(out var settings);
        string stamped = WriteStampedPng(tmp, settings, "stamped.png",
            missingTextures: new[] { @"textures\gone.dds" });
        string clean = WriteStampedPng(tmp, settings, "clean.png");

        checker.NeedsRegeneration(stamped, Npc).Should().BeTrue(
            "the session's first check grants the heal-chance re-render");
        checker.NeedsRegeneration(stamped, Npc).Should().BeFalse(
            "nothing can have installed the files between two checks in one session — " +
            "a Mod Issues filter tweak must not re-render the tile");
        checker.NeedsRegeneration(clean, Npc).Should().BeFalse("no stamps, nothing to heal");
    }

    [Fact]
    public void ClaimIsSharedAcrossNpcAndOutfitStamps_AndResetRearms()
    {
        using var tmp = new TempDir();
        var checker = MakeChecker(out var settings);
        string both = WriteStampedPng(tmp, settings, "both.png",
            missingTextures: new[] { @"textures\gone.dds" },
            missingOutfitAssets: new[] { @"Outfit texture not found: textures\outfit.dds" });

        checker.NeedsRegeneration(both, Npc).Should().BeTrue();
        // One render re-resolves everything the PNG depicts — the outfit gate must
        // not spend a second render on the same file.
        checker.NeedsRegeneration(both, Npc).Should().BeFalse();

        checker.ResetMissingAssetRerenderClaims();
        checker.NeedsRegeneration(both, Npc).Should().BeTrue(
            "an explicit Generate-All batch is the user's 'check again now'");
    }
}
