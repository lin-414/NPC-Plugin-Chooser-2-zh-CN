using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;

namespace NPC_Plugin_Chooser_2.Models
{
    /// <summary>
    /// Classification of a problem the Mod Issues scanner can detect for one
    /// appearance mod. The scanner is render-free and engine-faithful: it checks
    /// the assets the game actually reads (FaceGen NIF + the texture paths baked
    /// inside rendered NIFs, ArmorAddon world models, AlternateTextures TXSTs)
    /// rather than every path a record happens to mention.
    /// </summary>
    public enum ModIssueType
    {
        /// <summary>The FaceGen geometry .nif for an NPC that should have one is
        /// absent from the mod, vanilla archives, and the live Data folder.</summary>
        MissingFaceGenMesh,

        /// <summary>The FaceGen tint .dds is absent everywhere the mesh rule looks.</summary>
        MissingFaceGenTint,

        /// <summary>FaceGen-vs-records mismatch (the in-game dark-face class):
        /// a head part the NPC's records resolve to has no baked shape in the
        /// FaceGen NIF, or vice versa. Detail carries the analyzer's reason text.</summary>
        DarkFaceMismatch,

        /// <summary>A mesh the engine would draw (body/hands/feet/hair/tail skin
        /// ARMA, or an outfit ArmorAddon world model) failed to resolve.</summary>
        MissingArmaMesh,

        /// <summary>The _0/_1 weight-sibling counterpart of a resolvable
        /// ArmorAddon world model is absent.</summary>
        MissingWeightSibling,

        /// <summary>A texture referenced by an AlternateTextures (MODS) entry's
        /// TextureSet failed to resolve.</summary>
        MissingAltTexture,

        /// <summary>A texture path baked inside a rendered NIF (FaceGen head or
        /// ARMA world model) failed to resolve. Grouped per shape via
        /// <see cref="ModIssue.ShapeName"/>.</summary>
        MissingNifTexture,

        /// <summary>The mod's folders are missing or empty on disk — the mod is
        /// effectively uninstalled. Emitted once per mod instead of flooding one
        /// issue per NPC asset.</summary>
        ModNotInstalled,
    }

    /// <summary>One detected problem. NpcFormKey is FormKey.Null for mod-level
    /// issues (<see cref="ModIssueType.ModNotInstalled"/>).</summary>
    public class ModIssue
    {
        public ModIssueType Type { get; set; }
        public FormKey NpcFormKey { get; set; } = FormKey.Null;
        public string? NpcDisplayName { get; set; }

        /// <summary>The Data-relative path that failed to resolve (or, for
        /// <see cref="ModIssueType.DarkFaceMismatch"/>, the FaceGen NIF inspected).</summary>
        public string AffectedPath { get; set; } = string.Empty;

        /// <summary>For NIF-internal texture issues: the NIF whose shape referenced
        /// <see cref="AffectedPath"/>.</summary>
        public string? NifPath { get; set; }

        /// <summary>For NIF-internal texture issues: the shape that referenced the texture.</summary>
        public string? ShapeName { get; set; }

        /// <summary>Human-readable identity of the referencing record when the issue
        /// came from a record walk (e.g. "ArmorAddon 'SteelCuirassAA' [123456:Mod.esp]").</summary>
        public string? ReferencingRecord { get; set; }

        /// <summary>Extra human-readable context for tooltips/CSV.</summary>
        public string? Detail { get; set; }

        /// <summary>True when the issue belongs to the NPC's outfit/headgear
        /// (the attire mesh-override walk) rather than the NPC's own face/body
        /// assets. Drives which badge the tile shows (Missing Outfit Assets vs
        /// Missing Asset) and the "include outfit-only issues" filter.</summary>
        public bool IsOutfitIssue { get; set; }
    }

    /// <summary>
    /// Aggregate snapshot of a mod folder's general asset trees (meshes\ and
    /// textures\ under each corresponding folder). Widens
    /// <see cref="ModStateSnapshot"/>, which only tracks plugins, BSAs and the
    /// two FaceGen directories, so the issues cache also invalidates when loose
    /// non-FaceGen assets change. Aggregates (count + bytes + max mtime) rather
    /// than per-file lists keep the cache file small.
    /// </summary>
    public class LooseAssetTreeSnapshot
    {
        public string Root { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public DateTime MaxLastWriteUtc { get; set; }

        public bool Equals(LooseAssetTreeSnapshot? other)
        {
            if (other == null) return false;
            return Root.Equals(other.Root, StringComparison.OrdinalIgnoreCase) &&
                   FileCount == other.FileCount &&
                   TotalBytes == other.TotalBytes &&
                   MaxLastWriteUtc == other.MaxLastWriteUtc;
        }

        public override bool Equals(object? obj) => Equals(obj as LooseAssetTreeSnapshot);
        public override int GetHashCode() => HashCode.Combine(Root.ToLowerInvariant(), FileCount, TotalBytes, MaxLastWriteUtc);
    }

    /// <summary>One mod's scan output — one entry in the on-disk cache.</summary>
    public class ModIssueScanResult
    {
        public DateTime ScanTimeUtc { get; set; }

        /// <summary>State of the mod's plugins/BSAs/FaceGen dirs at scan time;
        /// compared by value against a fresh snapshot to decide cache validity.</summary>
        public ModStateSnapshot? Snapshot { get; set; }

        /// <summary>State of the mod's general meshes\ / textures\ loose trees at scan time.</summary>
        public List<LooseAssetTreeSnapshot> LooseAssetTrees { get; set; } = new();

        /// <summary>False when the scan of this mod was cancelled mid-way — such an
        /// entry is never treated as a valid cache hit.</summary>
        public bool ScanCompleted { get; set; }

        public int ScannedNpcCount { get; set; }

        public List<ModIssue> Issues { get; set; } = new();
    }

    /// <summary>Root of ModIssuesCache.json (written next to the exe).</summary>
    public class ModIssuesCacheFile
    {
        /// <summary>Bump whenever the scan rules change so stale verdicts are
        /// invalidated globally.</summary>
        // v2: weight siblings gated on the ARMA weight-slider flag; issues carry
        //     the outfit/base split (IsOutfitIssue).
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Keyed by ModSetting.DisplayName.</summary>
        public Dictionary<string, ModIssueScanResult> Mods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
