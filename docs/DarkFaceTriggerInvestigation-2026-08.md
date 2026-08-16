# Dark-Face Trigger Investigation (handoff, 2026-08)

Goal: empirically determine **which record↔FaceGen-NIF disagreements actually
trigger the dark-face bug** in the vanilla SSE engine, and which render
harmlessly. The Mod Issues scanner and mugshot badges currently report every
forward mismatch as a potential dark-face; specimens below show that
over-warns. The deliverable is a trigger matrix that recalibrates
`FaceGenConsistencyAnalyzer` severity/wording (per part type and direction)
with evidence instead of community folklore.

## What is already established (do not re-derive)

| Fact | Evidence |
|---|---|
| Face tint loads ONLY from the canonical `FaceTint\<plugin>\<formid>.dds` path; the NIF's slot-6 string is never consulted | In-game A/B 2026-08-15, `docs/FaceTintEngineTest-2026-08.md` (Ysolda kit, V1–V4) |
| A single **forward** miss (record part with no baked shape) does NOT necessarily dark-face | Anoriath / Whiterun Hold Refine: record `BrowsMaleHumanoid04`, NIF baked with `BrowsMaleHumanoid01` only → user-verified normal render; the head displays the baked brows |
| An **extra** baked shape (reverse direction) does not dark-face on its own | 2026-07-24 specimen (`0_HAIRLINE_Male_Human_CurlyScalp` + duplicate shape), documented on `FaceGenConsistencyAnalyzer.Result.HasMismatch` |
| Missing facegeom NIF entirely → runtime head regeneration → dark face | Community-established; consistent with all observations |
| The dark-face mechanism is "the engine REGENERATES the head at runtime and applies no tints for NPCs"; Face Discoloration Fix works by force-loading record tint layers and un-gating tint application on that regen path | FDF source (Exit-9B/Face-Discoloration-Fix): NOPs TINC/TINI/TIAS/TINV skip branches in `TESNPC::ReadFromFileStream`, patches `TESNPC::FinishInit` + `BSFaceGenDB::GenerateHeadPartModel` |
| Real dark faces HAVE been observed tied to record/NIF disagreement | The Dawnguard-vampire disambiguation incident (see memory `dawnguard-vampire-darkface-disambiguation`) — involved eye head parts; exact direction/type should be re-derived from that memory + git history before designing the eye variants |

**Reframed question:** since dark face = regen-without-tint, the real question
is *which mismatches make the engine abandon the preprocessed head* (or fail
its tint reconciliation). Candidate discriminators: head-part **Type** (Face /
Hair / Eyes / Mouth vs Brows / Facial hair / Scars / Misc), **direction**
(record-part-missing-from-NIF vs extra-baked-shape), **count** of mismatches,
the **primary head shape** itself vs auxiliary parts, **race-default vs
explicit** parts, and hairline/extra-part chains.

## Method: mutation matrix on one vanilla NPC

Reuse the FaceTint test scaffold (same protocol as
`docs/FaceTintEngineTest-2026-08.md`, which this doc treats as the template).

**Subject:** Ysolda, `013BAB:Skyrim.esm` (unique, untemplated, Nord female,
verified via espdump). Baseline record data (espdump, Skyrim.esm): HeadParts =
`0EAA72`, `07291B`, `0EC1B2`, `0E4DA8` (identify which is hair/eyes/brows/
hairline via `espdump.exe <Skyrim.esm> <formkey>` at the start); Race
`013746` (NordRace); TintLayers present. FaceGen pair:
`meshes\...\facegeom\Skyrim.esm\00013BAB.nif` (Skyrim - Meshes0.bsa),
`textures\...\facetint\Skyrim.esm\00013BAB.dds` (Skyrim - Textures0.bsa).
Vanilla NIF baked shapes incl. `FemaleHeadNord`-family head, brows, eyes,
hair, mouth (`inspect` them with `NifHandler.GetShapeTextureDetails` or the
NifLab CLI at `S:\Dev\SynthEBD\CharacterViewer.NifLab`).

