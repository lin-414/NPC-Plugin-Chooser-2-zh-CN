using System;
using System.IO;
using FluentAssertions;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pins <see cref="ModsFolderAssetLocator"/> — the parent-Mods-folder cross-reference
/// behind out-of-scope asset attribution (Mod Issues scanner + Validate Output):
/// which mod folder(s) ship a loose relative path or a root-level archive, the
/// unavailable-folder degradation, and the prose formatting of provider lists.
/// </summary>
public class ModsFolderAssetLocatorTests
{
    [Fact]
    public void FindLooseProviders_NamesEveryShippingModFolder_Sorted()
    {
        using var mods = new TempDir();
        File.WriteAllText(mods.Combine("Zeta Hair", "textures", "hair", "ks.dds"), "x");
        File.WriteAllText(mods.Combine("Alpha Resources", "textures", "hair", "ks.dds"), "x");
        File.WriteAllText(mods.Combine("Unrelated", "textures", "other.dds"), "x");

        var locator = new ModsFolderAssetLocator(mods.Path);

        // Every shipping folder must be named (profile order decides the real
        // winner), sorted for determinism.
        locator.FindLooseProviders(@"textures\hair\ks.dds")
            .Should().Equal("Alpha Resources", "Zeta Hair");
        locator.FindLooseProviders(@"textures\hair\absent.dds").Should().BeEmpty();
    }

    [Fact]
    public void FindLooseProviders_NormalizesSeparators()
    {
        using var mods = new TempDir();
        File.WriteAllText(mods.Combine("Hair Mod", "textures", "hair", "ks.dds"), "x");

        var locator = new ModsFolderAssetLocator(mods.Path);

        // NIF-baked paths use either separator; the sweep must not care.
        locator.FindLooseProviders("textures/hair/ks.dds").Should().Equal("Hair Mod");
        locator.FindLooseProviders(@"\textures\hair\ks.dds").Should().Equal("Hair Mod");
    }

    [Fact]
    public void FindArchiveProviders_MatchesRootLevelArchivesOnly()
    {
        using var mods = new TempDir();
        File.WriteAllText(mods.Combine("KS Hairdos SSE", "KS Hairdo's.bsa"), "x");
        // An archive buried in a subfolder is not where the game loads from.
        File.WriteAllText(mods.Combine("Backup Mod", "old", "KS Hairdo's.bsa"), "x");

        var locator = new ModsFolderAssetLocator(mods.Path);

        locator.FindArchiveProviders("KS Hairdo's.bsa").Should().Equal("KS Hairdos SSE");
        // Full paths degrade to their file name so ledger/AssetSource paths both work.
        locator.FindArchiveProviders(@"C:\Game\Data\KS Hairdo's.bsa").Should().Equal("KS Hairdos SSE");
        locator.FindArchiveProviders("Absent.bsa").Should().BeEmpty();
    }

    [Fact]
    public void UnavailableModsFolder_IsEmptyAndSaysSo()
    {
        var unset = new ModsFolderAssetLocator(null);
        var missing = new ModsFolderAssetLocator(@"C:\definitely\not\a\real\mods\folder");

        unset.IsAvailable.Should().BeFalse();
        missing.IsAvailable.Should().BeFalse();
        unset.FindLooseProviders(@"textures\a.dds").Should().BeEmpty();
        missing.FindArchiveProviders("A.bsa").Should().BeEmpty(
            "callers phrase attribution off IsAvailable; the queries themselves must just degrade to empty");
    }

    [Fact]
    public void InvalidPathCharacters_DegradeToEmpty_NotThrow()
    {
        using var mods = new TempDir();
        Directory.CreateDirectory(Path.Combine(mods.Path, "Some Mod"));

        var locator = new ModsFolderAssetLocator(mods.Path);

        // Junk strings straight out of malformed NIFs must never sink a scan.
        locator.FindLooseProviders("textures\\bad\"name.dds").Should().BeEmpty();
        locator.FindLooseProviders("   ").Should().BeEmpty();
        locator.FindLooseProviders(null).Should().BeEmpty();
        locator.FindArchiveProviders(null).Should().BeEmpty();
    }

