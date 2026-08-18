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
| ~~A single **forward** miss does NOT necessarily dark-face~~ **REVISED 2026-08-15**: the Anoriath specimen was mischaracterized — WHR's record carries TWO Eyebrows parts and the shipped NIF bakes everything EXCEPT the second one (see Results); plain forward misses DO dark-face (B1–B3, B5) | Anoriath / Whiterun Hold Refine; BSA NIF dumped this session |
| An **extra** baked shape (reverse direction) does not dark-face on its own — wild specimen only, clean-engine cell A6 pending | 2026-07-24 specimen (`0_HAIRLINE_Male_Human_CurlyScalp` + duplicate shape), documented on `FaceGenConsistencyAnalyzer.Result.HasMismatch` |
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

## Kit BUILT (2026-08-15) — ready to run

### Verified baseline (espdump + NifDump, this session)

Ysolda's record head parts (types confirmed via espdump's new HeadPart case):

| FormKey | EditorID | Type | Notes |
|---|---|---|---|
| `0EAA72` | HairFemaleNord15 | Hair | ExtraParts → `0EAA6A` HairLineFemaleNord15 (Misc/IsExtraPart) |
| `07291B` | FemaleEyesHumanAmber | Eyes | |
| `0EC1B2` | MarksFemaleHumanoid00NoGash | Scars | **NO model, no baked shape** — vanilla itself ships a modelless record part with no NIF counterpart, and renders fine. Built-in control. |
| `0E4DA8` | FemaleBrowsHuman11 | Eyebrows | |

No explicit Face or Mouth part — those fill from NordRace female HeadData
defaults: `051176` HairFemaleNord04, `051623` FemaleHeadNord (Face), `05150F`
FemaleMouthHumanoidDefault (Misc), `051548` FemaleEyesHumanHazelBrown,
`0E4D7C` FemaleBrowsHuman01. All swap targets below were chosen to NOT be race
defaults (avoids the race-default confound).

Vanilla NIF baked shapes (6, all `BSDynamicTriShape`, named exactly by HDPT
EditorID): `FemaleMouthHumanoidDefault`, `FemaleHeadNord`,
`FemaleBrowsHuman11`, `FemaleEyesHumanAmber`, `HairLineFemaleNord15`,
`HairFemaleNord15`.

### Kit inventory

