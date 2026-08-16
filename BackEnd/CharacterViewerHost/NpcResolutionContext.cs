using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using NPC_Plugin_Chooser_2.BackEnd;

namespace NPC_Plugin_Chooser_2.BackEnd.CharacterViewerHost;

/// <summary>
/// Per-NPC resolution scope. When non-null, <see cref="NpcMeshResolver"/>
/// reads records from <see cref="PreferredModKeys"/> first (falling back to
/// the load-order winner via <see cref="RecordHandler"/>) and rebases asset
/// paths to absolute when the file is present under any
/// <see cref="PreferredFolderPaths"/> entry. Mirrors the behavior of the
/// legacy <c>PortraitCreator.FindNpcNifPath</c> + <c>AssetHandler.FindAssetSource</c>
/// pair for the in-process renderer.
/// </summary>
public sealed class NpcResolutionContext
{
    /// <summary>Plugins owned by the user-selected mod, in the order recorded by
    /// <see cref="Models.ModSetting.CorrespondingModKeys"/> (last = winner).</summary>
    public IReadOnlyList<ModKey> PreferredModKeys { get; init; } = System.Array.Empty<ModKey>();

    /// <summary>Mod data folders, in <see cref="Models.ModSetting.CorrespondingFolderPaths"/>
    /// order (last = override winner). Loose-file lookups iterate this in
    /// reverse so the override beats the base.</summary>
    public IReadOnlyList<string> PreferredFolderPaths { get; init; } = System.Array.Empty<string>();

    /// <summary>Folder name set passed to <see cref="RecordHandler"/> as the
    /// fall-back disk-discovery scope when the plugin isn't already cached.</summary>
    public HashSet<string> FallBackFolderNames { get; init; } = new();

    /// <summary>
    /// Plugins of the mod that ORIGINALLY added this NPC, consulted below the selected mod and
    /// above vanilla. A mod may legitimately ship a face tint without the mesh (or a record
    /// without either) and let the origin supply the rest — the FaceGen ladder sources exactly
    /// that case — so without the origin in scope the renderer draws a headless body for a whole
    /// class of otherwise healthy NPCs.
    /// </summary>
    public IReadOnlyList<ModKey> OriginModKeys { get; init; } = System.Array.Empty<ModKey>();

    /// <summary>Data folders of the origin mod, paired with <see cref="OriginModKeys"/>. Kept
    /// separate from <see cref="PreferredFolderPaths"/> so archive lookups stay folder-scoped to
    /// the mod that actually owns them.</summary>
    public IReadOnlyList<string> OriginFolderPaths { get; init; } = System.Array.Empty<string>();

    /// <summary>Set when <see cref="Models.ModSetting.NpcPluginDisambiguation"/>
    /// pins this NPC to a specific plugin within <see cref="PreferredModKeys"/>.
    /// Resolution tries this key first.</summary>
    public ModKey? DisambiguationModKey { get; init; }

    /// <summary>
    /// Where record resolution goes when the mod's own plugins don't carry the
    /// record. <see cref="RecordHandler.RecordLookupFallBack.Winner"/> (the
    /// default) consults the live load order — correct for RENDERING, which
    /// should show what the game would show. The consistency check
    /// (<see cref="NpcMeshResolver.ResolveNpcForConsistency"/>) uses
    /// <see cref="RecordHandler.RecordLookupFallBack.Origin"/> instead: it asks
    /// "does this mod's FaceGen match this mod's records", so a record the mod
    /// doesn't carry must come from the FormKey's DEFINING plugin, never from
    /// whichever unrelated mod happens to win the load order — a foreign winner
    /// (RS Children overriding child races) otherwise gets compared against the
    /// scanned mod's own NIF and files a guaranteed false mismatch under the
    /// scanned mod's name. Origin mode also makes those verdicts independent of
    /// the live load order (stable inside vs outside MO2).
    /// </summary>
    public RecordHandler.RecordLookupFallBack FallbackMode { get; init; } =
        RecordHandler.RecordLookupFallBack.Winner;
}
