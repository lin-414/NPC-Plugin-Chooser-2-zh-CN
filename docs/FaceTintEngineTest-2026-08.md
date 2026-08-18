# FaceTint Slot-6 Engine Behavior Test (2026-08)

Goal: establish, in game, how the engine locates an NPC's FaceGen tint DDS —
from the path baked in the head shape's texture-set **slot 6**, from the
**canonical** path it constructs (`Textures\Actors\Character\FaceGenData\FaceTint\<plugin>\<formid>.dds`),
or baked-first-with-canonical-fallback. The answer decides how the Mod Issues
scanner treats missing slot-6 files (improvement plan, workstream A) and
retro-validates the shared-FaceGen tint rewrite.

## Why a test is needed (the evidence conflicts)

- **Baked path matters:** the 2026-08-04 share fix changed ONLY the NIF's
  slot-6 string (`RewriteCopiedFaceTintPath`) and verifiably fixed wrong tints
  in game — impossible if the engine ignored the slot.
- **Baked path seems ignorable:** external tester's NITHI Dexion Evicus has a
  wrong-plugin slot-6 path (file absent) yet renders fine; 1,069 analogous rows
  exist across this machine's own mods. *Caveat: the tester may run
  Face Discoloration Fix or similar, which regenerates tints when loading
  fails — their "looks fine" is not conclusive.* (FDF source confirms it NOPs
  the tint-skip branches and forces regeneration with record tint layers.)
- **Engine constructs the canonical path:** `SkyrimSE.exe` contains exactly one
  facetint string — the template
  `data\Textures\Actors\Character\FaceGenData\FaceTint\%s\%08X.dds` — so the
  canonical path is definitely built at runtime; what's unknown is whether the
  texture *bind* comes from it, the slot, or slot-then-fallback.

## Test kit (already built, ready to run)

Subject: **Ysolda** (`00013BAB:Skyrim.esm`, verified via espdump; unique,
untemplated). Her vanilla facegeom NIF + facetint DDS were extracted from
`Skyrim - Meshes0.bsa` / `Skyrim - Textures0.bsa`. The vanilla NIF's head shape
slot 6 = `data\Textures\...\FaceTint\Skyrim.esm\00013BAB.dds` (single
occurrence; all patches are same-length in-place edits). Test tints keep the
original 512×512 dimensions and alpha (fully opaque) with solid marker colors,
written as uncompressed BGRA DDS.