    [Fact]
    public void Queries_AreMemoized_PerPath()
    {
        using var mods = new TempDir();
        string file = mods.Combine("Hair Mod", "textures", "ks.dds");
        File.WriteAllText(file, "x");

        var locator = new ModsFolderAssetLocator(mods.Path);
        locator.FindLooseProviders(@"textures\ks.dds").Should().Equal("Hair Mod");

        // Same answer after the file vanishes: the locator is a per-run snapshot,
        // which is what makes the scanner's hundred-NPC repeats one sweep total.
        File.Delete(file);
        locator.FindLooseProviders(@"textures\ks.dds").Should().Equal("Hair Mod");
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "A" }, "'A'")]
    [InlineData(new[] { "A", "B" }, "'A' or 'B'")]
    [InlineData(new[] { "A", "B", "C" }, "'A', 'B' or 'C'")]
    public void FormatProviderList_ReadsAsProse(string[] providers, string expected)
    {
        ModsFolderAssetLocator.FormatProviderList(providers).Should().Be(expected);
    }

    [Fact]
    public void FormatProviderList_CapsLongLists()
    {
        var many = new[] { "A", "B", "C", "D", "E", "F" };
        ModsFolderAssetLocator.FormatProviderList(many)
            .Should().Be("'A', 'B', 'C' or 'D' (+2 more)");
    }

    // --- DataFolderAssetAttributor (blue-badge tooltip attribution) ---

    [Fact]
    public void ResolveProvidersCore_LooseWins_ArchiveNotConsulted()
    {
        using var mods = new TempDir();
        File.WriteAllText(mods.Combine("Hair Mod", "textures", "ks.dds"), "x");
        var locator = new ModsFolderAssetLocator(mods.Path);

        bool archiveAsked = false;
        var providers = DataFolderAssetAttributor.ResolveProvidersCore(
            @"textures\ks.dds", locator, _ => { archiveAsked = true; return null; });

        providers.Should().Equal("Hair Mod");
        archiveAsked.Should().BeFalse(
            "a loose hit already names the supplier — the engine reads loose before archives, and the archive probe costs an index");
    }

    [Fact]
    public void ResolveProvidersCore_ArchiveFallback_MapsWinningArchiveToItsModFolder()
    {
        using var mods = new TempDir();
        File.WriteAllText(mods.Combine("KS Hairdos SSE", "KS Hairdo's.bsa"), "x");
        var locator = new ModsFolderAssetLocator(mods.Path);

        var providers = DataFolderAssetAttributor.ResolveProvidersCore(
            @"textures\ks hairdo's\hair01.dds", locator, _ => "KS Hairdo's.bsa");
        providers.Should().Equal("KS Hairdos SSE");

        DataFolderAssetAttributor.ResolveProvidersCore(
                @"textures\nowhere.dds", locator, _ => null)
            .Should().BeEmpty("no loose provider and no winning archive = unattributable");
        DataFolderAssetAttributor.ResolveProvidersCore(
                @"textures\nowhere.dds", locator, _ => throw new InvalidOperationException("index broke"))
            .Should().BeEmpty("an archive-probe failure degrades to unattributed, never throws into the tooltip");
    }

    [Fact]
    public void ComposeNoticeText_AppendsProviderBrackets_OnlyForAttributedPaths()
    {
        var paths = new[] { @"textures\a.dds", @"textures\b.dds" };

        var plain = DataFolderAssetAttributor.ComposeNoticeText("My Mod", paths, providersByPath: null);
        plain.Should().Contain("My Mod's Corresponding Mod Folders:");
        plain.Should().Contain(@"textures\a.dds").And.NotContain("[from");

        var providers = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase)
        {
            [@"textures\a.dds"] = new[] { "KS Hairdos SSE", "Modpocalypse Resources" },
            [@"textures\b.dds"] = System.Array.Empty<string>(),
        };
        var enriched = DataFolderAssetAttributor.ComposeNoticeText("My Mod", paths, providers);
        enriched.Should().Contain(@"textures\a.dds  [from KS Hairdos SSE, Modpocalypse Resources]");
        enriched.Should().Contain(@"textures\b.dds");
        enriched.Should().NotContain(@"textures\b.dds  [from");

        var many = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase)
        {
            [@"textures\a.dds"] = new[] { "A", "B", "C", "D", "E" },
        };
        DataFolderAssetAttributor.ComposeNoticeText("My Mod", new[] { @"textures\a.dds" }, many)
            .Should().Contain("[from A, B, C, …]", "long provider lists cap like the scan's Provided By column");
    }
}
