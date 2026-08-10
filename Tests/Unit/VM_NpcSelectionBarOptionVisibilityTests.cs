using FluentAssertions;
using NPC_Plugin_Chooser_2.View_Models;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

public class VM_NpcSelectionBarOptionVisibilityTests
{
    [Theory]
    [InlineData(false, true, false, false, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, true)]
    [InlineData(true, false, true, false, false)]
    public void OptionVisibility_MatchesNpcTabToggles(
        bool hidden,
        bool hasInstalledData,
        bool showHiddenMods,
        bool showUninstalledMods,
        bool expected)
    {
        const string modName = "Appearance Mod";
        IReadOnlySet<string> globallyHidden = hidden
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modName }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        VM_NpcSelectionBar.IsAppearanceOptionVisible(
                modName,
                hasInstalledData,
                showHiddenMods,
                showUninstalledMods,
                globallyHidden,
                npcHiddenMods: null)
            .Should().Be(expected);
    }

    [Fact]
    public void PerNpcHiddenOption_IsExcludedWhenHiddenModsAreNotShown()
    {
        const string modName = "Appearance Mod";

        VM_NpcSelectionBar.IsAppearanceOptionVisible(
                modName,
                hasInstalledData: true,
                showHiddenMods: false,
                showUninstalledMods: true,
                globallyHiddenMods: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                npcHiddenMods: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modName })
            .Should().BeFalse();
    }
}
