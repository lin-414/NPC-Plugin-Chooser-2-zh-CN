using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// <c>RecordHandler</c>'s Origin fallback must treat the vanilla masters as a FAMILY and
/// resolve winner-first (Dragonborn → … → Skyrim): the DLC are guaranteed present and
/// canonically override earlier masters' records. The bug this pins (Sybille Stentor /
/// Botox, 2026-08-16): the Mod Issues consistency check resolved BretonRaceVampire from
/// Skyrim.esm alone and graded the mod's FaceGen against the pre-Dawnguard default head
/// parts — flagging a dark-face mismatch the engine never sees, because in game Dawnguard's
/// race override supplies the vampire eyes the mesh was actually baked with.
///
/// <para>Real plugin files resolved through a real PluginProvider, like
/// <see cref="ValidatorInjectedRecordTests"/>. Fake vanilla masters live in the fallback
/// folder, which is consulted before the game data folder, so no Skyrim install is
/// touched and the walk stays hermetic.</para>
/// </summary>
public class RecordHandlerOriginFamilyTests
{
    /// A FormID chosen not to collide with anything a real master would carry — but the
    /// fixture writes its own Skyrim.esm/Dawnguard.esm anyway, which win path resolution.
    private static readonly FormKey VampRace = FormKey.Factory("0FABCD:Skyrim.esm");

    private static readonly ModKey MyMod = ModKey.FromFileName("MyMod.esp");
    private static readonly ModKey OtherMod = ModKey.FromFileName("OtherMod.esp");
    private static readonly FormKey OwnPart = FormKey.Factory("000801:MyMod.esp");

    private static void WriteEmptyMaster(string folder, string fileName)
    {
        new SkyrimMod(ModKey.FromFileName(fileName), SkyrimRelease.SkyrimSE)
            .WriteToBinary(Path.Combine(folder, fileName));
    }

    private static string BuildFixture(TempDir dir, bool withDawnguardOverride)
    {
        var folder = dir.Path;

        var skyrim = new SkyrimMod(ModKey.FromFileName("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        skyrim.Races.Add(new Race(VampRace, SkyrimRelease.SkyrimSE) { EditorID = "VampRace_Vanilla" });
        skyrim.WriteToBinary(Path.Combine(folder, "Skyrim.esm"));

        // The whole family exists on disk so the winner-first walk never leaves this
        // folder for a real install's masters.
        WriteEmptyMaster(folder, "Update.esm");
        WriteEmptyMaster(folder, "HearthFires.esm");
        WriteEmptyMaster(folder, "Dragonborn.esm");

        if (withDawnguardOverride)
        {
            var dawnguard = new SkyrimMod(ModKey.FromFileName("Dawnguard.esm"), SkyrimRelease.SkyrimSE);
            dawnguard.Races.Add(new Race(VampRace, SkyrimRelease.SkyrimSE) { EditorID = "VampRace_Dawnguard" });
            dawnguard.WriteToBinary(Path.Combine(folder, "Dawnguard.esm"));
        }
        else
        {
            WriteEmptyMaster(folder, "Dawnguard.esm");
        }

        var myMod = new SkyrimMod(MyMod, SkyrimRelease.SkyrimSE);
        myMod.HeadParts.Add(new HeadPart(OwnPart, SkyrimRelease.SkyrimSE) { EditorID = "OwnPart_MyMod" });
        myMod.WriteToBinary(Path.Combine(folder, MyMod.FileName));

        // A third-party override of MyMod's record: must never be consulted by Origin
        // mode — the family expansion is for vanilla masters only.
        var otherMod = new SkyrimMod(OtherMod, SkyrimRelease.SkyrimSE);
        otherMod.HeadParts.Add(new HeadPart(OwnPart, SkyrimRelease.SkyrimSE) { EditorID = "OwnPart_Foreign" });
        otherMod.WriteToBinary(Path.Combine(folder, OtherMod.FileName));

        return folder;
    }

    private static RecordHandler MakeHandler()
    {
        var settings = new Settings();
        var env = Reflect.Uninitialized<EnvironmentStateProvider>();
        Reflect.SetField(env, "SkyrimVersion", SkyrimRelease.SkyrimSE);
        return new RecordHandler(env, new PluginProvider(env, settings), settings);
    }

    private static IMajorRecordGetter? ResolveOrigin(RecordHandler handler, FormKey fk, string folder)
    {
        handler.TryGetRecordFromMods(fk.ToLinkGetter<IMajorRecordGetter>(), Enumerable.Empty<ModKey>(),
            new HashSet<string>(new[] { folder }, StringComparer.OrdinalIgnoreCase),
            RecordHandler.RecordLookupFallBack.Origin, out var record);
        return record;
    }

    [Fact]
    public void OriginFallback_BaseGameFormKey_ResolvesTheFamilyWinner()
    {
        using var dir = new TempDir("originfamily");
        var folder = BuildFixture(dir, withDawnguardOverride: true);

        var record = ResolveOrigin(MakeHandler(), VampRace, folder);

        record.Should().NotBeNull();
        record!.EditorID.Should().Be("VampRace_Dawnguard",
            "Dawnguard's override of a Skyrim.esm record is what the engine resolves — grading against the origin's stale copy is the Sybille/Botox false positive");
    }

    [Fact]
    public void OriginFallback_BaseGameFormKey_FallsThroughToTheDefiner_WhenNoFamilyOverride()
    {
        using var dir = new TempDir("originfamily");
        var folder = BuildFixture(dir, withDawnguardOverride: false);

        var record = ResolveOrigin(MakeHandler(), VampRace, folder);

        record.Should().NotBeNull();
        record!.EditorID.Should().Be("VampRace_Vanilla");
    }

    [Fact]
    public void OriginFallback_ThirdPartyFormKey_NeverExpandsBeyondTheOrigin()
    {
        using var dir = new TempDir("originfamily");
        var folder = BuildFixture(dir, withDawnguardOverride: true);

        var record = ResolveOrigin(MakeHandler(), OwnPart, folder);

        record.Should().NotBeNull();
        record!.EditorID.Should().Be("OwnPart_MyMod",
            "Origin mode exists to keep third-party winners out of mod-scoped verdicts — only the vanilla-master family gets the winner-first walk");
    }
}
