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
/// </summary>
public class VM_ModIssueEntry : ReactiveObject
{
    public string DisplayName { get; }
    public VM_ModSetting SourceVm { get; }
    public ModIssueScanResult Result { get; }

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
        VM_Mods modsViewModel)
    {
        DisplayName = displayName;
        SourceVm = sourceVm;
        Result = result;

        IssuesByNpc = result.Issues
            .Where(i => !i.NpcFormKey.IsNull)
            .GroupBy(i => i.NpcFormKey)
            .ToDictionary(g => g.Key, g => g.ToList());
        ModLevelIssues = result.Issues.Where(i => i.NpcFormKey.IsNull).ToList();

        TotalIssueCount = result.Issues.Count;
        AffectedNpcCount = IssuesByNpc.Count;
        CountsByType = result.Issues
            .GroupBy(i => i.Type)
            .ToDictionary(g => g.Key, g => g.Count());
        CountsByTypeDisplay = CountsByType
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{GetIssueTypeDisplayName(kv.Key)}: {kv.Value}")
            .ToList();

        SummaryText = ModLevelIssues.Any(i => i.Type == ModIssueType.ModNotInstalled)
            ? "Mod not installed"
            : $"{TotalIssueCount} issue{(TotalIssueCount == 1 ? "" : "s")} · {AffectedNpcCount} NPC{(AffectedNpcCount == 1 ? "" : "s")}";
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
        _ => type.ToString(),
    };

    /// <summary>Builds the tile tooltip body for one NPC's issues, grouped by type.</summary>
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
                if (issue.Type == ModIssueType.DarkFaceMismatch && !string.IsNullOrEmpty(issue.Detail))
                {
                    sb.Append("\n   ").Append(issue.Detail.Replace("\n", "\n   "));
                }
            }
        }
        return sb.ToString();
    }
}