**A variants** (`S:\Skyrim NPC Selection\mods\`, loose facegeom + vanilla
facetint, no plugin; built with NifLab's new `strip` verb, shape lists
verified via NifDump):

| Mod folder | Shape(s) removed |
|---|---|
| `!DarkFace A1 NoBrows` | FemaleBrowsHuman11 |
| `!DarkFace A2 NoEyes` | FemaleEyesHumanAmber |
| `!DarkFace A3 NoHair` | HairFemaleNord15 |
| `!DarkFace A3b NoHairNoHairline` | HairFemaleNord15 + HairLineFemaleNord15 |
| `!DarkFace A4 NoMouth` | FemaleMouthHumanoidDefault |
| `!DarkFace A5 NoHead` | FemaleHeadNord |

**B variants** (one esp each, single Ysolda override, Skyrim.esm-only master;
generated by `tools/darkfacetest`, read back with espdump):

| Mod folder | Plugin | Record mutation |
|---|---|---|
| `!DarkFace B1 BrowsSwap` | DarkFaceTest_B1.esp | brows `0E4DA8` Brows11 → `0E4D88` Brows02 |
| `!DarkFace B2 EyesSwap` | DarkFaceTest_B2.esp | eyes `07291B` Amber → `072917` Brown |
| `!DarkFace B3 HairSwap` | DarkFaceTest_B3.esp | hair `0EAA72` Nord15 → `0511A7` Nord01 |
| `!DarkFace B4 BrowsRemoved` | DarkFaceTest_B4.esp | brows removed (record 4 → 3 parts) |
| `!DarkFace B5 ScarAdded` | DarkFaceTest_B5.esp | + `0E4E3F` MarksFemaleHumanoid06LeftGash (4 → 5) |

Tools created/extended this session: NifLab `strip` verb
(`S:\Dev\SynthEBD\CharacterViewer.NifLab`), `tools\darkfacetest` (Mutagen
0.54.0 esp generator, rerunnable; also emits B6), espdump HeadPart detail case
(Type / Flags / Model / ExtraParts). Round-2 additions: `!DarkFace A6
ExtraBrowsShape` (NifLab self-transplant clone) and `!DarkFace B6 BrowsAdded`
— see the Round 2 section.

### Profile state (restored this session)

The TintTest profile had drifted: **Whiterun Hold Refine was enabled** (left
over from the Anoriath check) — and WHR overrides Ysolda (different brows
`0E4DA4` Brows09, custom hair `0008B8:WhiterunHoldRefine.esp`, own FaceGen in
its BSA). Now disabled; plugins.txt was purged of stale entries and pre-seeded
with `*DarkFaceTest_B1..B5.esp` (auto-active when the mod folder is enabled);
WhiterunHoldRefine.bsa removed from archives.txt. All 11 `!DarkFace` mods are
pre-registered disabled in modlist.txt. No SKSE plugin DLLs anywhere: game
`Data\` has none (no `SKSE\Plugins`), MO2 overwrite has only an ini, and with
all mods disabled the VFS carries none — Face Discoloration Fix cannot load.

**Caveat worth one question:** if WHR was still enabled during the V1–V4 tint
runs (plugins.txt said active), those runs rendered WHR's record (hair+brows
differ) against the vanilla loose NIF and the user saw a normal face — an
accidental wild specimen of hair+brows forward mismatch not dark-facing. B1/B3
retest this under control.

### Interpretation notes (write down BEFORE looking at results)

- **B3 (hair swap) is a compound mismatch**: HairFemaleNord01 drags extra part
  HairLineFemaleNord01, so the record expects TWO shapes the NIF lacks while
  the NIF carries two baked shapes (Hair15 + Hairline15) the record no longer
  references. A3 vs A3b separates the hairline's contribution on the NIF side.
- **B4 (brows removed) may not stay "removed"**: per the Dawnguard incident,
  the engine can fill a missing slot from race HeadData — the female default
  brows is Brows01, which the NIF also lacks. So B4's effective state may be
  "race-default part with no baked shape + extra baked shape", not merely
  "extra baked shape". Interpret with that in mind.
- **B5 adds a second Scars-type part** (Ysolda already has NoGash). LeftGash
  has a real model (FemaleMaskLeftSide.nif) with no baked shape. If B5
  behaves oddly, the duplicate-type axis is a possible confound.
- The modelless NoGash part on the baseline record means "record part with no
  baked shape" is ALREADY tolerated when the part has no model — any analyzer
  rule must first ask whether the part has a model at all.

### Launch checklist (11 launches, ~1–2 min each)

Once, before the first launch: **restart MO2** (profile files were edited on
disk), switch to profile **TintTest**, and confirm the mod list shows the 11
`!DarkFace` entries plus the 4 `!TintTest` entries, all unchecked, and
*everything else* unchecked (search "Whiterun Hold Refine" and confirm it is
unchecked).

Per variant:
1. Enable exactly ONE `!DarkFace` mod (left pane). For B variants, confirm
   the right pane shows its `DarkFaceTest_B#.esp` ticked (it should auto-tick
   from plugins.txt; tick it if not).
2. Launch the game (SKSE launcher is fine — no plugin DLLs exist in the VFS).
3. Main menu console: `coc qasmoke` → `player.placeatme 13BAB` → `tfc`,
   inspect the face up close (front + side; check hairline seams for A3/A3b).
4. Classify: NORMAL / PART ABSENT (which?) / PART-SWAPPED (baked part shows) /
   **DARK FACE** (grey/untinted skin) / FULL REGEN (morphless generic face) /
   CTD. Screenshot (machine-local only — never commit renders).
5. Quit fully, disable the mod, next variant.

### RESULTS — round 1 (run by user, 2026-08-15)

Control (no kit mods enabled) = normal vanilla Ysolda.

