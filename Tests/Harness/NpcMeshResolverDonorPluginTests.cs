using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Harness;

/// <summary>
/// <see cref="NpcMeshResolver.SelectDonorPluginsToIndex"/> — decides which plugins' Data-folder
/// BSAs to index so the renderer can resolve an outfit that came from OUTSIDE the appearance
/// mod (SkyPatcher / SPID / the conflict winner).
///
/// The rule exists because every other writer of the BSA index takes a <c>ModSetting</c>, and a
/// mod only becomes a ModSetting if it ships FaceGen — so an armor mod that is active in the
/// load order but carries no faces had its archives invisible, and its outfits rendered as
/// nothing. Selection is: donors present in the load order, plus one level of their masters,
/// minus base game and Creation Club (indexed at startup already).
///
/// Pure and static, so these run with no game install, no environment and no archive I/O.
/// </summary>
public class NpcMeshResolverDonorPluginTests
{
    private static ModKey Mk(string fileName) => MutagenFixtures.Mk(fileName);

    /// <summary>A load-order listing whose mod loaded and declares <paramref name="masters"/>.</summary>
    private static (ModKey, IReadOnlyList<ModKey>?) Listing(string fileName, params string[] masters)
        => (Mk(fileName), masters.Select(Mk).ToList());

    /// <summary>A listing whose mod could not be loaded — masters are unknown (null).</summary>
    private static (ModKey, IReadOnlyList<ModKey>?) Unloaded(string fileName)
        => (Mk(fileName), null);

    private static readonly ModKey[] Vanilla =
    {
        Mk("Skyrim.esm"), Mk("Update.esm"), Mk("Dawnguard.esm"),
        Mk("HearthFires.esm"), Mk("Dragonborn.esm"),
    };

    private static HashSet<ModKey> Select(
        IEnumerable<ModKey> donors,
        IEnumerable<(ModKey, IReadOnlyList<ModKey>?)> loadOrder,
        IEnumerable<ModKey>? cc = null)
        => NpcMeshResolver.SelectDonorPluginsToIndex(donors.ToList(), loadOrder, Vanilla, cc);

    private static IEnumerable<string> Names(IEnumerable<ModKey> keys)
        => keys.Select(k => k.FileName.String);

    // -------------------------------------------------------------------------------------
    // Empty / null inputs short-circuit.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void NoDonors_ReturnsEmpty()
    {
        Select(new ModKey[0], new[] { Listing("Armor.esp") }).Should().BeEmpty();
    }

    [Fact]
    public void NullLoadOrder_ReturnsEmpty()
    {
        NpcMeshResolver.SelectDonorPluginsToIndex(new[] { Mk("Armor.esp") }, null!, Vanilla, null)
            .Should().BeEmpty();
    }

    [Fact]
    public void NullVanillaAndCcLists_AreTolerated()
    {
        NpcMeshResolver.SelectDonorPluginsToIndex(
                new[] { Mk("Armor.esp") }, new[] { Listing("Armor.esp") }, null, null)
            .Should().ContainSingle().Which.Should().Be(Mk("Armor.esp"));
    }

    // -------------------------------------------------------------------------------------
    // The core case: the reported Apachii shape.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void DonorInLoadOrder_IsSelected()
    {
        var result = Select(
            new[] { Mk("Apachii_DivineEleganceStore.esm") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("Apachii_DivineEleganceStore.esm", "Skyrim.esm", "Update.esm"),
            });

        Names(result).Should().BeEquivalentTo("Apachii_DivineEleganceStore.esm");
    }

