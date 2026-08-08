using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ReadinessGate(
    string Code,
    string Name,
    bool Complete,
    int Completed,
    int Total,
    string Detail,
    string Action,
    string EvaluationState = "Evaluated",
    string? PrerequisiteCode = null);
public sealed record ReleaseReadiness(int Percent, bool ReadyForRelease, IReadOnlyList<ReadinessGate> Gates);

public sealed class ReleaseReadinessService(AeroLinkDbContext db)
{
    public async Task<ReleaseReadiness> CalculateAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).SingleAsync(x => x.Id == campaignId, ct);
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == campaign.BaselineId, ct);
        var requests = await db.SystemChangeRequests.AsNoTracking().Where(x => x.TargetReleaseId == campaign.ReleaseId && x.State != ChangeRequestState.Deferred).ToListAsync(ct);
        var impacts = await db.ImpactDispositions.AsNoTracking().Where(x => x.CampaignId == campaignId).ToListAsync(ct);
        var members = await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct); var revisionIds = members.Select(x => x.RevisionId).ToList();
        var derivedIds = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id) join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id where artifact.Level != RequirementLevel.System select member.RevisionId).ToListAsync(ct);
        var tracedDerivedIds = await db.RequirementTraces.AsNoTracking().Where(x => derivedIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId)).Select(x => x.SourceRevisionId).Distinct().ToListAsync(ct);
        // Coverage counts only when it is settled, which takes three things.
        //
        // It must not be suspect: a link carried across a requirement change that nobody has reconfirmed
        // would otherwise let a requirement reach release on a procedure written against its previous wording.
        //
        // The procedure revision it names must itself be Approved. Nothing checked this before, so a
        // requirement could be counted as covered by a procedure still in draft.
        //
        // And the procedure must have no revision in flight. A procedure being modified has to be reviewed
        // and approved before anything relying on it can be considered approved; counting the superseded
        // revision in the meantime would claim a settled answer while the answer is being rewritten.
        // The predicate itself lives in VerificationCoverageProjection so the requirements workspace filter
        // reads the same definition. Two implementations of "covered" is how a workspace comes to disagree
        // with the gate it is meant to be preparing for.
        var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baseline.Id, ct);
        var coveredIds = await VerificationCoverageProjection.SettledCoveredAsync(db, revisionIds, ct,
            procedureEffectivity?.RevisionIds, buildScoped: false);
        var docs = await db.ControlledDocuments.AsNoTracking().Where(x => x.BaselineId == baseline.Id).ToListAsync(ct);
        // A release cannot be declared ready while an unwaived controlled problem report remains a blocker.
        // This is deliberately project-scoped until product-line configuration provides exact release applicability.
        var problemBlockers = await db.ProblemReports.AsNoTracking().Where(x => x.ProjectId == campaign.ProjectId && x.IsReleaseBlocker && string.IsNullOrEmpty(x.WaiverRationale)).ToListAsync(ct);
        // Every requirement this release introduced or modified raised a verification impact item when its
        // change request was approved. Each one carries an owed decision: a procedure that covers it, or a
        // recorded confirmation that no test is required. A release with no requirement changes raises none,
        // and is complete by having nothing to decide.
        var verificationImpacts = await db.VerificationImpactItems.AsNoTracking().Where(x => x.ReleaseId == campaign.ReleaseId).ToListAsync(ct);
        var currentImpacts = verificationImpacts.Where(x => x.State != VerificationImpactState.Superseded).ToList();
        var impactDecided = currentImpacts.Count(x => x.State == VerificationImpactState.Resolved);
        var undecided = currentImpacts.Where(x => x.State != VerificationImpactState.Resolved).ToList();
        var testChangeReviews = await db.TestChangeReviews.AsNoTracking().Where(x => x.ReleaseId == campaign.ReleaseId).ToListAsync(ct);
        var approvedTestChangeReviews = testChangeReviews.Count(x => x.State == TestChangeReviewState.Approved);
        // What this build was planned to run, and whether it has run it.
        //
        // Loaded separately from the coverage-driven executions above, because a test set is not limited to
        // procedures that cover a changed requirement: exercising an area the change makes worth re-testing
        // is the other half of why a procedure is selected, and those procedures would be invisible here.
        var selectedRevisionIds = await db.BuildTestSetEntries.AsNoTracking()
            .Where(x => db.BuildTestSets.Any(set => set.Id == x.BuildTestSetId && set.ReleaseId == campaign.ReleaseId))
            .Select(x => x.ProcedureRevisionId).Distinct().ToListAsync(ct);
        // Scoped through the one shared rule, not a local predicate.
        //
        // The previous condition relaxed to "any execution at all" whenever the campaign had no software
        // build, because `campaign.SoftwareBuildId == null || ...` is simply true for every row in that case
        // and nothing constrained the release. A determination recorded against released Build 1.5 could
        // therefore satisfy Build 1.6's verification and evidence gates. ExecutionScope is now the single
        // authority, shared with the Test Results workspace so the gate and the page it prepares cannot
        // disagree about which runs belong to the build.
        var selectedLatest = (await ExecutionScope.LatestByProcedureAsync(
            db, selectedRevisionIds, campaign.ReleaseId, campaign.SoftwareBuildId, ct)).Values.ToList();
        var selectedPassed = selectedLatest.Count(x => x.Outcome == TestOutcome.Pass);
        var selectedRunIds = selectedLatest.Select(x => x.Id).ToList();
        var selectedEvidenced = selectedRunIds.Count == 0 ? 0 : await db.TestExecutionEvidence.AsNoTracking()
            .Where(x => selectedRunIds.Contains(x.TestExecutionId)).Select(x => x.TestExecutionId).Distinct().CountAsync(ct);
        // An empty set is only an answer when there was nothing to plan. A build that changed something and
        // has selected nothing has not been planned yet, and a gate that passed it would be reporting
        // "nothing left to run" about a decision nobody has made.
        var nothingToTest = testChangeReviews.Count == 0;

        IReadOnlyList<RequiredCodeTraceabilityRequirement> requiredCode = baseline.RequirementsMaterializedAt is null
            ? Array.Empty<RequiredCodeTraceabilityRequirement>()
            : await CodeTraceabilityProjection.RequiredAsync(db, campaign.ProjectId, campaign.ReleaseId, baseline.Id, ct);
        var requiredCodeRevisionIds = requiredCode.Select(x => x.RevisionId).ToList();
        var mappedCode = requiredCodeRevisionIds.Count == 0 ? 0 : await db.CodeTraceabilityRecords.AsNoTracking()
            .Where(x => x.ProjectId == campaign.ProjectId && x.ReleaseId == campaign.ReleaseId
                && requiredCodeRevisionIds.Contains(x.RequirementRevisionId))
            .Select(x => x.RequirementRevisionId).Distinct().CountAsync(ct);

        var integrated = requests.Count(x => x.State == ChangeRequestState.SelectedForBaseline); var disposed = impacts.Count(x => x.State != ImpactDispositionState.Pending);
        var baselineMaterialized = baseline.RequirementsMaterializedAt is not null;
        var gates = new List<ReadinessGate>
        {
            new("change_control","Change requests integrated",requests.Count > 0 && integrated == requests.Count,integrated,requests.Count,$"{requests.Count-integrated} non-deferred change request records remain outside the candidate baseline.","Approve and select every included change, or formally defer it."),
            new("impact_disposition","Impact analysis dispositioned",impacts.Count > 0 && disposed == impacts.Count,disposed,impacts.Count,$"{impacts.Count-disposed} impact findings remain pending.","Disposition requirement, trace, verification, and document impacts."),
            new("baseline","Requirement baseline materialized",baseline.State is CandidateBaselineState.Frozen or CandidateBaselineState.Released && baseline.RequirementsMaterializedAt is not null,baseline.RequirementsMaterializedAt is null?0:1,1,"The release needs an exact frozen and materialized requirement set.","Freeze the candidate and materialize its requirements."),
            new("verification_impact","Verification impact decided",impactDecided == verificationImpacts.Count,impactDecided,verificationImpacts.Count,undecided.Count==0?"Every new, modified, and orphaned requirement in this release has a recorded verification decision.":$"{undecided.Count} changed requirement(s) await a verification decision: {string.Join(", ",undecided.Take(3).Select(x=>x.SubjectDisplayNumber))}.","Assign each item to a test engineer, then record an approved procedure or a confirmation that no test is required."),
            new("test_change_reviews","Test change requests approved",
                testChangeReviews.Count > 0 && approvedTestChangeReviews == testChangeReviews.Count,
                approvedTestChangeReviews,testChangeReviews.Count,
                testChangeReviews.Count == 0
                    ? "No controlled test change requests have been raised for this software build."
                    : $"{testChangeReviews.Count-approvedTestChangeReviews} System, HLR, or LLR test change request(s) still require approval.",
                "Complete every procedure decision, submit each discipline review, and record test-lead approval."),
            baselineMaterialized
                ? new("traceability","Trace network complete",members.Count > 0 && tracedDerivedIds.Count == derivedIds.Count,tracedDerivedIds.Count,derivedIds.Count,
                    members.Count == 0
                        ? "The materialized baseline contains no effective requirement revisions, so traceability cannot pass."
                        : "Every derived HLR/LLR must retain an exact parent link.",
                    members.Count == 0
                        ? "Inspect the selected changes and materialized manifest; a releasable baseline must contain an effective requirement population."
                        : "Resolve orphan and suspect trace links.")
                : WaitingForMaterializedBaseline("traceability", "Trace network complete"),
            baselineMaterialized
                ? new("coverage","Requirement coverage complete",members.Count > 0 && coveredIds.Count == members.Count,coveredIds.Count,members.Count,
                    members.Count == 0
                        ? "The materialized baseline contains no effective requirement revisions, so coverage cannot pass."
                        : $"{members.Count-coveredIds.Count} effective requirement revisions have no settled coverage. A link counts only when it is not suspect, names an approved procedure revision, and that procedure has no revision still in draft or review.",
                    members.Count == 0
                        ? "Inspect the selected changes and materialized manifest; a releasable baseline must contain an effective requirement population."
                        : "Approve every procedure being changed, then confirm the coverage each changed requirement needs.")
                : WaitingForMaterializedBaseline("coverage", "Requirement coverage complete"),
            baselineMaterialized
                ? new("code_traceability", "Code traceability complete", mappedCode == requiredCode.Count, mappedCode, requiredCode.Count,
                    requiredCode.Count == 0
                        ? "No LLR revision changed in this build, so no implementation mapping is owed."
                        : $"{requiredCode.Count-mappedCode} exact LLR revision(s) lack a GitLab merge mapping or an attributable no-code decision.",
                    "Record immutable GitLab merge evidence or a justified no-code decision for every required exact LLR revision.")
                : WaitingForMaterializedBaseline("code_traceability", "Code traceability complete"),
            // The gate codes stay as they were. They are what the decision room looks its blockers up by, and
            // a build is rarely worth its whole suite whichever way the set of procedures was arrived at.
            baselineMaterialized
                ? new("verification","Selected test set has results",
                    selectedRevisionIds.Count == 0 ? nothingToTest : selectedPassed == selectedRevisionIds.Count,
                    selectedPassed,selectedRevisionIds.Count,
                    selectedRevisionIds.Count == 0
                        ? (nothingToTest
                            ? "This build changed nothing that needs testing, so no procedures were selected."
                            : "No procedures have been selected for this build yet.")
                        : $"{selectedRevisionIds.Count-selectedPassed} procedure(s) in the selected test set lack a latest Pass.",
                    selectedRevisionIds.Count == 0
                        ? "Choose the procedures this build must run — those covering what changed, and any area worth re-exercising."
                        : "Record a determination for every procedure in the set. Testing beyond it continues after release.")
                : WaitingForMaterializedBaseline("verification", "Selected test set has results"),
            baselineMaterialized
                ? new("evidence","Selected test set results carry evidence",
                    selectedRevisionIds.Count == 0 ? nothingToTest : selectedEvidenced == selectedRevisionIds.Count,
                    selectedEvidenced,selectedRevisionIds.Count,
                    selectedRevisionIds.Count == 0
                        ? (nothingToTest
                            ? "This build changed nothing that needs testing, so no evidence is owed."
                            : "No procedures have been selected for this build yet.")
                        : $"{selectedRevisionIds.Count-selectedEvidenced} result(s) in the selected test set lack checksummed evidence.",
                    "Attach the evidence package for every result in the selected test set.")
                : WaitingForMaterializedBaseline("evidence", "Selected test set results carry evidence"),
            new("problem_reports","Problem-report blockers resolved",problemBlockers.Count==0,0,problemBlockers.Count,problemBlockers.Count==0?"No unwaived controlled problem reports block this release.":$"{problemBlockers.Count} unwaived problem report blocker(s) remain: {string.Join(", ",problemBlockers.Take(3).Select(x=>x.DisplayNumber))}.","Resolve, formally disposition, or record an attributable waiver for every release-blocking problem report."),
            new("documents","Controlled outputs generated",docs.Select(x=>x.Type).Distinct().Count()>=6,docs.Select(x=>x.Type).Distinct().Count(),6,"The release package requires six controlled document types.","Generate SYSRD, both SWRDs, and three test-procedure documents."),
            new("release_approval","Release approval complete",campaign.Approvals.Count>0 && campaign.Approvals.All(x=>x.State==ReleaseApprovalState.Approved),campaign.Approvals.Count(x=>x.State==ReleaseApprovalState.Approved),campaign.Approvals.Count==0?3:campaign.Approvals.Count,"Ordered release approval must be unanimous.","Start release review and collect every approval.")
        };
        var percent = (int)Math.Round(gates.Average(x => x.Total == 0 ? (x.Complete ? 100 : 0) : Math.Min(100, x.Completed * 100d / x.Total)));
        return new(percent, gates.All(x => x.Complete), gates);
    }

    private static ReadinessGate WaitingForMaterializedBaseline(string code, string name) =>
        new(code, name, false, 0, 0,
            "Waiting for a materialized baseline. The exact requirement-revision population does not exist yet, so this gate has not been evaluated.",
            "Complete the Requirement baseline materialized gate first: freeze the candidate baseline and materialize its requirements.",
            "WaitingForPrerequisite", "baseline");
}
