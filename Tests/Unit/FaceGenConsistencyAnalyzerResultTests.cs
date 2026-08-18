using FluentAssertions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NPC_Plugin_Chooser_2.BackEnd;
using NPC_Plugin_Chooser_2.Tests.TestSupport;
using Xunit;

namespace NPC_Plugin_Chooser_2.Tests.Unit;

/// <summary>
/// Pure, deterministic surface of <see cref="FaceGenConsistencyAnalyzer"/>: the nested
/// <c>HeadPartRef</c> record-struct, the <c>Result</c> class (<c>HasMismatch</c> truth table
/// + <c>BuildReason</c> string formatting/truncation), and the private static
/// <c>IsGenericNode</c> classifier (exercised via <see cref="Reflect"/>).
///
/// These types are built directly in memory — no NIF parsing, no Skyrim install, no clock or
/// network. The <see cref="FaceGenConsistencyAnalyzer.Analyze"/> entry point and the private
/// <c>GetSurvey</c> path are deliberately out of scope here (they require a real FaceGen .nif
/// and a wired <c>NifMeshBuilder</c>); see the NOTE at the bottom of this file.
/// </summary>
public class FaceGenConsistencyAnalyzerResultTests
{
    private static readonly FormKey HpA = FormKey.Factory("000801:HeadParts.esp");
    private static readonly FormKey HpB = FormKey.Factory("000802:HeadParts.esp");
    private static readonly FormKey HpC = FormKey.Factory("000803:Other.esp");

    // ---- HeadPartRef (readonly record struct) -----------------------------------------------

    [Fact]
    public void HeadPartRef_ExposesConstructorArgsAsProperties()
    {
        var r = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadNord");
        r.FormKey.Should().Be(HpA);
        r.EditorId.Should().Be("MaleHeadNord");
    }

