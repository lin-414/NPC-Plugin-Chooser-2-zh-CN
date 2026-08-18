# Mod Issues Tab — Improvement Plan (2026-08)

Status: **IMPLEMENTED 2026-08-15** (all workstreams A–G; test suite 2447/2447
green; cache version bumped to 3 so the first tab open triggers a full rescan).
Remaining gates: the in-app before/after rescan and the external tester re-run
described in §5. The slot-6 question was settled by in-game A/B before
implementation — see `docs/FaceTintEngineTest-2026-08.md`.
Trigger: external tester report (2026-08-14) after running the scanner on their
(Vortex-managed) loadout, plus reproduction against the local setup. Tester CSV:
`C:\Users\Piranha\Downloads\ModIssues_20260814_231052.csv` (3,460 rows). Local
evidence: `bin\Debug\...\ModIssuesCache.json` (Aug-12 scan, 326 mods, ~40k issues)
and live `Settings.json`.

The tester found **no false positives among the issues they hand-checked** and
called the feature "great, a tool mod authors should run before publishing" — the
work below is noise reduction, two systematic false-positive classes, one
mis-attribution bug, and quality-of-life features. Nothing here weakens a check
that catches a real problem.

---

## 1. Diagnosis summary (all items reproduced/confirmed)

| # | Reported symptom | Root cause (confirmed) | Scale |
|---|---|---|---|
| 1 | "Issues with USSEP but details refer to RSSE Children headparts" | Consistency check resolves records the mod's own plugins don't carry via **load-order Winner fallback** — a foreign mod's race/NPC override gets compared against the scanned mod's own FaceGen NIF | 45 of the tester's 49 dark-face rows |
| 2 | NITHI facetint path "gets ignored" by the game | The flagged texture lives in **slot 6 (face tint)** of the head shape, and the tester is right: **PROVEN 2026-08-15 by in-game A/B** (`docs/FaceTintEngineTest-2026-08.md`) that the engine loads the tint exclusively from the canonical `facetint\<plugin>\<formid>.dds` path it constructs — the baked slot-6 string is never consulted, even when it points at a valid different file | 21 tester rows; **1,069 local rows** (Beyond Reach, Amulets of Skyrim, JH Additions, …) |
| 3 | Joke paths ("moneyistoiletpaper") "in fields that are usually empty" | **Same slot 6.** Verified by parsing the local `Literal Who - Female Bandits` NIF `00037c08.nif`: `slot 6: 'Data\moneyistoiletpaper'`, all real textures in slots 0–3/7 | tester-only rows; specimen NIFs exist locally |
| 4 | "Hundreds of issues, all the same three textures" | Scan is per-NPC; shared body/head NIFs repeat one missing file across every NPC | Local: 36,257 texture rows collapse to **3,834 distinct (mod, path) pairs (~9.5×)** |
| 5 | (unreported, found during repro) mouth/secondary-slot noise | `mouthhuman_s.dds`, `mouthhuman_sk.dds`, `teeth_e.dds` etc. are referenced by (near-)vanilla meshes but **not shipped by vanilla either** — the engine renders fine without secondary maps | **20,915 local rows = 58% of all texture rows**; tester's top rows are the same class |
| 6 | (unreported) one absent dependency floods dark-face rows | Familiar Faces: head parts point at `Eyes Nouveaux.esp` (not in mod folders / load order) → unresolved link **plus** a cascade: the unresolved part's slot type is unknown, so race defaults get added and reported missing too | **2,929 local rows for one mod** (77% of local dark-face rows) |
| 7 | Outfit issues blamed on the replacer mod | Attribution exists (`Category=Outfit`, `Referencer=Outfit:<FormKey>:<plugin>`), but nothing names **which installed mod supplies the outfit mesh**, and the UI doesn't explain the semantics | 1,247 of 3,460 tester rows are Outfit |

Key mechanism details, for the record:

- **#1:** `NpcMeshResolver.ResolveRecord` ([NpcMeshResolver.cs:2146-2205](../BackEnd/CharacterViewerHost/NpcMeshResolver.cs#L2146-L2205))
  resolves disambiguation → mod plugins → `RecordLookupFallBack.Winner` → outer
  `linkCache.TryResolve`. For the renderer that ladder is deliberate (render what
  the game would show). For `ResolveNpcForConsistency`
  ([NpcMeshResolver.cs:260-275](../BackEnd/CharacterViewerHost/NpcMeshResolver.cs#L260-L275))
  it poisons the comparison: scanning USSEP on a load order where RS Children is
  active resolves child races (and NPCs USSEP doesn't override) to RS Children's
  overrides, then compares them against USSEP's own vanilla-style child FaceGen →
  guaranteed mismatch, filed under USSEP. **Locally non-reproducing** because
  `RSkyrimChildren.esm` isn't in the `NPC Test` profile's `plugins.txt` at all —
  which also proves the scan verdict currently depends on the live load order
  (and therefore on whether the app ran inside MO2).
- **#2/#3:** the scanner's only facetint exclusion is a string-containment match
  against the NPC's *own* canonical tint path
  ([ModIssueScanner.cs:404-411](../BackEnd/CharacterViewerHost/ModIssueScanner.cs#L404-L411)),
  so an embedded path naming the *wrong plugin folder* (NIF reused from another
  NPC/plugin — extremely common) slips through. The in-game A/B
  (`docs/FaceTintEngineTest-2026-08.md`, run 2026-08-15) settled the engine
  model: **canonical-only** — slot 6 is never consulted, missing or valid. The
  canonical tint file is already checked separately (`MissingFaceGenTint`), so
  skipping slot-6 checks entirely loses nothing.
  - *History note:* the shared-FaceGen tint rewrite (`RewriteCopiedFaceTintPath`,
    36ab0f4) was built on the opposite premise and is engine-inert; what
    actually keeps shares/surrogates correct is the FaceGen ladder delivering
    the donor tint at the destination's canonical path (`DdsChoice`, 0cdc63f).
    The rewrite survives as NIF self-consistency hygiene; its comments claiming
    engine behavior should be corrected when next touched (see the test doc's
    reconciliation section).
- **#6 cascade:** `FaceGenConsistencyAnalyzer.Analyze` records occupied slot
  types via `resolveHeadPart(...)?.Type`
  ([FaceGenConsistencyAnalyzer.cs:596-628](../BackEnd/FaceGenConsistencyAnalyzer.cs#L596-L628));
  an unresolved link yields no type, its slot looks unoccupied, race defaults get
  walked in `fromRaceDefaults: true`, and each becomes an extra "missing baked
  shape" — one absent plugin produces 3+ findings per NPC.
- **`NifHandler.GetTexturesByShape`**
  ([NifHandler.cs:62-83](../BackEnd/NifHandler.cs#L62-L83)) already iterates
  slots 0–8 via `GetTexturePathByIndex` but **discards the slot index** — the
  one fact needed to fix #2/#3/#5. NifHandler is NPC2-local (nifly), so **no
  CV.R change or NuGet publish is needed** for any of this plan.

---

## 2. Workstreams

### A. Slot-aware NIF texture checks + severity tiers (fixes #2, #3, #5)

**Change**
1. Extend `NifHandler.GetTexturesByShape` to return per-texture **slot index**
   plus per-shape shader info (BSLightingShaderProperty type + the flag bits we
   act on, at minimum `Environment_Mapping`/eye-envmap and `Face`/facegen
   flags). Keep the existing tuple shape available (wrapper or update the 3
   `AssetHandler` call sites + tests in the same commit).
2. Scanner rules by slot (drawn shapes only, as today):
   - **Slot 6 on FaceGen head NIF shapes: never probed** — the engine loads
     the tint only from the canonical FormID-derived path (proven in-game,
     `docs/FaceTintEngineTest-2026-08.md`), which is independently checked
     (`MissingFaceGenTint`). Belt: any path matching
     `facegendata[\\/]facetint` is skipped wherever it appears. This alone
     deletes both reported false-positive classes.
   - **Slots 0 (diffuse) / 1 (normal): missing ⇒ Issue** (visibly broken in
     game — white/flat mesh). This preserves every "misspelling / genuinely
     missing file" catch the tester praised.
   - **Slots 4/5 (env cubemap/mask): skipped when the shader doesn't enable
     environment mapping; otherwise Note.**
   - **Slots 2/3/7/8 (glow/subsurface, detail/tint-palette, specular/backlight,
     spare): missing ⇒ Note** — real but subtle in game; this is the
     mouth/`teeth_e` family (58% of all local rows). Vanilla itself ships
     meshes referencing textures it doesn't include.
   - Unknown/unparseable shader ⇒ conservative: slots 0/1 Issue, rest Note.
3. New `ModIssue.Severity` (`Issue` / `Note`). Default view shows Issues;
   Notes behind a toggle ("Show notes (N)"). Naming intentionally mirrors the
   established project rule that WARNING-level output is reserved for
   in-game-visible defects; inert findings are neutral notes.
   - Existing non-texture types map: MissingFaceGenMesh/Tint, DarkFaceMismatch,
     MissingArmaMesh, MissingAltTexture, ModNotInstalled ⇒ Issue;
     MissingWeightSibling ⇒ Issue (engine morphs weights; a missing sibling
     pops/crashes).

**Files:** `BackEnd/NifHandler.cs`, `BackEnd/CharacterViewerHost/ModIssueScanner.cs`,
`Models/ModIssue.cs`, `View Models/VM_ModIssues.cs` / `VM_ModIssueEntry.cs`,
`Views/ModIssuesView.xaml`, `BackEnd/AssetHandler.cs` (signature ripple).

**Tests:** extend `NifHandlerShapeVisibilityTests` fixture usage for slot
extraction; new `ModIssueScannerRuleTests` cases: wrong-plugin facetint skipped,
bare junk in slot 6 skipped, junk in slot 0 still an Issue, env slots gated on
the shader flag, severity mapping.

**Expected effect (measured baselines):** local texture rows drop from 36,257 to
roughly 14k, of which the large majority become Notes; the 1,069 facetint rows
disappear; tester's `taranis`/mouth families demote to Notes; their misspelling
finds remain Issues.

### B. Consistency checks resolve within the mod's world, not the load order's (fixes #1)

**Change:** for the consistency path only (`ResolveNpcForConsistency` and the
delegates it hands out — shared by `ModIssueScanner` and the mugshot tile
dark-face badge in `InternalMugshotGenerator`), replace the fallback ladder:

> disambiguation → mod's plugins (from its folders) → **origin definition of the
> FormKey** (the defining master's record, via `ResolveAllContexts(...).Last()`
> or equivalent) → unresolved.

The removed tier is exactly "an intermediate override from an unrelated plugin
that happens to win the load order". Mod's-own-plugin overrides still win within
scope (USSEP's fixes apply when scanning USSEP; RS Children's race edits apply
when scanning RS Children — its plugin carries them).

**Consequences (intended):**
- Tester's USSEP case: race defaults come from vanilla ChildRace → match USSEP's
  child FaceGen (`ChildEyes`/`ChildMouth`) → clean. RS Children's own scan is
  unaffected.
- Scan verdicts become **load-order-independent** — stable whether the app runs
  inside or outside MO2 (today they differ; the local cache proves it). This is
  the right semantic for a *mod pre-flight* tool: "is this mod internally
  consistent", not "who wins your load order today" (that question belongs to
  Validate Output, which stays load-order-scoped).
- The mugshot tile dark-face badge inherits the fix (same false positives shown
  there today, e.g. USSEP tiles on an RS Children load order).

**Implementation note:** add an explicit mode to `NpcResolutionContext` (e.g.
`FallbackMode = Winner | Origin`) rather than a second resolver; the renderer's
mesh path keeps `Winner`, only `ResolveNpcForConsistency` sets `Origin`.
Mind the cache-key rule (memory `reference_cache_key_scope_audit`): if any
resolve results are cached, the mode must be part of the key.

**Tests:** synthetic-mod tests in the `NpcMeshResolverScopeTests` pattern:
master defines race+parts; foreign plugin overrides the race in the load order;
mod under scan lacks the race → assert origin race is used (winner ignored);
assert mod's own race override IS used when present; assert absent-plugin links
stay unresolved.

### C. Missing-dependency rollup + cascade suppression (fixes #6)

**Change**
1. Analyzer: when the NPC's own head-part list has unresolved links, **do not
   add race-default parts** (their occupancy can't be determined) — the finding
   is the broken/absent dependency, not fabricated race mismatches. Keep
   reporting genuinely-resolvable missing shapes.
2. Scanner: an analysis whose defects are *only* unresolved links (no baked-shape
   disagreement among resolvable parts) becomes a new issue type
   `MissingHeadPartPlugin` (Issue severity), `AffectedPath` = the absent plugin
   filename, Detail lists the head-part FormKeys.
3. Display/CSV: these roll up per mod — "records reference head parts from
   `Eyes Nouveaux.esp`, which is not in this mod's folders or your load order —
   N NPCs affected" — with per-NPC rows only under the rollup/tile filter.

**Effect:** Familiar Faces goes from 2,929 wall-of-text dark-face rows to a
handful of one-line dependency rollups naming the exact missing mods (KS
Hairdo's, Eyes Nouveaux, …), which is also precisely the actionable advice.

**Files:** `BackEnd/FaceGenConsistencyAnalyzer.cs`, `ModIssueScanner.cs`,
`Models/ModIssue.cs`, VMs/View. **Tests:** `FaceGenConsistencyAnalyzerResultTests`
(cascade suppression), scanner rule tests (classification).

### D. Grouped results view (fixes #4)

**Change:** default the results table to **grouped rows**: one row per
(Severity, Type, Category, AffectedPath) with an "NPCs" count column and
first-few-names tooltip; expanding (or clicking a mugshot tile, as today) shows
the per-NPC rows. A header toggle switches Grouped ⇄ Flat. Sorting stays
column-based. The scan cache stays per-NPC (tile badges need it) — grouping is a
pure VM transform in `RebuildIssueTable`.

CSV export stays flat (already pivot-friendly, tester relies on it) but gains
the new columns at the END: `Severity`, `ProvidedBy` (workstream F), `Ignored`
(workstream E) — appended so existing spreadsheets keep working.

### E. Ignore / whitelist (tester request)

**Change:** row context menu with two scopes:
- **"Ignore this file for this mod"** → key `(ModName, Type, normalized AffectedPath)` — hides every NPC row of that file (the shared-texture case).
- **"Ignore this exact issue"** → adds `NpcFormKey` (+ `NifPath`/`Shape` for NIF-texture rows).

Persisted in `Settings` (new `ModIssuesIgnored` list) — **not** in the scan
cache, so ignores survive rescans and cache-version bumps. UI: "Show ignored
(N)" toggle renders them greyed with an Unignore menu item; ignored issues are
excluded from left-panel counts, tile badge text, the summary line, and CSV
(exported only when "Show ignored" is on, flagged in the `Ignored` column).
A "global ignore across all mods" scope is deliberately deferred until someone
asks.

### F. Outfit attribution + messaging (fixes #7)

**Change**
1. New `ModIssue.SourceModName`: for outfit rows, the ModSetting (or raw folder
   name) whose folder/BSA supplied the *referencing NIF* (`job.DiskPath` is
   already known at issue time — map it against `CorrespondingFolderPaths` /
   BSA registrations; fall back to the `Referencer` plugin's owning ModSetting,
   else the plugin filename). Displayed as a "Provided by" column; also in the
   tile tooltip ("Outfit mesh from: X").
2. Wording: the Outfit category tooltip + a one-line hint above the table when
   outfit rows are present: *"Outfit issues usually come from the outfit/armor
   mod named in 'Provided by', not from the appearance replacer."*
3. README: extend the Mod Issues section with the same explanation, plus a
   short "for mod authors: run your replacer through this before publishing"
   paragraph (tester's suggestion).

### G. Smaller items (mostly found during reproduction)

1. **Unscanned-mods visibility:** eligible mods absent from the cache (the 8
   local Literal Who entries!) are currently invisible — absence reads as
   "clean". Add to the summary line ("… · M eligible mods not yet scanned") and
   list them greyed at the bottom of the left panel with a per-mod Scan action.
2. **Per-NPC scan failures surfaced:** `ScanNpc`'s blanket `catch` silently
   drops an NPC ([ModIssueScanner.cs:448-451](../BackEnd/CharacterViewerHost/ModIssueScanner.cs#L448-L451)).
   Count failures per mod into `ModIssueScanResult` and show "N NPCs could not
   be scanned" on the entry.
3. **Per-mod rescan** context-menu entry on the left list (author workflow:
   iterate on one mod without a full pass).
4. **Cache version bump → 3** (A/B/C all change scan rules; memory rule:
   `ModIssuesCacheFile.CurrentVersion` must be bumped). New `ModIssue` fields
   are additive-safe for serialization but stale verdicts must not survive.
5. **Release notes:** behavior change note — dark-face verdicts no longer
   depend on the live load order; texture notes tier introduced.

---

## 3. Sequencing

1. **A** (slot model + severity) — biggest row-count win, self-contained.
2. **B** (origin fallback) — small diff, large semantic fix; unblocks C.
3. **C** (dependency rollup) — builds on B's stance + analyzer touch.
4. **D** (grouping) → **E** (ignore) → **F** (attribution) — UI layer, in that
   order so the ignore list and attribution land on the grouped rows.
5. **G** throughout / last; single cache bump to v3 ships with the first of A–C.

Estimated touch set: ~10 files + tests. Per repo policy this plan precedes any
edits; implementation should also re-check the two uncommitted working-tree
changes (`VM_ModIssues.cs` cancel command, `ModIssuesView.xaml` spinner/wrap
polish) get committed first or folded in cleanly.

## 4. Risks / guardrails

- **Over-suppression** is the main risk in A. Guardrails: only slot 6 +
  flag-gated env slots are ever fully skipped; everything else demotes to Note,
  never disappears. The tester's confirmed true positives (misspellings, absent
  diffuse/normal sets, missing outfit meshes) all live in the still-Issue tiers.
- **B changes mugshot badge behavior** (shared code path) — intended, but must
  be called out in release notes and eyeballed on a few known tiles (USSEP,
  Nordic Faces, a COtR mod).
- **Winner-fallback removal must not leak into mesh/outfit resolution** — the
  renderer keeps Winner mode; only the consistency delegates switch. The
  existing outfit load-order fallback (`AllowLoadOrderFallback`) is untouched.
- **Slot-6 model: RESOLVED 2026-08-15** — in-game A/B verdict is
  canonical-only (slot 6 never consulted; see
  `docs/FaceTintEngineTest-2026-08.md`, including the reconciliation of
  commit 36ab0f4, whose premise was wrong but whose change is engine-inert).
  Workstream A's full-skip rule is proven correct; no residual risk here.

## 5. Verification plan

- Unit suites named per workstream above (scanner rules, resolver scope,
  analyzer cascade, NifHandler slots).
- **Local before/after:** rescan on this machine; expected deltas — facetint
  rows 1,069 → 0; mouth-trio (20,915 rows) demoted to Notes; Familiar Faces
  2,929 dark-face rows → per-dependency rollups; overall default-view row count
  drops by roughly an order of magnitude.
- **In-app (MO2) gate:** same rescan inside the MO2 session; B's promise is that
  the numbers now match the outside-MO2 run.
- **Tester follow-up:** ask them to re-run and confirm (a) USSEP rows are gone
  while RS Children stays clean, (b) NITHI/Literal Who rows are gone, (c) their
  real finds (misspellings/missing files) all still appear, (d) grouped view +
  ignore feel right on their 3,460-row loadout.

## 6. Decision points (recommendations inline)

1. **Severity naming/visibility** — recommend `Issue` (default view) / `Note`
   (toggle), consistent with the project's warning-severity bar. Alternative:
   Error/Warning/Info three-tier; not recommended (no third bucket has a clear
   in-game meaning here).
2. **Slot-6 handling** — RESOLVED: full skip, proven by the in-game A/B
   (`docs/FaceTintEngineTest-2026-08.md`; canonical-only, slot 6 never
   consulted). No decision left to make.
3. **B applying to mugshot badges too** — recommend yes (same false positives
   there); the alternative (fork the path) re-creates the drift the shared
   analyzer was built to avoid.
4. **Ignore scopes** — recommend shipping the two scopes above, deferring
   global-across-mods.
5. **CSV** — recommend appending `Severity`/`ProvidedBy`/`Ignored` columns to
   the existing schema rather than a second export format.