| # | Variant | Mismatch (direction / part type) | Observed |
|---|---|---|---|
| A1 | NIF lacks brows shape | record Eyebrows part, no baked shape | **DARK FACE** |
| A2 | NIF lacks eyes shape | record Eyes part, no baked shape | **DARK FACE** |
| A3 | NIF lacks hair shape | record Hair part, no baked shape (hairline still baked) | **DARK FACE** |
| A3b | NIF lacks hair+hairline | record Hair part + extra part, neither baked | **DARK FACE** |
| A4 | NIF lacks mouth shape | race-default Misc(Mouth) part, no baked shape | **DARK FACE** |
| A5 | NIF lacks head shape | race-default Face part, no baked shape | **DARK FACE** |
| B1 | record brows ≠ baked brows | Eyebrows: forward miss + extra baked | **DARK FACE** |
| B2 | record eyes ≠ baked eyes | Eyes: forward miss + extra baked | **DARK FACE** |
| B3 | record hair ≠ baked hair | Hair(+hairline): forward miss ×2 + extra baked ×2 | **DARK FACE** |
| B4 | record brows removed | extra baked shape (± race-default substitution) | **DARK FACE** |
| B5 | record + LeftGash scar | Scars: forward miss, modeled part, nothing removed | **DARK FACE** |

### Round-1 verdict

**The raw engine is maximally strict in the forward direction.** Every
variant — every part type (Eyebrows, Eyes, Hair, Mouth, Face, modeled Scars),
explicit and race-default parts alike, single-part changes included — produced
the dark face with no SKSE plugins loaded. The over-warning hypothesis this
investigation set out to confirm is **refuted** for forward misses: the
scanner's `DarkFaceMismatch` rows describe a real in-game defect, and wild
"renders fine anyway" reports are best explained by Face Discoloration
Fix-class plugins masking the symptom.

Timeline note: Whiterun Hold Refine was enabled on this profile only AFTER the
V1–V4 tint runs (user-confirmed), so the tint results were not confounded.

### Anoriath reconciliation (the one standing "normal render" specimen)

Re-derived this session from WHR's own files (espdump + BSA extraction +
NifDump): WHR's Anoriath record has **7 head parts including TWO Eyebrows
parts** — `051508` BrowsMaleHumanoid01 *and* `0C710A` BrowsMaleHumanoid04 —
plus a custom antlers part (`000808:WhiterunHoldRefine.esp`, Type=Scars, real
model). Its shipped facegeom (`00013b97.nif`, 10 shapes) bakes **every part
and extra-part except BrowsMaleHumanoid04**: head, mouth, antlers, beard +
beard-extra, gash, eyes, Brows01, hairline, hair all match.

So Anoriath is NOT a plain single-slot forward miss (that cell is B1, which
dark-faces). It is a **duplicate-slot forward miss: a second part of a type
whose other part IS baked**. Hypothesis: the engine's reconciliation is
per-slot-satisfiable — a slot with at least one matching baked shape
tolerates an additional unmatched part of the same type. B5 does not refute
this (Ysolda's other Scars part, NoGash, is modelless — the Scars slot had NO
baked shape to satisfy it). Controlled replica = **B6** below.

### Round 2 — two cells left open (kit built, ready to run)