    [Fact]
    public void HeadPartRef_Deconstructs()
    {
        var r = new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "MouthHumanoidDefault");
        var (fk, edid) = r;
        fk.Should().Be(HpB);
        edid.Should().Be("MouthHumanoidDefault");
    }

    [Fact]
    public void HeadPartRef_ValueEquality_SameFieldsAreEqual()
    {
        var a = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        var b = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void HeadPartRef_ValueEquality_DiffersOnEitherField()
    {
        var baseline = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        var diffKey = new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "Eyes");
        var diffEdid = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows");

        baseline.Should().NotBe(diffKey);
        baseline.Should().NotBe(diffEdid);
        (baseline != diffKey).Should().BeTrue();
    }

    [Fact]
    public void HeadPartRef_EditorIdComparison_IsCaseSensitive()
    {
        // The record uses the default string comparer (ordinal, case-sensitive) for equality.
        var lower = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "eyes");
        var upper = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Eyes");
        lower.Should().NotBe(upper);
    }

    // ---- Result.HasMismatch truth table -----------------------------------------------------

    [Fact]
    public void HasMismatch_EmptyResult_IsFalse()
    {
        new FaceGenConsistencyAnalyzer.Result().HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_MissingBakedShape_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
        };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_UnresolvedHeadPart_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            UnresolvedHeadParts = new[] { HpC },
        };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_NullHeadPartLinks_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 };
        r.HasMismatch.Should().BeTrue();
    }

    [Fact]
    public void HasMismatch_OrphanBakedShapesOnly_IsFalse()
    {
        // Regression guard: the flag is forward-direction only. A .nif carrying a shape with no
        // matching head part was observed in game WITHOUT the dark-face bug (2026-07-24), and the
        // reference detector for this bug (the "Dark Face Issue Reporter" xEdit script) likewise
        // only checks that every HeadPart in the record is present in the .nif. Orphans stay
        // corroborating detail — raising the flag on them would report NPCs that render fine.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "SomeCustomShape", "AnotherOne" },
        };
        r.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_SurplusAndSatisfiedExtrasOnly_IsFalse()
    {
        // Engine-inert findings must not flag: surplus singular-slot parts (the engine
        // keeps only the first-listed — B6/Anoriath/Khajiit tufts) and unbaked extras
        // satisfied by presence (Gaiden's renamed hairline).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            SurplusSlotParts = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "BrowsMaleHumanoid04")
                    { Type = HeadPart.TypeEnum.Eyebrows },
            },
            PresenceSatisfiedExtras = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLineMaleRedguard3")
                    { Type = HeadPart.TypeEnum.Misc, FromExtraParts = true },
            },
        };
        r.HasMismatch.Should().BeFalse();
        r.BuildReason().Should().BeEmpty();
    }

    // ---- RaceDefaultsParticipateInReconciliation (the overlay-race rule) --------------------

    [Fact]
    public void RaceDefaults_SkippedForOverlayHeadPartListRaces()
    {
        // Vampire races carry OverlayHeadPartList: their HeadData is a runtime overlay
        // (the vampirism transform), not slot-fill defaults the baked head must carry.
        // In-game verified 2026-08-16 (Bruma Vampire Fledgling renders normal despite the
        // Dawnguard-era default head being absent from her .nif), while the mutation
        // matrix proved race-default misses on NON-overlay races DO dark-face (A4/A5).
        var overlay = new Race(FormKey.Factory("088794:Skyrim.esm"), SkyrimRelease.SkyrimSE)
        {
            Flags = Race.Flag.FaceGenHead | Race.Flag.OverlayHeadPartList,
        };
        var normal = new Race(FormKey.Factory("013746:Skyrim.esm"), SkyrimRelease.SkyrimSE)
        {
            Flags = Race.Flag.FaceGenHead,
        };

        FaceGenConsistencyAnalyzer.RaceDefaultsParticipateInReconciliation(overlay).Should().BeFalse();
        FaceGenConsistencyAnalyzer.RaceDefaultsParticipateInReconciliation(normal).Should().BeTrue();
    }

    // ---- IsSingularSlotType (the first-listed-winner rule) ----------------------------------

    [Theory]
    [InlineData(HeadPart.TypeEnum.Eyes, true)]
    [InlineData(HeadPart.TypeEnum.Hair, true)]         // Khajiit ear tufts: second Hair dropped
    [InlineData(HeadPart.TypeEnum.Face, true)]
    [InlineData(HeadPart.TypeEnum.Eyebrows, true)]     // Anoriath/B6: first-listed Brows wins
    [InlineData(HeadPart.TypeEnum.FacialHair, true)]
    [InlineData(HeadPart.TypeEnum.Scars, false)]       // B5: a second gash was expected → dark
    [InlineData(HeadPart.TypeEnum.Misc, false)]        // grab-bag: mouth must not excuse others
    [InlineData(null, false)]
    public void IsSingularSlotType_MatchesTheFieldEvidence(HeadPart.TypeEnum? type, bool singular)
    {
        FaceGenConsistencyAnalyzer.IsSingularSlotType(type).Should().Be(singular);
    }

    [Fact]
    public void BuildReason_ExtraParts_AreAnnotatedInTheEvidenceList()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairLineFemaleNord15")
                    { Type = HeadPart.TypeEnum.Misc, FromExtraParts = true },
            },
        };

        r.BuildReason().Should().Contain("'HairLineFemaleNord15' (" + HpA + ") (extra part)");
    }

    [Fact]
    public void HasMismatch_NullHeadPartLinksZero_DoesNotTrigger()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 0 };
        r.HasMismatch.Should().BeFalse();
    }

    [Fact]
    public void HasMismatch_AnyTriggerAmongMany_IsTrue()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "X" },
            NullHeadPartLinks = 3,
        };
        r.HasMismatch.Should().BeTrue();
    }

    // ---- Result default property values -----------------------------------------------------

    [Fact]
    public void Result_Defaults_AreEmptyNonNullCollections()
    {
        var r = new FaceGenConsistencyAnalyzer.Result();
        r.NifParsed.Should().BeFalse();
        r.NifError.Should().BeNull();
        r.BakedShapeCount.Should().Be(0);
        r.ResolvedHeadPartCount.Should().Be(0);
        r.NullHeadPartLinks.Should().Be(0);
        r.MissingBakedShapes.Should().NotBeNull().And.BeEmpty();
        r.OrphanBakedShapes.Should().NotBeNull().And.BeEmpty();
        r.UnresolvedHeadParts.Should().NotBeNull().And.BeEmpty();
    }

    // ---- Result.Kind (cause classification) -------------------------------------------------
    //
    // The classifier is what keeps the message honest: "generated against a different version"
    // is only plausible for a single-slot difference. Anything broader is a different-source
    // mismatch (a lost plugin conflict or a lost FaceGen file conflict).

    [Fact]
    public void Kind_EmptyResult_IsNone()
    {
        new FaceGenConsistencyAnalyzer.Result().Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.None);
    }

    [Fact]
    public void Kind_SingleMissingNoOrphans_IsSingleHeadPartDifference()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.SingleHeadPartDifference);
    }

    [Fact]
    public void Kind_SingleMissingWithSingleOrphan_IsStillSingleHeadPartDifference()
    {
        // One slot swapped (missing X / baked Y) is still a one-part difference.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            OrphanBakedShapes = new[] { "SomeOtherBrows" },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.SingleHeadPartDifference);
    }

    [Fact]
    public void Kind_SingleMissingWithManyOrphans_IsDifferentSource()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            OrphanBakedShapes = new[] { "PAN_Hair", "PAN_Hairline" },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.DifferentSource);
    }

    [Fact]
    public void Kind_MultipleMissing_IsDifferentSource()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        r.Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.DifferentSource);
    }

    [Fact]
    public void Kind_UnresolvedOrNullOnly_IsBrokenHeadPartLinks()
    {
        new FaceGenConsistencyAnalyzer.Result { UnresolvedHeadParts = new[] { HpC } }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.BrokenHeadPartLinks);
        new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.BrokenHeadPartLinks);
    }

    [Fact]
    public void Kind_OrphansOnly_IsExtraBakedShapesOnly()
    {
        new FaceGenConsistencyAnalyzer.Result { OrphanBakedShapes = new[] { "Floater" } }
            .Kind.Should().Be(FaceGenConsistencyAnalyzer.MismatchKind.ExtraBakedShapesOnly);
    }

    // ---- Result.BuildReason -----------------------------------------------------------------

    [Fact]
    public void BuildReason_NothingToReport_ReturnsEmptyString()
    {
        new FaceGenConsistencyAnalyzer.Result().BuildReason().Should().BeEmpty();
    }

    [Fact]
    public void BuildReason_OrphansOnly_ReportsNothingAtAll()
    {
        // Validation reports only what the user can see in game. A purely additive .nif produces
        // no visible defect (see HasMismatch_OrphanBakedShapesOnly_IsFalse for the evidence), so
        // it must produce no row and no tooltip — not a softened one.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            OrphanBakedShapes = new[] { "Floater", "AnotherFloater" },
        };
        r.BuildReason().Should().BeEmpty();
    }

    [Fact]
    public void BuildReason_MixedMissingAndOrphans_StillWarnsAboutDarkFace()
    {
        // Guard the boundary: once a head part the record needs IS missing, the dark-face warning
        // and the remedies come back, orphans or not.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
            OrphanBakedShapes = new[] { "Floater" },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("dark-face bug");
        reason.Should().Contain("Likely cause(s)");
        reason.Should().Contain("Floater");
    }

    [Fact]
    public void BuildReason_SingleMissingBakedShape_DescribesSlotDrift_NeverDefiningPluginVersions()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadNord") },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("'MaleHeadNord'");
        reason.Should().Contain(HpA.ToString());
        reason.Should().Contain("uses one head part that isn't in the FaceGen .nif");
        // The old text blamed "a different version of <plugin that DEFINES the part>" —
        // usually a resource master or the base game (frozen for a decade). The single-slot
        // remedy must describe record-vs-mesh drift without naming that plugin's version.
        reason.Should().Contain("different part in this slot");
        reason.Should().NotContain("Version mismatch");
        // The trailing "verified to dark-face / Face Discoloration Fix masks it" note was
        // accurate but not actionable — documentation, not remedy — and was removed on user
        // ruling (2026-08-16). The evidence lives in the HasMismatch remarks and the doc.
        reason.Should().NotContain("Face Discoloration Fix");
        // Still no "you can probably ignore this" reassurance.
        reason.Should().NotContain("ignore this");
    }

    [Fact]
    public void BuildReason_SingleDiff_SubjectSuppliesRecord_BlamesTheModsOwnPairing()
    {
        // The record-aware single-slot remedies are LoadOrder-scope only now: SelectedMod
        // scope concludes with the uniform authoring-issue line instead. No slot-pairing
        // claim ("this one slot") either — the lone orphan opposite a lone miss need not
        // share its slot Type (observed: a Face-type miss opposite a Brows-type orphan),
        // and no re-install advice (it cannot fix data the mod itself ships mismatched).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "BrowsMaleHumanoid04") },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.LoadOrder,
            subjectSuppliesRecord: true);

        reason.Should().Contain("ships both the plugin record and the FaceGen .nif");
        reason.Should().Contain("Comments and Bugs sections");
        reason.Should().NotContain("this one slot");
        reason.Should().NotContain("Re-install");
        reason.Should().NotContain("Version mismatch");
    }

    [Fact]
    public void BuildReason_SingleDiff_MeshOnlyPairing_WithBaseGameRecord_PointsAtExpectedOverhaul()
    {
        // Mesh-only mod paired with a base-game record: base-game records don't change, so
        // the remedy must point at the overhaul the mesh was baked against, not versions.
        // LoadOrder scope — SelectedMod scope concludes with the authoring line instead.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(
                    FormKey.Factory("0C710A:Skyrim.esm"), "BrowsMaleHumanoid04"),
            },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.LoadOrder,
            subjectSuppliesRecord: false);

        reason.Should().Contain("no plugin record for this NPC");
        reason.Should().Contain("base-game records don't change");
        reason.Should().NotContain("different version of Skyrim.esm");
    }

    [Fact]
    public void BuildReason_ForeignPluginRemedies_AreLoadOrderScopeOnly()
    {
        // The Mod Issues scanner (SelectedMod scope) resolves only the mod's own
        // plugins + the record's origin, and reads the mod's NIF from its own
        // folder — so neither "another plugin renamed the part" nor "another mod
        // is overwriting the .nif" can CAUSE its mismatches; those causes exist
        // only where conflict winners resolve (Validate Output / LoadOrder scope).
        var single = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "BrowsMaleHumanoid04") },
        };
        single.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.LoadOrder)
            .Should().Contain("renames it breaks the match");
        single.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod, subjectSuppliesRecord: true)
            .Should().NotContain("check whether another plugin edits that head part");
        single.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod, subjectSuppliesRecord: false)
            .Should().NotContain("check whether another plugin edits that head part");

        var multi = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "A"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "B"),
            },
            OrphanBakedShapes = new[] { "C", "D" },
        };
        multi.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod)
            .Should().NotContain("overwriting this NPC's FaceGen");
    }

    [Fact]
    public void BuildReason_MissingPart_NamesSameTypeBakedCounterpart()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "BrowsMaleHumanoid04")
                    { BakedSameTypeShapes = new[] { "BrowsMaleHumanoid01" } },
            },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("the .nif has 'BrowsMaleHumanoid01' instead");
    }

    [Fact]
    public void BuildReason_OrphansNamedAsCounterparts_AreNotRepeated()
    {
        // "the .nif has 'X' instead" already names X — repeating it in the orphan section
        // was noise (user feedback 2026-08-16). Only orphans NOT named that way remain,
        // in bullet format matching the .esp list.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairFemaleOrc02")
                    { BakedSameTypeShapes = new[] { "HairFemaleOrc04" } },
            },
            OrphanBakedShapes = new[] { "HairFemaleOrc04", "SomeExtraShape" },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("the .nif has 'HairFemaleOrc04' instead");
        reason.Should().Contain("\n • SomeExtraShape");
        // The counterpart appears exactly once — in the "instead" clause, not the orphan list.
        reason.IndexOf("HairFemaleOrc04", StringComparison.Ordinal)
            .Should().Be(reason.LastIndexOf("HairFemaleOrc04", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReason_OrphanSection_OmittedWhenAllOrphansAreCounterparts()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairFemaleOrc02")
                    { BakedSameTypeShapes = new[] { "HairFemaleOrc04" } },
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLineFemaleOrc01")
                    { BakedSameTypeShapes = new[] { "HairLineFemaleOrc03" } },
            },
            OrphanBakedShapes = new[] { "HairFemaleOrc04", "HairLineFemaleOrc03" },
        };

        r.BuildReason().Should().NotContain("in the .nif but not the .esp");
    }

    [Fact]
    public void BuildReason_HeadlineClause_IsScopeAndFidelityDependent()
    {
        // "not coming from the same appearance mod" is a load-order inference. SelectedMod
        // scope resolves only the mod's own data — no other mod is in scope to blame — and
        // even LoadOrder scope hedges with "may" (mods are sometimes just authored
        // incorrectly; user feedback 2026-08-16).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };

        r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.LoadOrder)
            .Should().Contain("may not be coming from the same appearance mod");
        r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod)
            .Should().NotContain("coming from the same appearance mod");
    }

    [Fact]
    public void BuildReason_MultipleMissing_BlamesConflictsNotPluginVersion()
    {
        // Regression guard for the reported case: several unmatched head parts plus foreign
        // baked shapes must be reported as a source mismatch (lost plugin / file conflict),
        // never as "the FaceGen was generated against a different version of Skyrim.esm".
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("051148:Skyrim.esm"), "HairFemaleNord07"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("0510BB:Skyrim.esm"), "HairLineFemaleNord07"),
            },
            OrphanBakedShapes = new[] { "PAN_AbeloneHair", "PAN_AbeloneHairline" },
        };
        var reason = r.BuildReason();

        reason.Should().NotContain("different version");
        reason.Should().Contain("different set of head parts than the NPC's .esp plugin record");
        reason.Should().Contain("Plugin conflict");
        reason.Should().Contain("Asset conflict");
        // The load-order remedies must never name the plugins that DEFINE the unmatched head
        // parts: that is usually a resource master (High Poly Head.esm), not the record winner,
        // so it read as a plugin conflict that wasn't happening. Winning Source names the winner.
        // (The headline legitimately says "not coming from the same appearance mod", so this is
        // asserted against the remedy section alone.)
        var remedies = reason[reason.IndexOf("Likely cause(s)", StringComparison.Ordinal)..];
        remedies.Should().NotContain("coming from");
        remedies.Should().NotContain("Skyrim.esm");
    }

    [Fact]
    public void BuildReason_LoadOrderRemedies_NameNoPlugins()
    {
        // The two conflict remedies are about NPC2's own output winning; naming plugins here was
        // noise at best and misleading at worst (see the regression guard above).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("Plugin conflict");
        reason.Should().Contain("Asset conflict");
        // 'HeadParts.esp' may still appear in the EVIDENCE list (the FormKeys), but never in the
        // remedies. Check the remedy section alone.
        var remedies = reason[reason.IndexOf("Likely cause(s)", StringComparison.Ordinal)..];
        remedies.Should().NotContain("HeadParts.esp");
        remedies.Should().NotContain("vanilla Skyrim");
    }

    [Fact]
    public void BuildReason_SelectedModScope_ConcludesWithModPagePointer_NoRemedyMenu()
    {
        // Mod-scoped analysis compares the mod's own record against the mod's own mesh — no
        // deployment, no load order, no other mod is in the verdict. A "Likely cause(s)" menu
        // is meaningless there: the mod is simply authored incorrectly, and the only pointer
        // worth giving is the mod page (user ruling 2026-08-16).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod);

        // "This is an authoring issue" is not stated: in a mod scan that is obvious, so
        // the conclusion is just the mod-page pointer (user wording).
        reason.Should().Contain("Check the mod page's Comments and Bugs sections for known issues or an updated version.");
        reason.Should().NotContain("authored incorrectly");
        reason.Should().NotContain("Likely cause(s)");
        reason.Should().NotContain("Re-install");
        reason.Should().NotContain("Validate Output");
        reason.Should().NotContain("load order");
        reason.Should().NotContain("Mods menu");
    }

    [Fact]
    public void BuildReason_SelectedModScope_AllVanillaParts_MakesNoRecordInference()
    {
        // The scanner KNOWS whether the mod supplies a plugin record (subjectSuppliesRecord)
        // — inferring "no matching plugin record" from the parts all being vanilla was
        // wrong-headed, and "check the plugin is assigned / the right source NPC" was
        // meaningless in this scope (user feedback 2026-08-16, notes 2a–2c).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("051148:Skyrim.esm"), "HairFemaleNord07"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(FormKey.Factory("0510BB:Skyrim.esm"), "HairLineFemaleNord07"),
            },
        };
        var reason = r.BuildReason(scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod);

        reason.Should().NotContain("all vanilla Skyrim ones");
        reason.Should().NotContain("source NPC");
        reason.Should().Contain("Check the mod page's Comments and Bugs sections");
    }

    // ---- DeliveryFidelity: proven-faithful delivery replaces the conflict remedies ----------

    [Fact]
    public void HeadPartRef_FromRaceDefaults_DefaultsFalse_AndParticipatesInEquality()
    {
        var own = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "ChildMouth");
        var raceDefault = new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "ChildMouth") { FromRaceDefaults = true };

        own.FromRaceDefaults.Should().BeFalse();
        raceDefault.FromRaceDefaults.Should().BeTrue();
        own.Should().NotBe(raceDefault);
    }

    [Fact]
    public void BuildReason_RaceDefaultParts_AreAnnotatedInTheEvidenceList()
    {
        // The annotation is evidence, not a remedy, so it appears regardless of fidelity.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "ChildMouth") { FromRaceDefaults = true },
            },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("'ChildMouth' (" + HpB + ") (race default)");
        reason.Should().NotContain("'Hair' (" + HpA + ") (race default)");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_DropsConflictRemedies_AndBlamesTheModsOwnData()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        // The delivery is proven faithful: no conflict accusations, and no "different mods"
        // inference in the headline — the mod's own files simply disagree.
        reason.Should().NotContain("may be overwritten");
        reason.Should().NotContain("not coming from the same appearance mod");
        reason.Should().Contain("Not a deployment problem");
        reason.Should().Contain("The selected mod's own files disagree with each other");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_VanillaData_NamesTheBaseGamesOwnBug()
    {
        // The verified Dawnguard-vampire case: record and mesh are both vanilla's own, and they
        // genuinely mismatch in an unmodded game. Single difference (Demon vs Vampire eyes).
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(
                    FormKey.Factory("02425E:Skyrim.esm"), "MaleEyesHumanDemon"),
            },
            OrphanBakedShapes = new[] { "MaleEyesHumanVampire" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.VanillaOwnData);

        reason.Should().Contain("base game's own files");
        reason.Should().Contain("unmodded game");
        reason.Should().NotContain("may be overwritten");
        reason.Should().NotContain("Version mismatch"); // the generic single-diff guess is replaced
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_MeshOnly_ExplainsTheOriginRecordPairing()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairMaleNord01"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HumanBeard02"),
            },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModMeshOnly);

        reason.Should().Contain("supplies only FaceGen files");
        reason.Should().Contain("original plugin");
        reason.Should().NotContain("may be overwritten");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_AllRaceDefaults_NamesBothRaceSlotCauses()
    {
        // Two verified-in-the-wild patterns share this shape: RS Children (the mod's race edit is
        // merged away, so race defaults roll back to vanilla under its mesh) and Women of Unslaad
        // (the mod's record stopped listing parts its mesh was baked with, so the vanilla race
        // defaults apply). One resolution context cannot tell them apart, so the row must present
        // both — and must not accuse the load order, which is proven innocent.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "MaleHeadChild") { FromRaceDefaults = true },
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "ChildMouth") { FromRaceDefaults = true },
            },
            OrphanBakedShapes = new[] { "0RCOChildMouth" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("Every mismatched part is a RACE default");
        reason.Should().Contain("race edits are not carried over");
        reason.Should().Contain("record no longer lists");
        reason.Should().NotContain("may be overwritten");
    }

    [Fact]
    public void BuildReason_FaithfulDelivery_SingleDifference_KeepsTheRenameHint()
    {
        // One flipped slot can be a head part RENAMED by an overriding plugin (the NPC record
        // still points at the same FormKey) — that cause survives proven-faithful delivery.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "HairLineMaleRedguard3"),
            },
            OrphanBakedShapes = new[] { "HairLineMaleRedguard4" },
        };
        var reason = r.BuildReason(
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("renamed head part");
        reason.Should().Contain("Not a deployment problem");
    }

    [Fact]
    public void BuildReason_Fidelity_IsIgnoredOutsideLoadOrderScope()
    {
        // The mugshot path resolves mod-scoped; fidelity is a load-order concept and must not
        // change the mod-scoped remedies.
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
        };
        var reason = r.BuildReason(
            scope: FaceGenConsistencyAnalyzer.ReasonScope.SelectedMod,
            fidelity: FaceGenConsistencyAnalyzer.DeliveryFidelity.SelectedModOwnData);

        reason.Should().Contain("Check the mod page's Comments and Bugs sections");
        reason.Should().NotContain("Not a deployment problem");
    }

    [Fact]
    public void BuildReason_NullLinks_ReportsCountAndFix()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 4 };
        var reason = r.BuildReason();
        reason.Should().Contain("4 empty entry(s)");
        reason.Should().Contain("clean it in xEdit");
    }

    [Fact]
    public void BuildReason_UnresolvedHeadPart_ReportsFormKeyAndNamesThePluginToInstall()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            UnresolvedHeadParts = new[] { HpC },
        };
        var reason = r.BuildReason();
        reason.Should().Contain(HpC.ToString());
        reason.Should().Contain("no plugin in your load order has them");
        reason.Should().Contain("Install and enable the mod that owns these head parts");
        reason.Should().Contain(HpC.ModKey.FileName.ToString());
    }

    [Fact]
    public void BuildReason_AllCategories_AppearTogether()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[] { new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Brows") },
            NullHeadPartLinks = 2,
            UnresolvedHeadParts = new[] { HpC },
            OrphanBakedShapes = new[] { "Orphan1" },
        };
        var reason = r.BuildReason();

        reason.Should().Contain("'Brows'");
        reason.Should().Contain("2 empty entry(s)");
        reason.Should().Contain(HpC.ToString());
        reason.Should().Contain("Orphan1");
    }

    [Fact]
    public void BuildReason_MissingBakedShapes_TruncatesWithAndNMore()
    {
        // 10 missing parts, cap of 3 -> 3 shown + an "…and 7 more" tail line.
        var missing = Enumerable.Range(0, 10)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        var reason = r.BuildReason(maxPerCategory: 3);

        reason.Should().Contain("HP_0");
        reason.Should().Contain("HP_2");
        reason.Should().NotContain("HP_3"); // beyond the cap, only summarized
        reason.Should().Contain("…and 7 more head part(s) with no baked shape.");
    }

    [Fact]
    public void BuildReason_MissingBakedShapes_ExactlyAtCap_NoTruncationTail()
    {
        // Count == cap: every entry shown, no "…and N more" tail (the tail uses strict >).
        var missing = Enumerable.Range(0, 3)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        var reason = r.BuildReason(maxPerCategory: 3);

        reason.Should().Contain("HP_0").And.Contain("HP_1").And.Contain("HP_2");
        reason.Should().NotContain("more head part(s) with no baked shape");
    }

    [Fact]
    public void BuildReason_UnresolvedHeadParts_TruncatesWithAndNMore()
    {
        var unresolved = Enumerable.Range(0, 6)
            .Select(i => FormKey.Factory($"00{i:D4}:Missing.esp"))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { UnresolvedHeadParts = unresolved };

        var reason = r.BuildReason(maxPerCategory: 2);

        reason.Should().Contain("…and 4 more unresolved head part(s).");
    }

    // Orphans only render as corroborating detail, so these two need a missing head part to
    // get past the "nothing the user can see" guard.
    private static readonly FaceGenConsistencyAnalyzer.HeadPartRef[] OneMissing =
        { new(HpA, "Brows") };

    [Fact]
    public void BuildReason_OrphanBakedShapes_TruncatesWithAndNMore()
    {
        var orphans = Enumerable.Range(0, 7).Select(i => "Orphan" + i).ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = OneMissing,
            OrphanBakedShapes = orphans,
        };

        var reason = r.BuildReason(maxPerCategory: 2);

        reason.Should().Contain("\n • Orphan0");
        reason.Should().Contain("\n • Orphan1");
        reason.Should().Contain("…and 5 more");
        reason.Should().NotContain("Orphan2");
    }

    [Fact]
    public void BuildReason_OrphanBakedShapes_AreBulleted()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = OneMissing,
            OrphanBakedShapes = new[] { "A", "B", "C" },
        };
        // Bullet format matching the .esp list (user feedback 2026-08-16); under the
        // default cap (8) all three are shown, no "…and N more".
        var reason = r.BuildReason();
        reason.Should().Contain("\n • A\n • B\n • C");
        reason.Should().NotContain("more");
    }

    [Fact]
    public void BuildReason_HeaderAlwaysPrecedesDetail()
    {
        var r = new FaceGenConsistencyAnalyzer.Result { NullHeadPartLinks = 1 };
        var reason = r.BuildReason();
        reason.Should().StartWith("Broken head part references (a common cause of the in-game dark-face bug):");
    }

    [Fact]
    public void BuildReason_OrdersSections_EvidenceThenRemedies()
    {
        var r = new FaceGenConsistencyAnalyzer.Result
        {
            MissingBakedShapes = new[]
            {
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpA, "Hair"),
                new FaceGenConsistencyAnalyzer.HeadPartRef(HpB, "HairLine"),
            },
            OrphanBakedShapes = new[] { "Orphan1" },
        };
        var reason = r.BuildReason();

        reason.IndexOf("in the .esp but not the .nif", StringComparison.Ordinal)
            .Should().BeLessThan(reason.IndexOf("in the .nif but not the .esp", StringComparison.Ordinal));
        reason.IndexOf("in the .nif but not the .esp", StringComparison.Ordinal)
            .Should().BeLessThan(reason.IndexOf("Likely cause(s), most common first:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReason_DefaultCap_IsEight()
    {
        // 9 missing parts with the DEFAULT cap (8) -> "…and 1 more".
        var missing = Enumerable.Range(0, 9)
            .Select(i => new FaceGenConsistencyAnalyzer.HeadPartRef(
                FormKey.Factory($"00{i:D4}:HeadParts.esp"), "HP_" + i))
            .ToArray();
        var r = new FaceGenConsistencyAnalyzer.Result { MissingBakedShapes = missing };

        r.BuildReason().Should().Contain("…and 1 more head part(s) with no baked shape.");
    }

    // ---- IsGenericNode (private static, via Reflect) ----------------------------------------

    private static bool IsGenericNode(string shapeName, string? primaryHeadShapeName) =>
        Reflect.InvokeStatic<FaceGenConsistencyAnalyzer, bool>(
            "IsGenericNode", shapeName, primaryHeadShapeName);

    [Fact]
    public void IsGenericNode_MatchesPrimaryHeadShapeName_CaseInsensitive()
    {
        IsGenericNode("MyHeadShape", "myheadshape").Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_NpcHeadSubstring_IsGeneric()
    {
        IsGenericNode("NPC Head [Head]", null).Should().BeTrue();
        IsGenericNode("Some NPC Head marker", null).Should().BeTrue();
        IsGenericNode("npc head", null).Should().BeTrue(); // substring match is case-insensitive
    }

    [Fact]
    public void IsGenericNode_BsFaceGenPrefix_IsGeneric()
    {
        IsGenericNode("BSFaceGenNiNodeSkinned", null).Should().BeTrue();
        IsGenericNode("bsfacegenfoo", null).Should().BeTrue(); // prefix match is case-insensitive
    }

    [Fact]
    public void IsGenericNode_FaceGenPrefix_IsGeneric()
    {
        IsGenericNode("FaceGenSomething", null).Should().BeTrue();
        IsGenericNode("facegen", null).Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_OrdinaryHeadPartName_IsNotGeneric()
    {
        IsGenericNode("MaleHeadNord", null).Should().BeFalse();
        IsGenericNode("Brows", "MaleHeadNord").Should().BeFalse();
    }

    [Fact]
    public void IsGenericNode_NullPrimary_FallsThroughToPrefixChecks()
    {
        // A null primaryHeadShapeName must not throw; classification proceeds on the name alone.
        IsGenericNode("OrdinaryShape", null).Should().BeFalse();
        IsGenericNode("FaceGenHead", null).Should().BeTrue();
    }

    [Fact]
    public void IsGenericNode_EmptyPrimary_DoesNotMatchEmptyShape()
    {
        // Guard: an empty primary name must not make an arbitrary shape "primary" by equality.
        IsGenericNode("RealHeadPart", "").Should().BeFalse();
    }

    [Theory]
    [InlineData("NPC Head", null, true)]
    [InlineData("BSFaceGenNiNode", null, true)]
    [InlineData("FaceGenNode", null, true)]
    [InlineData("Hair_Long", null, false)]
    [InlineData("Eyes", "Eyes", true)]   // matches primary
    [InlineData("Eyes", "Brows", false)] // does not match primary, no prefix/substring
    public void IsGenericNode_TruthTable(string shapeName, string? primary, bool expected)
    {
        IsGenericNode(shapeName, primary).Should().Be(expected);
    }

    // ---- CollectShapeNamesOfType (public static; in-memory Mutagen records) -----------------
    //
    // Feeds ResolvedNpcMeshPaths.EyeShapeNames — the renderer's authoritative IsEye input.
    // Motivating case: FoxGlove Auri's eyeball is an ENVMAP-typed shape named "FoxGloveEyeMesh"
    // (singular), which evades the renderer's plural-"Eyes" name heuristic and received
    // eye-socket SSAO until classified via its HeadPart record here.

    private static Func<FormKey, IHeadPartGetter?> Resolver(params HeadPart[] parts)
    {
        var map = parts.ToDictionary(p => p.FormKey, p => (IHeadPartGetter)p);
        return fk => map.TryGetValue(fk, out var hp) ? hp : null;
    }

    private static HeadPart NewHeadPart(
        SkyrimMod mod, string editorId, HeadPart.TypeEnum? type)
    {
        var hp = mod.HeadParts.AddNew();
        hp.EditorID = editorId;
        hp.Type = type;
        return hp;
    }

    [Fact]
    public void CollectShapeNamesOfType_CollectsTypedPartAndItsExtraParts_ExcludesOtherTypes()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "FoxGloveEyeMesh", HeadPart.TypeEnum.Eyes);
        var extra = NewHeadPart(mod, "FoxGloveEyeExtra", null); // Extra Parts are typically untyped
        var hair = NewHeadPart(mod, "HairShape", HeadPart.TypeEnum.Hair);
        eyes.ExtraParts.Add(extra.FormKey.ToLink<IHeadPartGetter>());

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());
        npc.HeadParts.Add(hair.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes, extra, hair), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "FoxGloveEyeMesh", "FoxGloveEyeExtra" });
        names.Contains("foxgloveeyemesh").Should().BeTrue("shape-name reconciliation is case-insensitive");
    }

    [Fact]
    public void CollectShapeNamesOfType_FallsBackToRaceDefault_WhenNpcLacksSlot()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var raceEyes = NewHeadPart(mod, "RaceEyesDefault", HeadPart.TypeEnum.Eyes);
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var headData = new HeadData();
        var hpRef = new HeadPartReference();
        hpRef.Head.SetTo(raceEyes.FormKey);
        headData.HeadParts.Add(hpRef);
        race.HeadData = new GenderedItem<HeadData?>(headData, null);

        var npc = MutagenFixtures.NewNpc(mod, race: race); // male by default, no own eyes

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(raceEyes), _ => race, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "RaceEyesDefault" });
    }

    [Fact]
    public void CollectShapeNamesOfType_SkipsRaceDefault_WhenNpcOccupiesSlot()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var npcEyes = NewHeadPart(mod, "NpcEyes", HeadPart.TypeEnum.Eyes);
        var raceEyes = NewHeadPart(mod, "RaceEyesDefault", HeadPart.TypeEnum.Eyes);
        var race = MutagenFixtures.NewRace(mod, "TestRace");
        var headData = new HeadData();
        var hpRef = new HeadPartReference();
        hpRef.Head.SetTo(raceEyes.FormKey);
        headData.HeadParts.Add(hpRef);
        race.HeadData = new GenderedItem<HeadData?>(headData, null);

        var npc = MutagenFixtures.NewNpc(mod, race: race);
        npc.HeadParts.Add(npcEyes.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(npcEyes, raceEyes), _ => race, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "NpcEyes" });
    }

    [Fact]
    public void CollectShapeNamesOfType_CircularExtraParts_Terminates()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "LoopingEyes", HeadPart.TypeEnum.Eyes);
        eyes.ExtraParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>()); // self-referencing Extra Part

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "LoopingEyes" });
    }

    [Fact]
    public void CollectShapeNamesOfType_UnresolvableAndNullLinks_AreSkipped()
    {
        var mod = MutagenFixtures.NewMod("Test.esp");
        var eyes = NewHeadPart(mod, "GoodEyes", HeadPart.TypeEnum.Eyes);

        var npc = MutagenFixtures.NewNpc(mod);
        npc.HeadParts.Add(eyes.FormKey.ToLink<IHeadPartGetter>());
        npc.HeadParts.Add(MutagenFixtures.Fk("0DEAD0:Missing.esp").ToLink<IHeadPartGetter>());

        var names = FaceGenConsistencyAnalyzer.CollectShapeNamesOfType(
            npc, Resolver(eyes), _ => null, HeadPart.TypeEnum.Eyes);

        names.Should().BeEquivalentTo(new[] { "GoodEyes" });
    }

    // ---- IsExtraPresenceSatisfied (reconciliation rule 3, incl. the shared-model clause) ----

    private static FaceGenConsistencyAnalyzer.HeadPartRef TopLevel(
        FormKey fk, string edid, string? model = null)
        => new(fk, edid) { AncestorFormKey = fk, ModelPath = model };

    private static FaceGenConsistencyAnalyzer.HeadPartRef ExtraOf(
        FormKey ancestor, FormKey fk, string edid, string? model = null)
        => new(fk, edid) { FromExtraParts = true, AncestorFormKey = ancestor, ModelPath = model };

    private static IReadOnlySet<string> Baked(params string[] names)
        => new HashSet<string>(names, System.StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ExtraPresence_TopLevelPart_NeverSatisfied()
    {
        // Top-level parts reconcile by NAME (rule 1); the presence predicate must not
        // excuse them even when orphans exist.
        var hair = TopLevel(HpA, "HairFemaleNord15", @"meshes\hair\hair15.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                hair, anyOrphans: true, new[] { hair }, Baked())
            .Should().BeFalse();
    }

    [Fact]
    public void ExtraPresence_OrphanStandIn_Satisfies()
    {
        // Gaiden Shinji / Brand-Shei: a renamed hairline's shape is present under another
        // name (an orphan), which satisfies the extra.
        var hair = TopLevel(HpA, "HairMaleRedguard4", @"meshes\hair\hair4.nif");
        var hairline = ExtraOf(HpA, HpB, "HairLineMaleRedguard3", @"meshes\hair\hairline3.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                hairline, anyOrphans: true, new[] { hair, hairline }, Baked("HairMaleRedguard4"))
            .Should().BeTrue();
    }

    [Fact]
    public void ExtraPresence_BakedSiblingExtra_Satisfies()
    {
        var hair = TopLevel(HpA, "Hair", @"meshes\hair\hair.nif");
        var lineA = ExtraOf(HpA, HpB, "HairLineA", @"meshes\hair\lineA.nif");
        var lineB = ExtraOf(HpA, HpC, "HairLineB", @"meshes\hair\lineB.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                lineA, anyOrphans: false, new[] { hair, lineA, lineB }, Baked("Hair", "HairLineB"))
            .Should().BeTrue();
    }

    [Fact]
    public void ExtraPresence_DistinctModel_AncestorBaked_StillFlags()
    {
        // Matrix variant A7: the hairline is DISTINCT geometry — its ancestor hair being
        // baked does NOT satisfy it, and its absence dark-faces in game. Regression guard
        // for the shared-model clause staying narrow.
        var hair = TopLevel(HpA, "HairFemaleNord15", @"meshes\hair\hair15.nif");
        var hairline = ExtraOf(HpA, HpB, "HairLineFemaleNord15", @"meshes\hair\hairline15.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                hairline, anyOrphans: false, new[] { hair, hairline }, Baked("HairFemaleNord15"))
            .Should().BeFalse();
    }

    [Fact]
    public void ExtraPresence_SharedModel_AncestorBaked_Satisfies()
    {
        // MQ304Ulfric / Men of Winter (in-game verified 2026-08-17): the "_1bit" beard twin
        // references its parent's own mesh, so the parent's baked shape already carries its
        // geometry — engine-inert.
        var beard = TopLevel(HpA, "111BeardUlfric",
            @"Meshes\actors\character\character assets\beards\humanbeardmedium09.nif");
        var twin = ExtraOf(HpA, HpB, "111BeardUlfric_1bit",
            @"Meshes\actors\character\character assets\beards\humanbeardmedium09.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                twin, anyOrphans: false, new[] { beard, twin }, Baked("111BeardUlfric"))
            .Should().BeTrue();
    }

    [Fact]
    public void ExtraPresence_SharedModel_AncestorNotBaked_StillFlags()
    {
        // The twin is only carried by its parent's GEOMETRY — an unbaked parent satisfies
        // nothing (the parent's own top-level row dominates that case anyway).
        var beard = TopLevel(HpA, "Beard", @"meshes\beards\beard.nif");
        var twin = ExtraOf(HpA, HpB, "Beard_1bit", @"meshes\beards\beard.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                twin, anyOrphans: false, new[] { beard, twin }, Baked("SomethingElse"))
            .Should().BeFalse();
    }

    [Fact]
    public void ExtraPresence_SharedModel_ToleratesSeparatorAndCaseDrift()
    {
        var beard = TopLevel(HpA, "Beard", "meshes/beards/Beard.NIF");
        var twin = ExtraOf(HpA, HpB, "Beard_1bit", @"Meshes\Beards\beard.nif");
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                twin, anyOrphans: false, new[] { beard, twin }, Baked("Beard"))
            .Should().BeTrue();
    }

    [Fact]
    public void ExtraPresence_MissingModelPaths_NeverMatchViaModelClause()
    {
        // Null/empty model paths must not read as "equal" — a modelless pairing proves
        // nothing about shared geometry.
        var beard = TopLevel(HpA, "Beard", model: null);
        var twin = ExtraOf(HpA, HpB, "Beard_1bit", model: null);
        FaceGenConsistencyAnalyzer.IsExtraPresenceSatisfied(
                twin, anyOrphans: false, new[] { beard, twin }, Baked("Beard"))
            .Should().BeFalse();

        FaceGenConsistencyAnalyzer.ModelPathsEqual(null, null).Should().BeFalse();
        FaceGenConsistencyAnalyzer.ModelPathsEqual("", "").Should().BeFalse();
        FaceGenConsistencyAnalyzer.ModelPathsEqual(@"a\b.nif", @"a\b.nif").Should().BeTrue();
        FaceGenConsistencyAnalyzer.ModelPathsEqual(@"a\b.nif", @"a\c.nif").Should().BeFalse();
    }

    // ---- IsSurplusSingularExtra (rule 2 on the flattened set — extras contest slots) ----

    [Fact]
    public void SurplusSingularExtra_OccupiedSingularSlot_IsSurplus()
    {
        var occupied = Baked("FacialHair", "Eyebrows"); // reuse the case-insensitive set helper
        // Men of Winter's "_1bit" beard twin behind the baked beard (in-game verified inert).
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(HeadPart.TypeEnum.FacialHair, occupied)
            .Should().BeTrue();
        // Miggyluv Hjoromir's lashes behind his top-level brows (in-game verified inert).
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(HeadPart.TypeEnum.Eyebrows, occupied)
            .Should().BeTrue();
    }

    [Fact]
    public void SurplusSingularExtra_MiscType_NeverSurplus()
    {
        // A7's hairline is typed Misc — the multi grab-bag never loses a slot contest,
        // so its absence keeps dark-facing. Regression guard for the amendment's scope.
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(HeadPart.TypeEnum.Misc, Baked("Misc"))
            .Should().BeFalse();
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(HeadPart.TypeEnum.Scars, Baked("Scars"))
            .Should().BeFalse();
    }

    [Fact]
    public void SurplusSingularExtra_UnoccupiedSlotOrNullType_NotSurplus()
    {
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(HeadPart.TypeEnum.Eyebrows, Baked("Hair"))
            .Should().BeFalse();
        FaceGenConsistencyAnalyzer.IsSurplusSingularExtra(null, Baked("Eyebrows"))
            .Should().BeFalse();
    }

    // NOTE: FaceGenConsistencyAnalyzer.Analyze / GetSurvey / CachedSurvey not covered:
    // they require a real FaceGen .nif parsed by NifMeshBuilder (from CharacterViewer.Rendering)
    // and a constructed CharacterPreviewCache — i.e. live rendering assets unavailable offline.
    // Those belong to the integration wave. The pure Result/HeadPartRef/IsGenericNode surface
    // exercised above carries the deterministic logic.
}
