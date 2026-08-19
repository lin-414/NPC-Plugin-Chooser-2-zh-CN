using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.Models;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace NPC_Plugin_Chooser_2.View_Models;

/// <summary>
/// One row in the Mod Issues tab's left panel: a mod with at least one
/// scan-detected issue, with per-type counts and the per-NPC issue grouping the
/// mugshot panel displays. Plain ReactiveObject constructed directly by
/// <see cref="VM_ModIssues"/> — no DI registration needed.
///
/// <para>Counts and groupings are computed over <see cref="VisibleIssues"/> —
/// the subset that survives the owner's severity (Show notes) and ignore-list
/// filters — while <see cref="Result"/> keeps the raw scan output for CSV
/// export. The owner rebuilds entries whenever those filters change.</para>
/// </summary>
public class VM_ModIssueEntry : ReactiveObject
{
    public string DisplayName { get; }
    public VM_ModSetting SourceVm { get; }
    public ModIssueScanResult Result { get; }

    /// <summary>Issues that pass the owner's severity/ignore filters — the set
    /// every count, tile and table row on this entry is built from.</summary>
    public IReadOnlyList<ModIssue> VisibleIssues { get; }

    public int TotalIssueCount { get; }
    public int AffectedNpcCount { get; }
    public IReadOnlyDictionary<ModIssueType, int> CountsByType { get; }
    public IReadOnlyList<string> CountsByTypeDisplay { get; }
    public string SummaryText { get; }
    public string ScanTimeText { get; }

    /// <summary>Issues grouped per NPC (mod-level issues excluded).</summary>
    public IReadOnlyDictionary<FormKey, List<ModIssue>> IssuesByNpc { get; }

    /// <summary>Mod-level issues (e.g. ModNotInstalled).</summary>
    public IReadOnlyList<ModIssue> ModLevelIssues { get; }

    /// <summary>True when the cached scan no longer matches the mod's on-disk
    /// state — the numbers shown may be outdated until the next Scan.</summary>
    [Reactive] public bool IsStale { get; set; }

    public ReactiveCommand<Unit, Unit> OpenInModsTabCommand { get; }