| Mod folder | What it isolates | Prediction if per-slot hypothesis holds |
|---|---|---|
| `!DarkFace A6 ExtraBrowsShape` | Pure reverse: vanilla NIF + cloned extra shape `FemaleBrowsHuman02` (real HDPT EditorID not on the record), record untouched | NORMAL (extra baked shapes inert; B4's dark face then attributes to race-default substitution, i.e. a forward miss) |
| `!DarkFace B6 BrowsAdded` | Anoriath replica: record = vanilla 4 parts + `0E4D88` Brows02 appended (Brows11 still present and baked), NIF untouched | NORMAL (duplicate-slot miss tolerated when the slot is satisfied) — if DARK FACE, the Anoriath render needs a re-look (subtle-symptom false negative?) |

Same launch protocol. `DarkFaceTest_B6.esp` is pre-starred in plugins.txt.

| # | Variant | Observed |
|---|---|---|
| A6 | extra baked brows shape, record untouched | **NORMAL** (run by user, 2026-08-16) |
| B6 | duplicate-slot record part, slot satisfied | **NORMAL** (run by user, 2026-08-16) |

Both predictions of the per-slot hypothesis confirmed.

## FINAL TRIGGER MATRIX (matrix complete, 13 controlled launches)

| Configuration | Engine behavior | Evidence |
|---|---|---|
| Record part (any modeled type, explicit or race-default) with no baked shape, slot NOT otherwise satisfied | **DARK FACE** — always; one part suffices | A1–A5, B1–B5 |
| Duplicate-slot miss: unbaked part whose slot Type another resolved part satisfies with a baked shape | **Tolerated** (renders the baked part) | B6; wild: Anoriath/WHR |
| Extra baked shape, record untouched (reverse direction) | **Tolerated** (inert) | A6; wild: 2026-07-24 hairline specimen |
| Record part with NO model (modelless placeholder) | Never counts — no shape expected | vanilla Ysolda's own NoGash scar; `BearsBakedGeometry` |
| Record part removed (B4) | **DARK FACE**, via race-default substitution: the race's default part fills the vacated slot and is itself unbaked — a forward miss, NOT a reverse-direction effect | B4 + A6 jointly |

Untested residue (kept warning where relevant): Misc-type duplicate-slot
misses (mouth/hairline/beard-extra share the Misc Type, so the exemption
deliberately excludes Misc); dark-face SEVERITY as a function of tint-layer
count (Anoriath has 2 layers vs Ysolda's 5 — moot now that his render is
explained by the duplicate-slot rule, but low-tint NPCs would show a fainter
symptom if one ever needs visual confirmation).

### Post-matrix field report: Sybille Stentor / Botox (NOT an engine exception)

First live scan after the recalibration flagged Sybille Stentor (`0132AA`,
Botox for Skyrim SE) as a dark-face mismatch — race-default
FemaleEyesHumanHazelBrown missing, NIF baked FemaleEyesHumanVampire01 — yet
she rendered NORMAL on a blank profile. Verified NOT a matrix violation but a
**scanner resolution bug**: she is BretonRaceVampire with no explicit eyes,
and **Dawnguard's override of that race** replaces the female default eyes
with FemaleEyesHumanVampire01 — exactly what Botox baked. The engine
(Dawnguard always present) sees a full match; the scanner's Origin-scoped
resolution graded against Skyrim.esm's stale race defaults. Fixed by making
`RecordLookupFallBack.Origin` walk the vanilla-master FAMILY winner-first
(Dragonborn → … → Skyrim; `RecordHandler.OriginFamilyWinnerFirst`, pinned by
`RecordHandlerOriginFamilyTests`) — third-party plugins stay excluded, so the
RS Children-bleed rationale for Origin mode is preserved. Any future "renders
fine despite a flagged forward miss" report should FIRST be checked against
this class: is the graded record the one the engine actually resolves?

### Post-matrix field report 3: 20-mod spawn matrix (2026-08-16/17)

User spawned scanner-flagged NPCs across ~20 mods (first in the full load
order with the mod prioritized, then re-tested anomalies on the minimal
TintTest profile). Reconciliation of every anomaly:

- **All full-load-order "renders fine" rows on vanilla-override NPCs were
  load-order confounds** — Cathedral's four (incl. two race-default-with-
  substitute rows and the male-record `DLC2EncBandit04MagicDarkElf02F`) ALL
  dark-face on the minimal profile. This also **refutes the "race-default
  miss is tolerated when a same-type shape is baked" hypothesis** — AstridEnd
  and the Afflicted are exactly that shape and they trigger. No new
  race-default rule; matrix stands.
- **Hairline/extra-part misses: resolved by variant A7 + specimen forensics
  (see the refined model below).** A7 (vanilla NIF minus
  `HairLineFemaleNord15` only) = **DARK FACE** — so extras are NOT simply
  exempt. But Gaiden Shinji renders fine with his hairline *renamed*
  (vanilla data quirk: `HairMaleRedguard4`'s ExtraParts POINTS AT
  `HairLineMaleRedguard3`, while the shipped — and, engine-accepted — bake
  carries `HairLineMaleRedguard4`), and Brand-Shei renders fine with his
  hairline record unmatched while the NIF bakes two differently-named
  hairline shapes. **Extras reconcile by PRESENCE, not name**: a renamed
  hairline satisfies; a fully absent one (A7) triggers.
- **SOGS `dunWhiteRiverWatchLvlBanditBoss` = engine-inert file, scanner gap**:
  the NPC keeps its vanilla **Traits** template (SOGS.esp doesn't override
  it), so the engine renders the terminus's face and never loads the
  mod-shipped `000E1F81.nif` the scanner graded (the mugshot resolver
  deliberately prefers the mod's shipped file — right for the tile, wrong
  for an engine-behavior verdict). Fix pending: Traits-templated subject +
  mod ships no record → the file cannot manifest unpatched; demote/reword
  (NPC2's template handling decides its actual fate; Validate Output checks
  the real output).