Four MO2 mod folders under `S:\Skyrim NPC Selection\mods\`:

| Mod | NIF slot 6 | canonical `Skyrim.esm\00013BAB.dds` | other files |
|---|---|---|---|
| `!TintTest V1 Control` | vanilla (`Skyrim.esm\…`) | vanilla tint | — |
| `!TintTest V2 Marker` | vanilla | **GREEN** tint | — |
| `!TintTest V3 BrokenBakedPath` | `Qkyrim.esm\…` (nonexistent) | **GREEN** tint | — |
| `!TintTest V4 Conflict` | `Zkyrim.esm\…` (exists) | **GREEN** tint | **BLUE** tint at `FaceTint\Zkyrim.esm\00013BAB.dds` |

A minimal MO2 profile **`TintTest`** was generated (all 428 regular mods
explicitly disabled; only the V1 test mod enabled; empty plugins/archives
lists; INIs copied from NPC Test). With every mod disabled, no SKSE plugin DLLs
(RaceMenu etc.) enter the VFS, so the engine is unmodified even when launching
through SKSE.

## Protocol (~10 minutes)

1. Restart MO2 (the new profile was created while it was running), switch to
   profile **TintTest**.
2. For each variant V1→V4: enable exactly that one `!TintTest` mod (keep the
   other three disabled), launch the game, and at the main menu open the
   console: `coc qasmoke`, then `player.placeatme 13BAB`, look at Ysolda's
   face closely (`tfc` for free camera). Note the face color. Quit fully
   between variants.
3. Record: V1 = normal? V2 = green? V3 = green / normal / grey-dark? V4 =
   blue / green?

V1 confirms the loose override works and nothing else touches Ysolda.
V2 calibrates the marker (if V2 isn't obviously green, stop — the marker
technique needs adjusting, results would be uninterpretable).

## Interpretation

| V3 result | V4 result | Model | Scanner consequence (plan workstream A) |
|---|---|---|---|
| GREEN | BLUE | **Baked-first, canonical fallback** (the current working model) | Skip slot-6 *missing-file* checks (canonical is checked separately) — plan stands as written |
| GREEN | GREEN | **Canonical only** — slot 6 fully ignored | Same scanner rule (skip slot 6), but the 2026-08-04 share-fix verification needs re-audit (something else must have fixed it) |
| normal/grey/dark | BLUE | **Baked only, no fallback** | Slot-6 misses are REAL in-game issues: keep flagging them (likely as their own issue type, noting FDF masks the symptom); tester's NITHI row was FDF-masked |
| normal/grey/dark | GREEN | Incoherent — retest | — |

Whatever the outcome: update `docs/ModIssuesImprovementPlan-2026-08.md`
(workstream A, slot-6 rule + the "Slot-6 model" risk note) and the
`shared-facegen-tint-rewrite` / `mod-issues-tab` memories.

## RESULTS (run by user, 2026-08-15)

| Variant | Observed |
|---|---|
| V1 Control | normal face |
| V2 Marker (canonical = GREEN) | **green** |
| V3 BrokenBakedPath (slot 6 dangling, canonical = GREEN) | **green** |
| V4 Conflict (slot 6 → existing BLUE, canonical = GREEN) | **green** |

**Verdict: canonical-only.** The engine loads the face tint exclusively from
the path it constructs from the NPC's plugin + FormID (the
`...FaceTint\%s\%08X.dds` template found in `SkyrimSE.exe`). The slot-6 string
baked in the FaceGen NIF is **never consulted** — V4 is dispositive: a valid,
existing, differing slot-6 tint was ignored in favor of the canonical file.
(Untested residue: canonical-missing + slot-6-valid can't be probed on a
vanilla NPC because the BSA canonical always resolves; given V4, a slot-6
rescue is very unlikely, and the scanner flags canonical-missing separately
either way.)

### Reconciliation with commit 36ab0f4 (the share tint "fix")

Re-audit of the history shows **every observation fits canonical-only**. The
full cavy8 story (PR #2 verbatim body + their Nexus comments of 2026-08-04,
provided by the user):

- **Their in-game observation was real**: on the share test (Lydia - DF Edit →
  Carlotta Valentia) "the facetint dds is seemingly from a different mod,
  while the facegeom nif is correct… happening with other NPCs as well,
  **including non-shared ones** … similar visual errors."
- **That last detail refutes their own later diagnosis by itself**: a
  non-shared NPC's baked slot-6 path IS its canonical path — there is nothing
  stale to blame — so whatever painted wrong tints on non-shared NPCs cannot
  have been the stale NIF path their PR fixed. Under canonical-only, a wrong
  tint means wrong CONTENT at the canonical path (mis-sourced delivery or a
  runtime file conflict), never the baked string.
- **Version alignment**: they tested on the public **2.2.2** (2026-07-12).
  The entire FaceGen delivery overhaul — the forwarding ladder (0cdc63f,
  07-27), the cross-NPC path-write fix ("Stop writing a templated NPC's face
  to its template's path", 46d24f5, 07-29), the consistency-seam fixes
  (07-30) — postdates 2.2.2 and was unreleased (partly unpushed) on 08-04.
  Their symptom class ("tint from a different mod/NPC at the canonical path,
  geometry fine, arbitrary NPCs") is exactly the class that sprint fixed.
- Investigating the output files, they found the one visible anomaly — the
  stale slot-6 path in the shared NIF — and pattern-matched it to the
  symptom ("Found the issue"). Understandable, but the anomaly was inert;
  the PR body itself claims only the (correct) static inconsistency. The
  "engine reads the tint path from inside the NIF" premise was then minted
  in 36ab0f4's commit message when the fix was adapted:

- **36ab0f4 changed nothing the engine reads.** Its diff rewrites the slot-6
  string inside the copied NIF (deliberately *after* the texture scan) and
  wires validator bookkeeping (`EditedFaceGen`). It did not change which DDS
  files are delivered or their content.
- **The file the engine actually reads was already delivered.** The
  destination-canonical tint copy (`donor tint content → facetint\<dest
  plugin>\<dest id>.dds`) exists in the surrogate/share branch as far back as
  f047216's context (Oct 2025) and, in the current architecture, as the
  FaceGen ladder's `DdsChoice` half (0cdc63f, 2026-07-27).
- **f047216's original bug reads cleanly under canonical-only:** the NIF scan
  wrote a stray tint *file* at the DONOR's canonical path, and the engine —
  reading canonical paths — showed it on the donor's face. The baked slot was
  never part of that mechanism.
- **The 2026-08-04 in-game verification** confirmed shares render correctly
  with the fix in place — which they would have with or without the rewrite,
  since the ladder had been delivering the destination-canonical DDS since
  July 27. The rewrite received the credit for the ladder's delivery.

**Consequences:** the Mod Issues scanner must never report slot-6 facetint
paths (missing *or* mismatched — both are engine-inert); the canonical tint
check (`MissingFaceGenTint`) is the real coverage. `RewriteCopiedFaceTintPath`
is engine-inert NIF hygiene: keeping it makes delivered NIFs self-consistent
(EasyNPC does the same), but its code comments and the commit lore claiming
engine behavior are wrong and should be corrected whenever that code is next
touched.

## Cleanup

Delete the four `!TintTest *` folders from `mods\` and the
`profiles\TintTest\` folder. Nothing else was modified (the NPC Test profile
was only read).