    public VM_ModIssueEntry(string displayName, VM_ModSetting sourceVm, ModIssueScanResult result,
        IReadOnlyList<ModIssue> visibleIssues, VM_Mods modsViewModel)
    {
        DisplayName = displayName;
        SourceVm = sourceVm;
        Result = result;
        VisibleIssues = visibleIssues;

        IssuesByNpc = visibleIssues
            .Where(i => !i.NpcFormKey.IsNull)
            .GroupBy(i => i.NpcFormKey)
            .ToDictionary(g => g.Key, g => g.ToList());
        ModLevelIssues = visibleIssues.Where(i => i.NpcFormKey.IsNull).ToList();

        TotalIssueCount = visibleIssues.Count;
        AffectedNpcCount = IssuesByNpc.Count;
        CountsByType = visibleIssues
            .GroupBy(i => i.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        CountsByTypeDisplay = CountsByType
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{GetIssueTypeDisplayName(kv.Key)}: {kv.Value}")
            .ToList();

        SummaryText = ModLevelIssues.Any(i => i.Type == ModIssueType.ModNotInstalled)
            ? "Mod not installed"
            : $"{TotalIssueCount} issue{(TotalIssueCount == 1 ? "" : "s")} · {AffectedNpcCount} NPC{(AffectedNpcCount == 1 ? "" : "s")}";
        if (result.FailedNpcCount > 0)
        {
            SummaryText += $" · ⚠ {result.FailedNpcCount} NPC{(result.FailedNpcCount == 1 ? "" : "s")} could not be scanned";
        }
        ScanTimeText = $"Scanned {result.ScanTimeUtc.ToLocalTime():g}";

        OpenInModsTabCommand = ReactiveCommand.Create(() => modsViewModel.NavigateToMod(SourceVm));
    }

    public static string GetIssueTypeDisplayName(ModIssueType type) => type switch
    {
        ModIssueType.MissingFaceGenMesh => "Missing FaceGen mesh",
        ModIssueType.MissingFaceGenTint => "Missing FaceGen tint",
        ModIssueType.DarkFaceMismatch => "Dark-face mismatch",
        ModIssueType.MissingArmaMesh => "Missing mesh",
        ModIssueType.MissingWeightSibling => "Missing weight sibling",
        ModIssueType.MissingAltTexture => "Missing alt texture",
        ModIssueType.MissingNifTexture => "Missing texture",
        ModIssueType.ModNotInstalled => "Mod not installed",
        ModIssueType.MissingHeadPartPlugin => "Missing required mod",
        _ => type.ToString(),
    };

    /// <summary>Builds the tile tooltip body for one NPC's issues, grouped by type.</summary>
    /// <summary>The repin-remedy sentence for a dark-face row whose sibling plugin(s)
    /// grade clean. Composed in exactly ONE place so the green table line, the tile
    /// tooltip, and the post-scan switch dialog agree. Empty when there is nothing to
    /// recommend.</summary>
    public static string BuildRepinRemedyText(IReadOnlyList<string>? cleanSiblingPlugins)
        => cleanSiblingPlugins is not { Count: > 0 }
            ? string.Empty
            : $"Plugin '{string.Join("', '", cleanSiblingPlugins)}' in this same mod does not have " +
              "this problem for this NPC — selecting it as the NPC's source plugin (right-click " +
              "the NPC's mugshot in the Mods tab) avoids the bug.";

    /// <summary>The plugin an NPC currently draws from within one mod entry: its
    /// disambiguation pin when one exists, otherwise — for an UNAMBIGUOUS NPC — the
    /// single entry of AvailablePluginsForNpcs. Pins are only materialized for
    /// multi-source NPCs, so a missing pin with exactly one available source is not
    /// "unknown", it IS the source (the Auri resource-only case, 2026-08-18: the sole
    /// source was the clean sibling, but the pin-blind remedy still advised the
    /// switch). Null when the NPC is unknown to the mod or genuinely ambiguous
    /// without a pin.</summary>
    public static string? GetEffectiveSourcePluginFileName(
        IReadOnlyDictionary<FormKey, ModKey> npcPluginDisambiguation,
        IReadOnlyDictionary<FormKey, List<ModKey>> availablePluginsForNpcs,
        FormKey npcKey)
    {
        if (npcPluginDisambiguation.TryGetValue(npcKey, out var pin))
            return pin.FileName.String;
        return availablePluginsForNpcs.TryGetValue(npcKey, out var sources) && sources.Count == 1
            ? sources[0].FileName.String
            : null;
    }

    /// <summary>Pin-aware variant for the results table: the row's advice depends on
    /// where the NPC's CURRENT source-plugin selection points. Pin among the row's
    /// broken carriers → the actionable switch sentence (with a count tail on grouped
    /// rows). Every known pin already a clean sibling → a reassurance instead — the
    /// row describes a plugin the user's selection does not use (the Hert confusion,
    /// 2026-08-18: her row kept advising a switch she had already made). Pins
    /// unknown or pointing elsewhere → the pin-blind base sentence.</summary>
    public static string BuildPinAwareRepinRemedyText(IReadOnlyList<string> cleanSiblingPlugins,
        IReadOnlyList<string>? recordPlugins, IReadOnlyList<string> currentPinFileNames)
    {
        string baseText = BuildRepinRemedyText(cleanSiblingPlugins);
        if (baseText.Length == 0) return baseText;
        if (currentPinFileNames.Count == 0 || recordPlugins is not { Count: > 0 }) return baseText;

        int brokenPinned = currentPinFileNames.Count(p =>
            recordPlugins.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (brokenPinned > 0)
        {
            return currentPinFileNames.Count > 1
                ? baseText + $" ({brokenPinned} of the {currentPinFileNames.Count} NPCs on this row " +
                  "currently draw from an affected plugin — the post-scan dialog offers the switch.)"
                : baseText;
        }

        int alreadyClean = currentPinFileNames.Count(p =>
            cleanSiblingPlugins.Contains(p, StringComparer.OrdinalIgnoreCase));
        if (alreadyClean == currentPinFileNames.Count)
        {
            return currentPinFileNames.Count > 1
                ? "Not affecting your current setup: every NPC on this row already draws from a " +
                  "source plugin that does not have this problem."
                : $"Not affecting your current setup: this NPC already draws from " +
                  $"'{currentPinFileNames[0]}', which does not have this problem.";
        }

        return baseText;
    }

    public static string BuildNpcIssueText(IReadOnlyList<ModIssue> issues)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Issues found by the mod scan:");
        foreach (var group in issues.GroupBy(i => i.Type).OrderBy(g => g.Key))
        {
            sb.Append('\n').Append(GetIssueTypeDisplayName(group.Key)).Append(':');
            foreach (var issue in group)
            {
                sb.Append("\n - ").Append(issue.AffectedPath);
                if (issue.Severity == ModIssueSeverity.Note) sb.Append(" (note)");
                if (!string.IsNullOrEmpty(issue.ShapeName))
                {
                    sb.Append(" (shape '").Append(issue.ShapeName).Append('\'');
                    if (!string.IsNullOrEmpty(issue.NifPath))
                    {
                        sb.Append(" in ").Append(System.IO.Path.GetFileName(issue.NifPath));
                    }
                    sb.Append(')');
                }
                else if (!string.IsNullOrEmpty(issue.ReferencingRecord))
                {
                    sb.Append(" (via ").Append(issue.ReferencingRecord).Append(')');
                }
                if (!string.IsNullOrEmpty(issue.SourceModName))
                {
                    sb.Append(" [from ").Append(issue.SourceModName).Append(']');
                }
                if (!string.IsNullOrEmpty(issue.RecordPluginName))
                {
                    sb.Append(" [record: ").Append(issue.RecordPluginName).Append(']');
                }
                if (issue.Type == ModIssueType.DarkFaceMismatch && !string.IsNullOrEmpty(issue.Detail))
                {
                    sb.Append("\n   ").Append(issue.Detail.Replace("\n", "\n   "));
                }
                var remedy = BuildRepinRemedyText(issue.CleanSiblingPlugins);
                if (remedy.Length > 0)
                {
                    sb.Append("\n   ").Append(remedy);
                }
            }
        }
        return sb.ToString();
    }
}