- **Khajiit ear tufts: explained by singular-slot FIRST-LISTED winner.**
  `WEAdventurerWarriorDualKhajiitM` carries TWO Hair-type parts —
  modelless `HairKhajiit00` listed first, modeled `KhajiitMaleEarTufts`
  after — and **vanilla's own facegen also omits the tufts shape**
  (extracted + dumped): the CK and the engine both keep only the
  first-listed part of a singular slot type; the surplus is dropped from
  the expected set (the first-listed being modelless, nothing is expected).
  This same rule RE-DERIVES the Anoriath and B6 tolerances (in both, the
  first-listed Eyebrows part was the baked one) and stays consistent with
  B5 (Scars is a MULTI slot — two gashes are legal — so the added LeftGash
  was expected and its miss triggered).

### REFINED ENGINE MODEL (all 16 controlled cells + ~20 field mods reconcile)

1. **Top-level parts reconcile by NAME** (B1–B3, A1–A5, every confirmed
   dark). Race defaults fill unoccupied singular slots and reconcile by
   name too (A4/A5, AstridEnd, Afflicted) — except OverlayHeadPartList
   races, whose defaults never participate (Fledgling).
2. **Singular slot types keep only the FIRST-LISTED part**; later same-type
   parts are dropped from the expected set (Anoriath, B6, Khajiit tufts).
   Scars is multi (B5); which other types are multi is untested.
   **AMENDED 2026-08-18 (field report 5): the rule applies to the FLATTENED
   set — EXTRAS contest the singular slots too.** A surplus-singular-typed
   extra (MoW's FacialHair `_1bit` twin behind the baked beard; Hjoromir's
   Eyebrows lashes behind his top-level brows) is dropped: the CK bakes
   neither and the engine expects neither (both in-game verified inert).
   Encoded check-only — extras consult slot occupancy but never claim it.
3. **Extra parts reconcile by PRESENCE, not name** (Gaiden renamed-hairline
   fine, Brand-Shei foreign-named hairlines fine, A7 absent-hairline DARK —
   the hairline is typed **Misc**, the multi grab-bag, so rule 2's dropping
   never touches it).
   **AMENDED 2026-08-17 (field report 4): an unbaked extra whose MODEL FILE
   equals its baked ancestor's is inert** — the vanilla "_1bit" beard-twin
   convention (MQ304Ulfric specimen; now doubly covered by the rule-2
   extension, kept as a belt). A hairline is distinct geometry, so A7
   stands.
4. Modelless parts never count (vanilla NoGash; `BearsBakedGeometry`).

**ENCODED 2026-08-17 (cache v7, suite 2477 green):** `Analyze` implements
rules 2–3 directly — `IsSingularSlotType` (Eyes/Hair/Face/Eyebrows/FacialHair
singular; Scars multi per B5; Misc multi as the grab-bag) drops surplus
top-level parts into diagnostic `Result.SurplusSlotParts` (names still
suppress orphan listings), and unbaked extras satisfied by a baked sibling
extra or any orphan stand-in land in `Result.PresenceSatisfiedExtras`
(superseding the narrower `IsDuplicateSlotTolerated`, now removed). Missing
extras that DO flag are annotated "(extra part)". The SOGS class is handled
in the scanner: when the mod-scope record keeps the Traits flag
(`NpcMeshResolver.KeepsTraitsTemplateInModScope`) and the appearance hop
didn't already redirect, the dark-face row demotes to Note with an
explanation that the raw engine never loads the graded file. All analyzer
callers (scanner, badges, both validator paths) inherit rules 2–3.
- **Marcurio Refined / OP zExtended "floating mouth"** = same trigger, worse
  fallback: the records' entire custom HDPT sets are absent from the baked
  NIF, so the regen path rebuilds from the HDPT source models — when those
  can't build, the head has no geometry and only mouth/teeth render. **MoS
  Refined Patreon freeze-on-spawn**: same total-mismatch class with
  cross-plugin HDPTs; regen hitting corrupt/physics-dependent part models.
  Both rows are true positives with different symptoms.

### Post-matrix field report 4: shared-model extras — the "_1bit" beard twin (2026-08-17)