**Environment:** the minimal MO2 profile `TintTest` at
`S:\Skyrim NPC Selection\profiles\TintTest` (all mods disabled, no SKSE
plugin DLLs → raw engine; recreate per the FaceTint doc's recipe if it was
cleaned up). The `!TintTest V1 Control` mod folder holds the extracted vanilla
pair if not deleted; otherwise re-extract with any BSA tool. **No Face
Discoloration Fix / SKSE plugins may be active** — they alter exactly the
behavior under test.

**Variant axis A — NIF-side (record untouched).** Copy the vanilla facegeom
and delete ONE shape per variant with `NifHandler.RemoveShapesByName` (already
in-repo, save-on-change, used by the wig pipeline) or NifLab:
A1 brows shape · A2 eyes shape · A3 hair shape (and A3b hair+hairline) ·
A4 mouth shape · A5 the primary head shape itself. Ship each as its own
`!DarkFace A#` mod folder (facegeom + vanilla facetint at canonical paths).

**Variant axis B — record-side (NIF untouched).** A tiny test plugin
(`DarkFaceTest.esp`, one Ysolda override per variant — author with a scratch
Mutagen console modeled on `S:\Dev\NPC Plugin Chooser 2\tools\espdump`,
Mutagen 0.54.0, `SkyrimMod.WriteToBinary`; enable via the profile's
plugins.txt):
B1 swap brows to a different vanilla brows FormKey · B2 swap eyes · B3 swap
hair · B4 REMOVE one part from the record (reverse direction: NIF now has an
extra baked shape) · B5 add an extra part the NIF lacks (forward miss without
removing anything). Each B variant is a separate esp build (or one esp swapped
between launches).

**Per-variant observation (one launch each):** main menu console →
`coc qasmoke` → `player.placeatme 13BAB` → inspect (`tfc`). Classify:
NORMAL / PART-SWAPPED (baked part renders) / PART ABSENT / **DARK FACE**
(grey/untinted) / FULL REGEN (vanilla-morphless face) / CTD. Screenshot each.
Record results in a table in this doc.

**Expected volume:** ~10 launches ≈ 30–45 min in-game time, plus kit
authoring. Kit construction is fully scriptable by the assistant; only the
in-game looks need the user.

## Applying the outcome

- Map (Type × direction) → observed effect; encode as a severity table in the
  scanner (e.g. if brows/beard forward misses never trigger → demote
  `SingleHeadPartDifference` rows of those types to Note; if eye/hair misses
  DO trigger → keep Issue and sharpen the wording).
- Update `FaceGenConsistencyAnalyzer.Result.HasMismatch` docs and
  `BuildReason` hedge lines with the matrix; bump
  `ModIssuesCacheFile.CurrentVersion`; update the `mod-issues-tab` memory and
  this doc with results.
- If regen is observed, note whether tint layers rescue anything (Ysolda has
  record tints) — relevant to how bad "dark" actually looks vanilla.

## Pointers for the new session

- Code: `BackEnd/FaceGenConsistencyAnalyzer.cs` (Analyze + HasMismatch docs +
  BuildRemedies), `BackEnd/CharacterViewerHost/ModIssueScanner.cs` (dark-face
  block), `BackEnd/NifHandler.cs` (`RemoveShapesByName`,
  `GetShapeTextureDetails`).
- Docs: `docs/FaceTintEngineTest-2026-08.md` (protocol template + profile
  recipe + results format), `docs/ModIssuesImprovementPlan-2026-08.md`.
- Memories: `mod-issues-tab`, `shared-facegen-tint-rewrite` (canonical-only
  verdict), `dawnguard-vampire-darkface-disambiguation`, `espdump-tool`,
  `test-environment-layout` (git root is the INNER folder; MO2 must be
  restarted to see new profiles; never commit rendered NPC images).
- Everything through commit `4a05633` is already merged; the working tree
  should be clean at session start.
