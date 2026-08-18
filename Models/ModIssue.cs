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

        /// <summary>The mod's records reference head parts from a plugin that is
        /// neither in the mod's own folders nor resolvable at all — a missing
        /// hard dependency (Eyes Nouveaux, KS Hairdo's, …). AffectedPath carries
        /// the absent plugin's filename so one dependency rolls up to one line
        /// per NPC instead of a wall of dark-face text; without this split, one
        /// absent plugin produced thousands of DarkFaceMismatch rows (measured:
        /// 2,929 for one mod).</summary>
        MissingHeadPartPlugin,
    }

    /// <summary>
    /// How visible a detected problem is in game. Mirrors the project's
    /// warning-severity bar: <see cref="Issue"/> is reserved for defects the
    /// player can actually see (untextured/invisible meshes, dark faces,
    /// missing dependencies); <see cref="Note"/> covers real-but-subtle
    /// findings (secondary texture-slot misses that even vanilla assets have),
    /// hidden by default behind a toggle.
    /// </summary>
    public enum ModIssueSeverity
    {
        Issue,
        Note,
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

        /// <summary>In-game visibility tier; <see cref="ModIssueSeverity.Note"/>
        /// rows are hidden by default. Serialized in the cache so the split
        /// survives sessions.</summary>
        public ModIssueSeverity Severity { get; set; } = ModIssueSeverity.Issue;

        /// <summary>Display name of the installed mod whose folder/BSA supplied
        /// the referencing NIF (outfit issues mostly come from the outfit mod,
        /// not the appearance replacer being scanned). Null when the provider is
        /// the scanned mod itself or could not be attributed.</summary>
        public string? SourceModName { get; set; }

        /// <summary>For record-graded issues (the dark-face class) in MULTI-plugin
        /// mods: the plugin filename(s) WITHIN the scanned mod whose record produced
        /// this verdict — plugins with identical records collapse to one row labeled
        /// with every carrier ("A.esp, B.esp"). Null for single-plugin mods, for
        /// file-side issues (which don't depend on the record), and for records
        /// supplied by the origin fallback — those rows render untagged, exactly the
        /// pre-v9 presentation. Distinct from <see cref="SourceModName"/>, which
        /// attributes a foreign INSTALLED mod ("Provided by").</summary>
        public string? RecordPluginName { get; set; }
    }

    /// <summary>
    /// One user-suppressed issue, persisted in Settings (NOT the scan cache, so
    /// ignores survive rescans and cache-version bumps). Two scopes exist:
    /// file-scope (NpcFormKey null — hides every occurrence of the path in the
    /// mod) and exact-scope (NpcFormKey set, plus Nif/Shape for NIF-texture
    /// rows). Paths are compared case-insensitively as stored.
    /// </summary>
    public class ModIssueIgnoreEntry
    {
        public string ModName { get; set; } = string.Empty;

        /// <summary>Global scope: the entry matches the path in EVERY mod
        /// (ModName is then informational only — the mod it was created from).
        /// For files like vanilla's own mouth subsurface maps that recur across
        /// the whole library.</summary>
        public bool AllMods { get; set; }

        public ModIssueType Type { get; set; }
        public string AffectedPath { get; set; } = string.Empty;
        public FormKey NpcFormKey { get; set; } = FormKey.Null;
        public string? NifPath { get; set; }
        public string? ShapeName { get; set; }

        public bool Matches(string modName, ModIssue issue)
        {
            if (!AllMods && !ModName.Equals(modName, StringComparison.OrdinalIgnoreCase)) return false;
            if (Type != issue.Type) return false;
            if (!AffectedPath.Equals(issue.AffectedPath, StringComparison.OrdinalIgnoreCase)) return false;
            if (NpcFormKey.IsNull) return true; // file scope
            if (!NpcFormKey.Equals(issue.NpcFormKey)) return false;
            if (NifPath != null && !NifPath.Equals(issue.NifPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            if (ShapeName != null && !ShapeName.Equals(issue.ShapeName ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
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

        /// <summary>NPCs whose scan threw and was swallowed — surfaced on the
        /// mod's row so a silent gap can't read as "clean".</summary>
        public int FailedNpcCount { get; set; }

        public List<ModIssue> Issues { get; set; } = new();
    }

    /// <summary>Root of ModIssuesCache.json (written next to the exe).</summary>
    public class ModIssuesCacheFile
    {
        /// <summary>Bump whenever the scan rules change so stale verdicts are
        /// invalidated globally.</summary>
        // v2: weight siblings gated on the ARMA weight-slider flag; issues carry
        //     the outfit/base split (IsOutfitIssue).
        // v3: slot-aware texture checks (face-tint slot skipped — engine proven
        //     canonical-only; secondary slots demoted to Notes), Issue/Note
        //     severity, origin-scoped dark-face resolution, missing-dependency
        //     rollup (MissingHeadPartPlugin), outfit source attribution.
        // v4: per-issue context enrichment — texture slot + biped partition +
        //     resolved-NIF source in Detail, same-type baked-shape pairing and
        //     record-source-aware remedies in dark-face text, template-terminus
        //     notes on FaceGen mesh/tint rows, WornArmor/race-skin referencers.
        // v5: dark-face recalibration from the 2026-08 Ysolda mutation matrix
        //     (docs/DarkFaceTriggerInvestigation-2026-08.md): forward misses are
        //     PROVEN triggers on a clean engine — hedges replaced with the
        //     verified claim + the FDF-masking explanation; duplicate-slot
        //     misses (slot satisfied by a baked same-type part) are the one
        //     verified-tolerated configuration and no longer produce rows.
        //     Also in v5: Origin resolution walks the vanilla-master family
        //     winner-first (Sybille/Botox false positive), and the dark-face
        //     text was compacted per user spec (scope-dependent headline, "has
        //     X instead", deduped orphan bullets; SelectedMod scope concludes
        //     with the authoring-issue line instead of a remedy menu).
        // v6: NIF-baked texture misses on every mesh reached through the worn
        //     ARMA chain (body/hands/feet/hair/tail) demoted to Note — those
        //     shapes are textured from the record chain's TextureSet at
        //     runtime (only FaceGen bakes are final), so the misses are
        //     invisible in game (Bijin Brelyna false positive). Race defaults
        //     of OverlayHeadPartList races (vampires) no longer enter the
        //     dark-face comparison (Bruma Vampire Fledgling false positive —
        //     in-game verified normal).
        // v7: refined engine model from the 20-mod spawn matrix + variant A7
        //     (doc field report 3): singular slots keep only the first-listed
        //     part, extra parts reconcile by presence not name, and a
        //     mod-shipped FaceGen at a Traits-templated NPC's own path (mod
        //     context) demotes to Note — the raw engine never loads it.
        // v8: extras-presence rule amended (doc field report 4): an unbaked
        //     extra sharing its baked ancestor's MODEL FILE (the vanilla
        //     "_1bit" beard-twin convention) is engine-inert and no longer
        //     produces a row (MQ304Ulfric / Men of Winter false positive,
        //     in-game verified 2026-08-17).
        // v9: multi-plugin mods grade the dark-face class PER PLUGIN record
        //     (identical records collapse to one row labeled with every
        //     carrier; ModIssue.RecordPluginName), so the verdict no longer
        //     silently follows the user's per-NPC source-plugin pin — the
        //     WICO field case: the two plugins carry different head-part
        //     sets for the same NPC and only one matches the shipped bake.
        //     Rows whose sibling plugin grades clean say so (repin remedy).
        // v10: singular-slot rule extended to EXTRAS (the flattened set,
        //      doc field report 5): an extra whose own singular Type is
        //      already occupied by an earlier part is dropped from the
        //      expected set — checks occupancy, never claims it. Both wild
        //      specimens (Men of Winter's "_1bit" FacialHair twin, Miggyluv
        //      Hjoromir's Eyebrows lashes) were CK-unbaked and in-game
        //      verified inert; A7's Misc-typed hairline still triggers.
        // v11: DFIR-derived adoptions (doc: external detector review).
        //      Player + 'Is CharGen Face Preset' NPCs skipped entirely
        //      (never placed; presets render from morph data, not FaceGen —
        //      preset-as-Traits-terminus still graded via its inheritors);
        //      present-but-unparseable FaceGen NIF now emits a dark-face
        //      row (was silent); ActorTypeGhost NPCs demote tint-symptom
        //      rows (dark-face, missing tint, broken NIF) to Note — the
        //      ghost effect usually hides them (user-observed), but mods
        //      can alter the effect, so Note rather than DFIR's skip.
        public const int CurrentVersion = 11;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Keyed by ModSetting.DisplayName.</summary>
        public Dictionary<string, ModIssueScanResult> Mods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
