using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>One checkable NPC row of the post-scan switch-suggestion dialog.</summary>
public sealed class VM_PluginSwitchProposalItem : ReactiveObject
{
    public VM_PluginSwitchProposalItem(VM_ModIssues.PluginSwitchProposal proposal)
    {
        Proposal = proposal;
        SwitchText = $"'{proposal.CurrentPluginFileName}'  →  '{proposal.TargetPluginFileName}'";
    }

    public VM_ModIssues.PluginSwitchProposal Proposal { get; }
    [Reactive] public bool IsChecked { get; set; } = true;
    public string NpcDisplayName => Proposal.NpcDisplayName;
    public string NpcFormKeyText => Proposal.NpcFormKey.ToString();
    public string SwitchText { get; }
}

/// <summary>One mod's collapsible group in the dialog.</summary>
public sealed class VM_PluginSwitchModGroup
{
    public VM_PluginSwitchModGroup(string modDisplayName, IEnumerable<VM_PluginSwitchProposalItem> items)
    {
        ModDisplayName = modDisplayName;
        Items = new ObservableCollection<VM_PluginSwitchProposalItem>(items);
        HeaderText = $"{ModDisplayName}  ({Items.Count} NPC{(Items.Count == 1 ? "" : "s")})";
    }

    public string ModDisplayName { get; }
    public ObservableCollection<VM_PluginSwitchProposalItem> Items { get; }
    public string HeaderText { get; }
}

/// <summary>
/// Backs the post-scan "switchable dark faces" dialog: per-mod collapsible groups of
/// NPCs whose CURRENT source plugin grades dark-face while a sibling plugin of the
/// same mod grades clean. Pure presentation — the window returns true from ShowDialog
/// and the caller (<see cref="VM_ModIssues"/>) applies <see cref="CheckedProposals"/>.
/// </summary>
public sealed class VM_PluginSwitchSuggestions
{
    public VM_PluginSwitchSuggestions(IReadOnlyList<VM_ModIssues.PluginSwitchProposal> proposals)
    {
        Groups = new ObservableCollection<VM_PluginSwitchModGroup>(proposals
            .GroupBy(p => p.ModDisplayName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VM_PluginSwitchModGroup(g.Key,
                g.OrderBy(p => p.NpcDisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new VM_PluginSwitchProposalItem(p)))));

        SummaryText =
            $"{proposals.Count} NPC{(proposals.Count == 1 ? "" : "s")} across {Groups.Count} " +
            $"mod{(Groups.Count == 1 ? "" : "s")} currently draw{(proposals.Count == 1 ? "s" : "")} from a " +
            "plugin whose records don't match the mod's own FaceGen (the dark-face bug), while another " +
            "plugin of the same mod matches. Applying changes only each NPC's source-plugin selection — " +
            "no rescan is needed, and you can change any of them back from the NPC's mugshot in the Mods tab.";
    }

    public ObservableCollection<VM_PluginSwitchModGroup> Groups { get; }
    public string SummaryText { get; }

    public IEnumerable<VM_ModIssues.PluginSwitchProposal> CheckedProposals() =>
        Groups.SelectMany(g => g.Items).Where(i => i.IsChecked).Select(i => i.Proposal);

    public void SetAll(bool isChecked)
    {
        foreach (var item in Groups.SelectMany(g => g.Items)) item.IsChecked = isChecked;
    }
}