User spawn-tested three scanner-flagged NPCs through a patch run: WICO's
Marise Aravel + Hert (top-level name mismatches) both DARK — true positives —
while Men of Winter's `MQ304Ulfric` rendered FINE despite his flagged row.
His was the only delta of its kind: record-side extra `111BeardUlfric_1bit`
(`IsExtraPart`, FacialHair) absent from the bake, nothing else. Confound
exclusion was possible without a minimal-profile rerun: the patched NPC.esp
kept the extra expectation (merged copy chain intact), the output bake was
byte-identical to the mod's (no `_1bit` shape), every other Ulfric replacer
was disabled, and Face Discoloration Fix is not installed — so the exact
graded pair was live in game.

The discriminator vs A7: `111BeardUlfric_1bit` references the **exact same
model file** as its parent `111BeardUlfric` (`humanbeardmedium09.nif`, same
TRIs) — the vanilla `_1bit` hard-alpha beard-twin convention. The twin
contributes no geometry the parent's baked shape doesn't already carry, and
the engine evidently doesn't demand a separate baked shape for it. A
hairline is DISTINCT geometry, which is why A7 (hairline absent, nothing
standing in) triggers.

Cache-wide scale check: across ~1,000 scanned mods the solo-extra dark-face
class has exactly THREE rows — the A7 control itself, Miggyluv Hjoromir's
`MIG_Hjoromir_Lashes` (distinct lash mesh — predicted DARK, untested), and
Ulfric. Narrow class, surgical fix.

**ENCODED (cache v8): rule 3 gains clause (c)** in
`FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied` (the extras predicate,
now factored + unit-tested): an unbaked extra is also satisfied when its
`Model.File` equals its baked ancestor's (separator/case-insensitive;
`HeadPartRef.ModelPath` captured in the walk). Ancestor unbaked → still
flags (the top-level row dominates anyway). All analyzer callers (scanner,
badges, both validator paths) inherit.

### Post-matrix field report 5: surplus-singular EXTRAS — the unifying rule (2026-08-18)

