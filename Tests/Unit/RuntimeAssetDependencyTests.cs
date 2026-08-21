using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Models;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// The runtime-asset-dependency feature's pure seams, no game install needed:
///
/// <list type="bullet">
/// <item><see cref="BsaHandler.LocateWinningEnabledArchive"/> — which archive the GAME would
/// take a referenced-but-not-copied asset from (enabled plugins only, latest-loading wins);
/// the winner's plugin is what the output gets mastered to.</item>
/// <item><see cref="OutputValidator.ValidateAssetDependencies"/> — the post-run mirror: the
/// token's recorded dependencies re-verified against the current load order + data folder
/// (missing/disabled master = Error, vanished loose file = grouped Warning).</item>
/// <item>The <see cref="NpcToken.AssetDependencies"/> ledger's JSON round-trip — the contract
/// between the patcher that writes it and the validator that reads it back.</item>
/// </list>
/// </summary>
public class RuntimeAssetDependencyTests
{
    private static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
    private static readonly ModKey EarlyMod = ModKey.FromNameAndExtension("EarlyMod.esp");
    private static readonly ModKey LateMod = ModKey.FromNameAndExtension("LateMod.esp");
    private static readonly ModKey DisabledMod = ModKey.FromNameAndExtension("Disabled.esp");

    private static readonly IReadOnlyList<ModKey> EnabledAscending =
        new List<ModKey> { Skyrim, EarlyMod, LateMod };

    /// <summary>Mirrors BsaLoadOrderPrecedenceTests.HandlerWith: a BsaHandler whose index is
    /// injected directly, since only _bsaContents backs the lookups under test.</summary>
    private static BsaHandler HandlerWith(
        params (ModKey Key, string BsaPath, string[] Files)[] archives)
    {
        var contents = new Dictionary<ModKey, Dictionary<string, HashSet<string>>>();
        foreach (var (key, bsaPath, files) in archives)
        {
            if (!contents.TryGetValue(key, out var byArchive))
            {
                byArchive = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                contents[key] = byArchive;
            }
            byArchive[bsaPath] = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        }

        var handler = Reflect.Uninitialized<BsaHandler>();
        Reflect.SetField(handler, "_bsaContents", contents);
        Reflect.SetField(handler, "_bsaContentsLock", new object());
        return handler;
    }

    // ─── LocateWinningEnabledArchive ─────────────────────────────────────

    [Fact]
    public void WinningArchive_LatestEnabledPluginWins()
    {
        var handler = HandlerWith(
            (Skyrim, @"C:\Game\Data\Skyrim - Textures0.bsa", new[] { @"textures\hair\ks.dds" }),
            (LateMod, @"C:\Game\Data\LateMod.bsa", new[] { @"textures\hair\ks.dds" }),
            (EarlyMod, @"C:\Game\Data\EarlyMod.bsa", new[] { @"textures\hair\ks.dds" }));

        var winner = handler.LocateWinningEnabledArchive(@"textures\hair\ks.dds", EnabledAscending);

        winner.Should().NotBeNull();
        winner!.Value.ModKey.Should().Be(LateMod,
            "the engine loads archives in plugin order, later winning — mastering must pin the plugin the game actually reads from");
    }

    [Fact]
    public void WinningArchive_DisabledOwnerIsInvisible()
    {
        var handler = HandlerWith(
            (DisabledMod, @"S:\mods\Disabled\Disabled.bsa", new[] { @"textures\hair\ks.dds" }));

        handler.LocateWinningEnabledArchive(@"textures\hair\ks.dds", EnabledAscending)
            .Should().BeNull("an archive no enabled plugin loads cannot satisfy a runtime dependency, " +
                             "however thoroughly it is indexed");
    }

    [Fact]
    public void WinningArchive_SamePluginTwoArchives_IsStable()
    {
        var handler = HandlerWith(
            (LateMod, @"C:\Game\Data\LateMod - Textures.bsa", new[] { @"textures\hair\ks.dds" }),
            (LateMod, @"C:\Game\Data\LateMod.bsa", new[] { @"textures\hair\ks.dds" }));

        var winner = handler.LocateWinningEnabledArchive(@"textures\hair\ks.dds", EnabledAscending);

        // Load order cannot separate one plugin's own archives; first-enumerated stays.
        winner!.Value.BsaPath.Should().Be(@"C:\Game\Data\LateMod - Textures.bsa");
    }

    [Fact]
    public void WinningArchive_EmptyInputs_AreNull()
    {
        var handler = HandlerWith(
            (EarlyMod, @"C:\Game\Data\EarlyMod.bsa", new[] { @"textures\a.dds" }));

        handler.LocateWinningEnabledArchive(@"textures\missing.dds", EnabledAscending).Should().BeNull();
        handler.LocateWinningEnabledArchive(@"textures\a.dds", Array.Empty<ModKey>()).Should().BeNull();
        handler.LocateWinningEnabledArchive("", EnabledAscending).Should().BeNull();
    }

    // ─── OutputValidator.ValidateAssetDependencies ───────────────────────

    private static List<ModListing<ISkyrimModGetter>> Listings(
        params (ModKey Key, bool Enabled)[] entries)
        => entries
            .Select(e => new ModListing<ISkyrimModGetter>(
                e.Key, new SkyrimMod(e.Key, SkyrimRelease.SkyrimSE), enabled: e.Enabled))
            .ToList();

    private static AssetDependencyLedger LedgerWithMaster(ModKey plugin, params string[] assets)
        => new()
        {
            ArchiveMasters =
            {
                new ArchiveDependency
                {
                    Plugin = plugin,
                    Archives = { plugin.Name + ".bsa" },
                    Assets = assets.ToList()
                }
            }
        };