    [Fact]
    public void DonorAbsentFromLoadOrder_IsDropped()
    {
        // The game would not load its archive either, so neither do we.
        var result = Select(
            new[] { Mk("NotInstalled.esp") },
            new[] { Listing("Skyrim.esm"), Listing("Armor.esp") });

        result.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------
    // Masters: one level deep, because a plugin's records routinely point at assets that
    // ship in the parent mod it masters (the "De-Standalone" shape).
    // -------------------------------------------------------------------------------------

    [Fact]
    public void MastersOfADonor_AreSelected()
    {
        var result = Select(
            new[] { Mk("DeStandalone.esp") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("ParentMod.esp", "Skyrim.esm"),
                Listing("DeStandalone.esp", "Skyrim.esm", "ParentMod.esp"),
            });

        Names(result).Should().BeEquivalentTo("DeStandalone.esp", "ParentMod.esp");
    }

    [Fact]
    public void MastersAreOnlyOneLevelDeep()
    {
        // GrandParent is a master of Parent, but Parent is only a MASTER here, not a donor —
        // so its own masters are not harvested.
        var result = Select(
            new[] { Mk("Child.esp") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("GrandParent.esp", "Skyrim.esm"),
                Listing("Parent.esp", "Skyrim.esm", "GrandParent.esp"),
                Listing("Child.esp", "Skyrim.esm", "Parent.esp"),
            });

        Names(result).Should().BeEquivalentTo("Child.esp", "Parent.esp");
    }

    [Fact]
    public void UnloadedListing_StillSelectsThePluginItself()
    {
        // Masters unknown (Mod was null), but the plugin's own archive is still worth indexing.
        var result = Select(
            new[] { Mk("Armor.esp") },
            new[] { Listing("Skyrim.esm"), Unloaded("Armor.esp") });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    // -------------------------------------------------------------------------------------
    // Vanilla / Creation Club are already indexed at startup — never re-offer them.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void BaseGamePlugins_AreFilteredOut_EvenAsDirectDonors()
    {
        var result = Select(
            new[] { Mk("Skyrim.esm"), Mk("Dawnguard.esm"), Mk("Armor.esp") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("Dawnguard.esm", "Skyrim.esm"),
                Listing("Armor.esp", "Skyrim.esm"),
            });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void VanillaMastersOfADonor_AreFilteredOut()
    {
        // Nearly every mod masters Skyrim.esm; harvesting masters must not drag it back in.
        var result = Select(
            new[] { Mk("Armor.esp") },
            new[] { Listing("Skyrim.esm"), Listing("Armor.esp", "Skyrim.esm", "Update.esm") });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void CreationClubPlugins_AreFilteredOut()
    {
        var result = Select(
            new[] { Mk("ccBGSSSE001-Fish.esm"), Mk("Armor.esp") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("ccBGSSSE001-Fish.esm", "Skyrim.esm"),
                Listing("Armor.esp", "Skyrim.esm"),
            },
            cc: new[] { Mk("ccBGSSSE001-Fish.esm") });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    // -------------------------------------------------------------------------------------
    // Shape / dedup.
    // -------------------------------------------------------------------------------------

    [Fact]
    public void SharedMasterAcrossDonors_IsSelectedOnce()
    {
        var result = Select(
            new[] { Mk("AddonA.esp"), Mk("AddonB.esp") },
            new[]
            {
                Listing("Skyrim.esm"),
                Listing("Shared.esm", "Skyrim.esm"),
                Listing("AddonA.esp", "Skyrim.esm", "Shared.esm"),
                Listing("AddonB.esp", "Skyrim.esm", "Shared.esm"),
            });

        Names(result).Should().BeEquivalentTo("AddonA.esp", "AddonB.esp", "Shared.esm");
    }

    [Fact]
    public void NullModKey_IsNeverReturned()
    {
        var donors = new List<ModKey> { ModKey.Null, Mk("Armor.esp") };
        var result = Select(donors, new[] { Listing("Skyrim.esm"), Listing("Armor.esp") });

        result.Should().NotContain(ModKey.Null);
        Names(result).Should().BeEquivalentTo("Armor.esp");
    }

    [Fact]
    public void DonorsNotInLoadOrderDoNotSuppressOnesThatAre()
    {
        var result = Select(
            new[] { Mk("Missing.esp"), Mk("Armor.esp") },
            new[] { Listing("Skyrim.esm"), Listing("Armor.esp", "Skyrim.esm") });

        Names(result).Should().BeEquivalentTo("Armor.esp");
    }
}