Hjoromir (Miggyluv's 3DNPC replacer, `052FE7:3DNPC.esp`) — the last wild
member of the solo-extra class — spawn-tested FINE despite his flagged
`MIG_Hjoromir_Lashes` miss, with every confound excluded (base 3DNPC
enabled, all six competing replacers disabled, no FDF). User observation:
"looks like he might not have eyelashes" — the geometry is simply absent,
no regen. Unlike Ulfric's `_1bit`, the lashes are DISTINCT geometry
(`LashesMale.nif` vs the parent head's `malehead.nif`), refuting
shared-model as the general mechanism.

The actual discriminator: **both tolerated extras are surplus parts of a
singular slot type** — `_1bit` is a second FacialHair behind the beard,
the lashes are a second Eyebrows behind `MIG_Hjoromir_Brows` — while A7's
hairline is typed **Misc**, the multi grab-bag, so it never loses a slot
contest. Rule 2 (first-listed-wins) evidently applies to the FLATTENED
set, extras included. Corroboration: both mods' own CK bakes omit exactly
those shapes (the same CK-side dropping the Khajiit-tufts extraction
showed for surplus top-level parts) — the bakes were CK-canonical, so
these were never authoring defects at all. Bethesda typing hairlines Misc
rather than Hair reads as deliberate slot-collision avoidance.

Alternative hypothesis (H-tri): "extras without TRI morph parts don't
count" (lashes carry no TRIs; A7's hairline has `Hairline15.tri`) also
fits the wild data but needs the shared-model clause retained for Ulfric
(his `_1bit` HAS full TRIs). Discriminating cell, if ever wanted: a
surplus-singular extra WITH TRIs and a distinct model, absent from the
bake — slot rule says fine, TRI rule says dark.

**ENCODED (cache v10):** `IsSurplusSingularExtra` — inside the walk, an
extra whose own singular Type is already occupied by an earlier expected
part goes to `Result.SurplusSlotParts` (FromExtraParts=true) instead of
the expected set; names still suppress orphan readings. Check-only:
extras never CLAIM a slot, so top-level behavior is untouched. The v8
shared-model clause stays as a belt.

### External detector review: Dark Face Issue Reporter 2.8 (xEdit script, 2026-08-18)

User-supplied for gap analysis (not ground truth). Its check: winning
override, forward-only, EditorID-vs-`BSFaceGenNiNodeSkinned`-children
name match, modelless parts skipped, race defaults filled for
Face/Eyes/Eyebrows/Hair only (first-of-type). Notable deltas:
- It never walks ExtraParts at all (would MISS A7) and still checks
  surplus same-type top-level parts (would false-positive on
  Anoriath/B6/tufts). No tint-file check. Our model supersedes on all of
  these.
- **Exclusions we lacked**: `Is CharGen Face Preset` NPCs (chargen-menu
  data, never placed), the Player record, and NPCs with the
  `ActorTypeGhost` keyword (presumably the ghost shader masks the tint
  mismatch — UNVERIFIED, a test-scenario candidate).
- Missing-FaceGen verdicts are gated on the NPC being REFERENCED (placed
  ACHR, Traits-template user, or leveled-list entry) — unreferenced
  records get no row. Mutagen has no reverse-reference index, so the
  cheap preset/Player exclusions cover the main class for us.
- It reads only `BSFaceGenNiNodeSkinned` children as baked shapes (we
  survey all NiShapes — ours can see orphans outside the facegen node,
  detail-only, harmless), and it flags an unparseable/empty facegen NIF
  as broken — a case our scanner currently passes SILENTLY (gap worth
  closing: file present but unreadable ⇒ engine regen/dark face).

### Post-matrix field report 2: OverlayHeadPartList races (vampires)

Bruma's `CYREncVampire00Template` (Vampire Fledgling, `0792C4:BSHeartland.esm`,
NordRaceVampire) — flagged for the race-default Face part
`FemaleHeadNordVampire (006F97:Dawnguard.esm)` missing from the baked NIF —
**renders WITHOUT dark face**, user-verified both with NPC2 output active and
on plain Bruma. Not an engine-tolerance surprise and not a resolution-scope
bug: the discriminator is the race's **`OverlayHeadPartList` flag** (all
vanilla vampire races carry it; espdump-verified on NordRaceVampire in both
Skyrim.esm and Dawnguard). An overlay race's HeadData is a runtime overlay
(the vampirism transform), NOT slot-fill defaults that the baked head must
carry — so overlay-race defaults never enter the engine's reconciliation.
The matrix cells that proved race-default misses DO trigger (A4 mouth,
A5 head) ran on NordRace, which lacks the flag — no contradiction. Explicit
record parts on vampires still count (the EncVampire02BossBretonM demon-eye
incident dark-faced and keeps flagging). Encoded as
`FaceGenConsistencyAnalyzer.RaceDefaultsParticipateInReconciliation`
(skips the race-default walk for flagged races; all callers inherit). This
also retires the long-open benign "MaleEyesHumanVampire01 (race default) vs
MaleEyesHumanVampire (mesh)" residual rows from the Dawnguard incident.

## Applying the outcome

**Applied 2026-08-15 (round 1):** no per-type severity table is warranted —
the matrix is uniform, so `DarkFaceMismatch` stays Issue severity for ALL
forward misses. `FaceGenConsistencyAnalyzer` recalibrated:
`Result.HasMismatch` remarks rewritten around the proven trigger (+ the
refined Anoriath exception and the pending A6/B6 cells), the
`SingleHeadPartDifference` "does not always show as a dark face" hedge
replaced with the verified claim + FDF-masking explanation, headlines
upgraded from "a common cause of" to "causes";
`ModIssuesCacheFile.CurrentVersion` bumped 4 → 5.

**Applied 2026-08-16 (round 2, both cells NORMAL):**
- Duplicate-slot exemption implemented:
  `FaceGenConsistencyAnalyzer.IsDuplicateSlotTolerated` (public static, Misc
  excluded as a grab-bag Type), applied inside `Analyze` so every caller —
  Mod Issues scanner, mugshot badges, Validate Output — inherits it
  uniformly; exempted parts land in `Result.ToleratedDuplicateSlotMisses`
  (diagnostic only, never reported, never sets `HasMismatch`).
- `ExtraBakedShapesOnly` stays unreported — now clean-engine-confirmed rather
  than wild-specimen-inferred; B4 attributed to race-default substitution.
- Cache stays at v5 (both recalibration halves ship together); analyzer
  evidence docs updated from "pending" to "confirmed"; unit tests added for
  the exemption rule.

**INVESTIGATION COMPLETE.** Cleanup when convenient: the 13 `!DarkFace *` /
4 `!TintTest *` mod folders and the `TintTest` profile are disposable;
`tools\darkfacetest` and the NifLab `strip` verb are keepers.

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