    [Fact]
    public void Validate_NullOrEmptyLedger_ReportsNothing()
    {
        var result = new ValidationRunResult();
        var log = new System.Text.StringBuilder();
        var listings = Listings((Skyrim, true));

        OutputValidator.ValidateAssetDependencies(null, listings, @"C:\nowhere", result, log);
        OutputValidator.ValidateAssetDependencies(new AssetDependencyLedger(), listings, @"C:\nowhere", result, log);

        // Null = old-version token ("unknown"); empty = "no dependencies". Neither is a finding.
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_MasterPresentAndEnabled_ReportsNothing()
    {
        var result = new ValidationRunResult();
        var listings = Listings((Skyrim, true), (LateMod, true));

        OutputValidator.ValidateAssetDependencies(
            LedgerWithMaster(LateMod, @"textures\hair\ks.dds"),
            listings, @"C:\nowhere", result, new System.Text.StringBuilder());

        result.Issues.Should().BeEmpty("the dependency contract holds — nothing to report");
    }

    [Fact]
    public void Validate_MasterMissingFromLoadOrder_IsError()
    {
        var result = new ValidationRunResult();
        var listings = Listings((Skyrim, true));

        OutputValidator.ValidateAssetDependencies(
            LedgerWithMaster(LateMod, @"textures\hair\ks.dds"),
            listings, @"C:\nowhere", result, new System.Text.StringBuilder());

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ValidationSeverity.Error,
            "a missing master keeps the whole output plugin from loading at all");
        issue.Check.Should().Be(ValidationCheckKind.Asset);
        issue.Issue.Should().Contain(LateMod.FileName);
        issue.NpcFormKey.Should().BeEmpty("this is a run-level row, not tied to one NPC");
    }

    [Fact]
    public void Validate_MasterListedButDisabled_IsError()
    {
        var result = new ValidationRunResult();
        var listings = Listings((Skyrim, true), (LateMod, false));

        OutputValidator.ValidateAssetDependencies(
            LedgerWithMaster(LateMod, @"textures\hair\ks.dds"),
            listings, @"C:\nowhere", result, new System.Text.StringBuilder());

        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ValidationSeverity.Error);
        issue.Issue.Should().Contain("DISABLED");
    }

    [Fact]
    public void Validate_LooseFiles_PresentIsQuiet_MissingIsOneGroupedWarning()
    {
        using var tmp = new TempDir();
        string presentRel = @"textures\hair\present.dds";
        Directory.CreateDirectory(Path.Combine(tmp.Path, "textures", "hair"));
        File.WriteAllText(Path.Combine(tmp.Path, presentRel), "x");

        var ledger = new AssetDependencyLedger
        {
            LooseFiles = { presentRel, @"textures\hair\gone1.dds", @"textures\hair\gone2.dds" }
        };

        var result = new ValidationRunResult();
        OutputValidator.ValidateAssetDependencies(
            ledger, Listings((Skyrim, true)), tmp.Path, result, new System.Text.StringBuilder());

        // One grouped Warning for the two missing files — never a row per file.
        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ValidationSeverity.Warning,
            "vanished loose dependencies mean visible missing textures in game, but the plugin still loads");
        issue.Check.Should().Be(ValidationCheckKind.Asset);
        issue.Issue.Should().Contain("2 of 3");
    }

    // ─── Token ledger round-trip ─────────────────────────────────────────

    [Fact]
    public void TokenLedger_RoundTripsThroughJson()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "NPC_Token.json");

        var token = new NpcToken
        {
            CreationDate = "2026-08-21T00:00:00",
            AssetDependencies = new AssetDependencyLedger
            {
                ArchiveMasters =
                {
                    new ArchiveDependency
                    {
                        Plugin = LateMod,
                        Archives = { "LateMod.bsa", "LateMod - Textures.bsa" },
                        Assets = { @"textures\hair\ks.dds", @"meshes\hair\ks.nif" }
                    }
                },
                LooseFiles = { @"textures\hair\loose.dds" }
            }
        };

        JSONhandler<NpcToken>.SaveJSONFile(token, path, out bool saved, out string saveError);
        saved.Should().BeTrue(saveError);

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out bool ok, out string loadError);
        ok.Should().BeTrue(loadError);

        loaded!.AssetDependencies.Should().NotBeNull(
            "the ledger is the patcher→validator contract; losing it in serialization would silently disable the checks");
        var dep = loaded.AssetDependencies!.ArchiveMasters.Should().ContainSingle().Subject;
        dep.Plugin.Should().Be(LateMod);
        dep.Archives.Should().BeEquivalentTo("LateMod.bsa", "LateMod - Textures.bsa");
        dep.Assets.Should().BeEquivalentTo(@"textures\hair\ks.dds", @"meshes\hair\ks.nif");
        loaded.AssetDependencies.LooseFiles.Should().ContainSingle().Which.Should().Be(@"textures\hair\loose.dds");
    }

    [Fact]
    public void TokenLedger_AbsentInOldTokens_ReadsAsNull()
    {
        using var tmp = new TempDir();
        string path = Path.Combine(tmp.Path, "NPC_Token.json");
        // A token written by an app version that predates the ledger field.
        File.WriteAllText(path, "{\"CreationDate\":\"2025-01-01\",\"CreatedPlugins\":[]}");

        var loaded = JSONhandler<NpcToken>.LoadJSONFile(path, out bool ok, out string error);

        ok.Should().BeTrue(error);
        loaded!.AssetDependencies.Should().BeNull("readers must treat old tokens as 'unknown', never as 'no dependencies'");
    }
}
