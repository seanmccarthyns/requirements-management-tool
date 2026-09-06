using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// FMS showcase scenario qualification against a private copy of the once-seeded showcase template.
///
/// These tests grew inside <see cref="FmsShowcaseSeederTests"/>, where every one paid for a complete
/// deterministic FMS rebuild — identity seed, 150 system requirements, 400 HLR, 700 LLR, 105 change
/// requests, reviews, baselines, documents, procedures, executions, evidence — before reaching the
/// behaviour it actually proves. Back to back in one class, that serial chain was the Infrastructure
/// lane's critical path (#891: ~49 of ~50 CI minutes). Their subject is what happens after a valid seed
/// exists: upgrade authority and chronology, closure preservation, effectivity, concurrency, collision
/// handling and suspect coverage. None of that depends on the database having been built from nothing in
/// the same test, so each now rewinds, mutates and upgrades its own private copy of the shared template.
///
/// Isolation is unchanged: every test still owns a private writable SQLite file, still drives the real
/// seeder (<see cref="FmsShowcaseSeeder.EnsureSeededAsync"/>, <see cref="FmsShowcaseSeeder.CheckUpgradeAuthorityAsync"/>,
/// <see cref="FmsShowcaseSeeder.UpgradeAsync"/>) and domain materializers against it, and no test can see
/// another's mutations. What is no longer repeated is the building of a dataset that was identical every
/// time. Genuine fresh-seed qualification — exact dataset, identity ordering, idempotence, rollback —
/// remains in <see cref="FmsShowcaseSeederTests"/>, which deliberately does not use this template.
/// </summary>
[Collection(ShowcaseCollection.Name)]
public sealed class FmsShowcaseScenarioTests(ShowcaseDatabaseFixture showcase)
{
    private static async Task<Guid[]> OwnedScenarioIdsAsync(AeroLinkDbContext db, Guid programId, string prefix)
    {
        var details = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.StepKey.StartsWith(prefix))
            .OrderBy(x => x.StepKey).Select(x => x.Detail).ToListAsync();
        return details.Select(Guid.Parse).ToArray();
    }

    [Fact]
    public async Task Exact_empty_released_manifest_moves_only_corrective_evidence_to_the_active_build()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var released = await db.Releases.SingleAsync(x => x.ProjectId == summary.ProjectId && x.Version == "1.5");
        var active = await db.Releases.SingleAsync(x => x.ProjectId == summary.ProjectId && x.Version == "1.6");
        var releasedBaseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == released.Id);
        var activeBaseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == active.Id);

        // The persistent predecessor we are reproducing has already progressed 1.6 to an exact frozen
        // candidate. Drive this disposable fixture through the same domain materializers so the test does
        // not manufacture baseline membership or infer carried procedure truth from global revisions.
        // The Freeze guard validates the aggregate's loaded selections; hydrate them through the normal
        // EF mapping so the domain rule sees the seeded selections rather than a fresh-context artifact.
        var activeMaterializedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        activeBaseline = await db.CandidateBaselines
            .Include(x => x.Selections).Include(x => x.ExternalPackageSelections)
            .SingleAsync(x => x.Id == activeBaseline.Id);
        activeBaseline.Freeze("cm.fms", activeMaterializedAt.AddMinutes(-2));
        await db.SaveChangesAsync();
        var policyResolver = new EffectiveProjectLadderPolicyResolver(db);
        await new RequirementBaselineMaterializer(db,
                new VerificationImpactService(db, policyResolver: policyResolver),
                policyResolver: policyResolver)
            .MaterializeAsync(activeBaseline.Id, "cm.fms", activeMaterializedAt.AddMinutes(-1),
                CancellationToken.None);
        await new TestProcedureBaselineMaterializer(db, policyResolver: policyResolver)
            .MaterializeAsync(activeBaseline.Id, "cm.fms", activeMaterializedAt, CancellationToken.None);

        // Mirror the persistent installation's operator-time personnel reconciliation. Timeline
        // construction must remain attributable to that real grant without writing future events.
        var testEngineer = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
        var currentTestAuthority = await db.ProgramMemberships.SingleOrDefaultAsync(x => x.UserId == testEngineer.Id
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.TestEngineer && x.EndedAt == null);
        var currentGrant = DateTimeOffset.UtcNow.AddSeconds(-1);
        if (currentTestAuthority is not null)
        {
            currentTestAuthority.End("operator", currentGrant.AddTicks(-1));
            await db.SaveChangesAsync();
        }
        db.ProgramMemberships.Add(new ProgramMembership(testEngineer.Id, summary.ProgramId,
            ProgramRole.TestEngineer, "operator", currentGrant));
        await db.SaveChangesAsync();
        var activeEffectivity = await TestProcedureEffectivity.ForReleaseAsync(db, summary.ProjectId, active.Id,
            CancellationToken.None);
        Assert.NotNull(activeEffectivity);
        Assert.True(activeEffectivity.IsExactManifest);
        Assert.NotEmpty(activeEffectivity.RevisionByProcedure);

        var priorScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId,
            "scenario-richness/problem-report/");
        var priorClosureIds = priorScenarioIds.Skip(5).Take(2).ToArray();
        var priorRevisions = (await db.ProblemReportRevisions.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.SnapshotHash, x.SnapshotJson)).ToArray();
        var priorLinks = (await db.ProblemReportLinks.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.ArtifactId, x.Relationship, x.AddedAt)).ToArray();
        var priorCandidates = (await db.ProblemReportClosureCandidates.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.State, x.ClosurePackageHash, x.ClosurePackageJson)).ToArray();

        // Reproduce the real legacy installation: its released predecessor has an authoritative exact
        // empty manifest after the verification-model cutover, while 1.6 carries the stable Procedure
        // lineages. The old executions and reports remain immutable history.
        var releasedSelections = await db.BaselineTestProcedures
            .Where(x => x.BaselineId == releasedBaseline.Id).ToListAsync();
        Assert.NotEmpty(releasedSelections);
        db.BaselineTestProcedures.RemoveRange(releasedSelections);
        db.Entry(releasedBaseline).Property(x => x.TestProceduresHash).CurrentValue =
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        // Force the enrichment retry boundary. #889: the retry no longer recreates Interface scenarios —
        // the FMS ladder does not configure that level and the seed no longer contains them.
        var retrySteps = await db.ShowcaseUpgradeSteps.Where(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness").ToListAsync();
        db.ShowcaseUpgradeSteps.RemoveRange(retrySteps);
        await db.SaveChangesAsync();

        var releasedEffectivity = await TestProcedureEffectivity.ForReleaseAsync(db, summary.ProjectId,
            released.Id, CancellationToken.None);
        Assert.NotNull(releasedEffectivity);
        Assert.Empty(releasedEffectivity.RevisionByProcedure);
        var ready = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.True(ready.Ready, $"{ready.Code}: {ready.Detail}");

        var applied = await seeder.UpgradeAsync(summary.ProgramId);
        Assert.Contains(applied, x => x.StartsWith("scenario-richness:", StringComparison.Ordinal));
        var currentInterfaceIds = await OwnedScenarioIdsAsync(db, summary.ProgramId,
            "scenario-richness/interface/");
        Assert.Empty(currentInterfaceIds);
        Assert.Equal(CandidateBaselineState.Frozen, activeBaseline.State);
        var currentScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId,
            "scenario-richness/problem-report/");
        var currentClosureIds = currentScenarioIds.Skip(5).Take(2).ToArray();
        Assert.DoesNotContain(currentClosureIds, id => priorClosureIds.Contains(id));
        var currentReports = await db.ProblemReports.AsNoTracking()
            .Where(x => currentClosureIds.Contains(x.Id)).OrderBy(x => x.ReportNumber).ToListAsync();
        Assert.Equal(2, currentReports.Count);
        Assert.All(currentReports, report => Assert.Equal(active.Id, report.TargetReleaseId));

        foreach (var report in currentReports)
        {
            var originId = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => x.ProblemReportId == report.Id
                    && x.Relationship == ProblemReportRelationshipPolicy.OriginatingFailure)
                .Select(x => x.ArtifactId).SingleAsync();
            var origin = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == originId);
            Assert.Equal(released.Id, origin.ReleaseId);
            var selectedId = Assert.IsType<Guid>(report.ResolutionVerificationExecutionId);
            var selected = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == selectedId);
            Assert.Equal(active.Id, selected.ReleaseId);
            Assert.Null(selected.SoftwareBuildId);
            var predecessor = await db.TestExecutions.AsNoTracking()
                .SingleAsync(x => x.Id == selected.RetestOfExecutionId);
            Assert.Equal(origin.Id, predecessor.RetestOfExecutionId);
            var originProcedureId = await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.Id == origin.ProcedureRevisionId).Select(x => x.ProcedureId).SingleAsync();
            Assert.Equal(activeEffectivity.RevisionByProcedure[originProcedureId], selected.ProcedureRevisionId);
            Assert.True(selected.RecordedAt > activeBaseline.TestProceduresMaterializedAt);
            Assert.True(selected.RecordedAt <= DateTimeOffset.UtcNow);
            Assert.True(report.CreatedAt >= currentGrant);
            Assert.True(report.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.True(report.UpdatedAt <= DateTimeOffset.UtcNow);
            Assert.All(await db.ProblemReportRevisions.AsNoTracking()
                    .Where(x => x.ProblemReportId == report.Id).ToListAsync(),
                revision => Assert.True(revision.OccurredAt <= DateTimeOffset.UtcNow));
            var decision = await new ProblemReportClosureVerificationPolicy(db)
                .ValidateAsync(report, selected, CancellationToken.None);
            Assert.True(decision.Accepted, $"{decision.Code}: {decision.Error}");
        }
        Assert.Contains(currentReports, x => x.State == ProblemReportState.WaitingForSqaToClose);
        Assert.Contains(currentReports, x => x.State == ProblemReportState.Closed);
        var closed = currentReports.Single(x => x.State == ProblemReportState.Closed);
        var closedExecution = await db.TestExecutions.AsNoTracking()
            .SingleAsync(x => x.Id == closed.ResolutionVerificationExecutionId);
        Assert.True(closed.ClosureApprovedAt > closedExecution.RecordedAt);

        Assert.Equal(priorRevisions, (await db.ProblemReportRevisions.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.SnapshotHash, x.SnapshotJson)).ToArray());
        Assert.Equal(priorLinks, (await db.ProblemReportLinks.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.ArtifactId, x.Relationship, x.AddedAt)).ToArray());
        Assert.Equal(priorCandidates, (await db.ProblemReportClosureCandidates.AsNoTracking()
                .Where(x => priorClosureIds.Contains(x.ProblemReportId)).ToListAsync())
            .OrderBy(x => x.Id).Select(x => (x.Id, x.State, x.ClosurePackageHash, x.ClosurePackageJson)).ToArray());
        Assert.Empty(await seeder.UpgradeAsync(summary.ProgramId));

        // The deliberate cutover configuration above leaves the released baseline with an authoritative
        // exact-empty procedure manifest, so the #913 trace contract honestly reads unhealthy here:
        // nothing on the released build settles coverage any more, and the diagnostic says so instead of
        // silently falling back to unrestricted current coverage. Every other seed invariant holds.
        var invariants = await seeder.CheckInvariantsAsync(summary.ProgramId);
        Assert.All(invariants.Where(x => x.Key != "trace-gap-inventory"),
            invariant => Assert.True(invariant.Holds, $"{invariant.Key}: {invariant.Detail}"));
        var trace = invariants.Single(x => x.Key == "trace-gap-inventory");
        Assert.False(trace.Holds, trace.Detail);
        Assert.Contains("Scope: exact manifest, 0 procedure revision(s).", trace.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exact-empty released manifest plus unrelated current coverage: a later approved procedure revision
    /// linked to a released requirement must never settle released-build coverage. The invariant stays
    /// honestly unhealthy with an empty exact scope instead of reporting a false healthy state.
    /// </summary>
    [Fact]
    public async Task An_exact_empty_released_manifest_never_lets_later_current_coverage_settle_the_contract()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        var released = await db.Releases.SingleAsync(x => x.ProjectId == summary.ProjectId && x.Version == "1.5");
        var releasedBaseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == released.Id);

        db.BaselineTestProcedures.RemoveRange(await db.BaselineTestProcedures
            .Where(x => x.BaselineId == releasedBaseline.Id).ToListAsync());
        var laterProcedure = new TestProcedure(summary.ProjectId, "SYSTP-900001", "Later coverage",
            "test.author", DateTimeOffset.UtcNow, TestProcedureLevel.System);
        var laterRevision = new TestProcedureRevision(laterProcedure.Id, 0, "Later objective.", "Later preconditions.",
            "Later steps.", "Later expectation.", TestProcedureState.Approved, "test.author", DateTimeOffset.UtcNow,
            effectiveBaselineId: releasedBaseline.Id, parentKind: VerificationProcedureParentKind.Allocated);
        db.TestProcedures.Add(laterProcedure);
        db.TestProcedureRevisions.Add(laterRevision);
        var requirementRevisionId = await (from member in db.BaselineRequirements.AsNoTracking()
            where member.BaselineId == releasedBaseline.Id
            join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
            where artifact.BaseNumber == "SYSR-000040"
            select member.RevisionId).SingleAsync();
        db.TestCoverage.Add(new TestRequirementCoverage(laterRevision.Id, requirementRevisionId));
        await db.SaveChangesAsync();

        var trace = (await seeder.CheckInvariantsAsync(summary.ProgramId)).Single(x => x.Key == "trace-gap-inventory");
        Assert.False(trace.Holds, trace.Detail);
        Assert.Contains("Scope: exact manifest, 0 procedure revision(s).", trace.Detail, StringComparison.Ordinal);
        Assert.Contains("0 settled-covered", trace.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A genuinely pre-manifest released baseline takes the resolver's legacy compatibility selection as
    /// its authority: an approved procedure revision created after the release is excluded from the scope,
    /// so the named gap pair stays exactly the seeded pair and the invariant stays healthy — a later link
    /// must not leak in and settle a released requirement.
    /// </summary>
    [Fact]
    public async Task Legacy_effectivity_excludes_later_approved_revisions_from_the_trace_scope()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        var released = await db.Releases.SingleAsync(x => x.ProjectId == summary.ProjectId && x.Version == "1.5");
        var releasedBaseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == released.Id);

        db.Entry(releasedBaseline).Property(x => x.TestProceduresMaterializedAt).CurrentValue = null;
        // A genuinely pre-manifest release closed after its content was written, so the compatibility
        // window (approved revisions at or before the release) is populated; the seed's nominal dates do
        // not model that, so the fixture states it explicitly.
        db.Entry(released).Property(x => x.ReleasedAt).CurrentValue =
            new DateTimeOffset(2024, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var laterProcedure = new TestProcedure(summary.ProjectId, "SYSTP-900001", "Later coverage",
            "test.author", DateTimeOffset.UtcNow, TestProcedureLevel.System);
        var laterRevision = new TestProcedureRevision(laterProcedure.Id, 0, "Later objective.", "Later preconditions.",
            "Later steps.", "Later expectation.", TestProcedureState.Approved, "test.author", DateTimeOffset.UtcNow,
            effectiveBaselineId: releasedBaseline.Id, parentKind: VerificationProcedureParentKind.Allocated);
        db.TestProcedures.Add(laterProcedure);
        db.TestProcedureRevisions.Add(laterRevision);
        var requirementRevisionId = await (from member in db.BaselineRequirements.AsNoTracking()
            where member.BaselineId == releasedBaseline.Id
            join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
            where artifact.BaseNumber == "SYSR-000040"
            select member.RevisionId).SingleAsync();
        db.TestCoverage.Add(new TestRequirementCoverage(laterRevision.Id, requirementRevisionId));
        await db.SaveChangesAsync();

        var trace = (await seeder.CheckInvariantsAsync(summary.ProgramId)).Single(x => x.Key == "trace-gap-inventory");
        Assert.True(trace.Holds, trace.Detail);
        Assert.Contains("Scope: legacy compatibility manifest", trace.Detail, StringComparison.Ordinal);
        Assert.Contains("SYSR-000040.01 + SYSR-000115.01", trace.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ended_sqa_membership_is_preserved_and_reopened_closure_stays_in_work()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        var sqaId = await db.UserAccounts.Where(x => x.UserName == "quality.analyst").Select(x => x.Id).SingleAsync();
        var membership = await db.ProgramMemberships.SingleAsync(x => x.UserId == sqaId && x.ProgramId == summary.ProgramId
            && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null);
        var scenarioRowsBefore = await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == summary.ProgramId);
        var membershipRowsBefore = await db.ProgramMemberships.CountAsync(x => x.UserId == sqaId
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst);
        var account = await db.UserAccounts.SingleAsync(x => x.Id == sqaId);
        account.Disable(membership.GrantedAt.AddMinutes(1));
        await db.SaveChangesAsync();
        var disabledAuthority = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.False(disabledAuthority.Ready);
        Assert.Equal("quality_analyst_account_inactive", disabledAuthority.Code);
        account.Enable();
        await db.SaveChangesAsync();
        membership.End("admin", membership.GrantedAt.AddHours(1));
        var reportIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
        var report7Id = reportIds[6];
        var report7 = await db.ProblemReports.SingleAsync(x => x.Id == report7Id);
        report7.Reopen("quality.analyst", "Reopen the historical scenario to qualify ended-authority handling.", membership.GrantedAt.AddHours(2));
        await db.SaveChangesAsync();

        await seeder.EnsureSeededAsync();

        Assert.Equal(membershipRowsBefore, await db.ProgramMemberships.CountAsync(x => x.UserId == sqaId
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.SoftwareQualityAnalyst));
        Assert.Equal(scenarioRowsBefore, await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == summary.ProgramId));
        var endedAuthority = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.False(endedAuthority.Ready);
        Assert.Equal("quality_analyst_membership_inactive", endedAuthority.Code);
        Assert.False(await db.ProgramMemberships.AnyAsync(x => x.UserId == sqaId && x.ProgramId == summary.ProgramId
            && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null));
        report7 = await db.ProblemReports.AsNoTracking().SingleAsync(x => x.Id == report7Id);
        Assert.Equal(ProblemReportState.Verifying, report7.State);
        Assert.Null(report7.ResolutionVerificationExecutionId);
        var historicalCandidate = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(x => x.ProblemReportId == report7Id).OrderByDescending(x => x.Sequence).FirstAsync();
        Assert.Equal(ProblemReportClosureCandidateState.Approved, historicalCandidate.State);
        Assert.Equal(1, await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == report7Id
            && x.EventType == "ClosureApproved"));
    }

    [Fact]
    public async Task New_problem_report_scenarios_require_current_authority_for_every_controlled_actor()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var systemsAuthorId = await db.UserAccounts.Where(x => x.UserName == "systems.author").Select(x => x.Id).SingleAsync();
        var systemsAuthorMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == systemsAuthorId
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.Engineer && x.EndedAt == null);
        systemsAuthorMembership.End("admin", systemsAuthorMembership.GrantedAt.AddDays(1));
        var missing = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness/problem-report/01");
        var priorReportId = Guid.Parse(missing.Detail);
        db.ShowcaseUpgradeSteps.Remove(missing);
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness"));
        await db.SaveChangesAsync();
        var reportCount = await db.ProblemReports.CountAsync(x => x.ProjectId == summary.ProjectId);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
        Assert.Contains("systems.author", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(reportCount, await db.ProblemReports.CountAsync(x => x.ProjectId == summary.ProjectId));
        Assert.False(await db.ShowcaseUpgradeSteps.AnyAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness/problem-report/01"));

        var lateGrant = DateTimeOffset.UtcNow.AddSeconds(-1);
        db.ProgramMemberships.Add(new ProgramMembership(systemsAuthorId, summary.ProgramId,
            ProgramRole.Engineer, "operator", lateGrant));
        await db.SaveChangesAsync();
        var applied = await seeder.UpgradeAsync(summary.ProgramId);
        Assert.Contains(applied, x => x.StartsWith("scenario-richness:", StringComparison.Ordinal));
        var replacementReportId = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/01")
            .Select(x => x.Detail).SingleAsync());
        Assert.NotEqual(priorReportId, replacementReportId);
        var replacement = await db.ProblemReports.AsNoTracking()
            .SingleAsync(x => x.Id == replacementReportId);
        Assert.True(replacement.CreatedAt >= lateGrant);
        Assert.True(replacement.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.True(replacement.UpdatedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Late_actor_grant_covers_every_new_failed_execution_problem_report_revision()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        var scenario7Id = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/07")
            .Select(x => x.Detail).SingleAsync());
        var scenario7VerificationId = await db.ProblemReports.AsNoTracking()
            .Where(x => x.Id == scenario7Id).Select(x => x.ResolutionVerificationExecutionId).SingleAsync();

        // Remove only the durable ownership pointer for scenario 06. The old controlled report remains
        // immutable history; the explicit upgrade must create a new owned scenario and attribute every
        // new action on the real authority timeline rather than the 2024 execution that motivated it.
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness/problem-report/06"));
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness"));

        var testEngineer = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
        var priorMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == testEngineer.Id
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.TestEngineer && x.EndedAt == null);
        var endedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        priorMembership.End("operator", endedAt);
        var lateGrant = endedAt.AddSeconds(1);
        db.ProgramMemberships.Add(new ProgramMembership(testEngineer.Id, summary.ProgramId,
            ProgramRole.TestEngineer, "operator", lateGrant));
        await db.SaveChangesAsync();

        var ready = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.True(ready.Ready, ready.Detail);
        await seeder.UpgradeAsync(summary.ProgramId);

        var newReportId = Guid.Parse(await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/06")
            .Select(x => x.Detail).SingleAsync());
        var revisions = (await db.ProblemReportRevisions.AsNoTracking()
            .Where(x => x.ProblemReportId == newReportId).ToListAsync()).OrderBy(x => x.OccurredAt).ToList();
        Assert.NotEmpty(revisions);
        Assert.All(revisions, revision => Assert.True(revision.OccurredAt >= lateGrant,
            $"{revision.EventType} by {revision.Actor} occurred at {revision.OccurredAt:O} before {lateGrant:O}."));
        Assert.All(revisions, revision => Assert.True(revision.OccurredAt <= DateTimeOffset.UtcNow,
            $"{revision.EventType} by {revision.Actor} is future-dated at {revision.OccurredAt:O}."));

        var requiredRoles = new Dictionary<string, ProgramRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["test.engineer"] = ProgramRole.TestEngineer,
            ["project.lead"] = ProgramRole.ProjectEngineer,
            ["quality.analyst"] = ProgramRole.SoftwareQualityAnalyst,
        };
        var accounts = await db.UserAccounts.AsNoTracking()
            .Where(x => requiredRoles.Keys.Contains(x.UserName)).ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase);
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && accounts.Values.Select(a => a.Id).Contains(x.UserId))
            .ToListAsync();
        foreach (var revision in revisions)
        {
            var account = accounts[revision.Actor];
            var role = requiredRoles[revision.Actor];
            Assert.Contains(memberships, membership => membership.UserId == account.Id && membership.Role == role
                && membership.GrantedAt <= revision.OccurredAt
                && (membership.EndedAt is null || membership.EndedAt.Value > revision.OccurredAt));
        }

        var report = await db.ProblemReports.AsNoTracking().SingleAsync(x => x.Id == newReportId);
        var verificationId = Assert.IsType<Guid>(report.ResolutionVerificationExecutionId);
        var verification = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == verificationId);
        Assert.Equal(TestOutcome.Pass, verification.Outcome);
        Assert.True(verification.RecordedAt >= lateGrant);
        Assert.True(verification.RecordedAt <= DateTimeOffset.UtcNow);
        var predecessorId = Assert.IsType<Guid>(verification.RetestOfExecutionId);
        var predecessor = await db.TestExecutions.AsNoTracking().SingleAsync(x => x.Id == predecessorId);
        Assert.Equal(TestOutcome.Pass, predecessor.Outcome);
        Assert.NotNull(predecessor.RetestOfExecutionId);
        Assert.True(predecessor.RecordedAt < lateGrant);
        Assert.Equal(predecessor.ProjectId, verification.ProjectId);
        Assert.Equal(predecessor.ReleaseId, verification.ReleaseId);
        Assert.Equal(predecessor.SoftwareBuildId, verification.SoftwareBuildId);
        Assert.Equal(predecessor.ProcedureRevisionId, verification.ProcedureRevisionId);
        var policyDecision = await new ProblemReportClosureVerificationPolicy(db)
            .ValidateAsync(report, verification, CancellationToken.None);
        Assert.True(policyDecision.Accepted, $"{policyDecision.Code} {policyDecision.Error}");
        Assert.Equal("test.engineer", verification.ExecutedBy);
        Assert.Single(await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness/problem-report-verification/06").ToListAsync());

        var executionCount = await db.TestExecutions.CountAsync();
        Assert.Empty(await seeder.UpgradeAsync(summary.ProgramId));
        Assert.Equal(executionCount, await db.TestExecutions.CountAsync());
        Assert.Equal(scenario7VerificationId, await db.ProblemReports.AsNoTracking()
            .Where(x => x.Id == scenario7Id).Select(x => x.ResolutionVerificationExecutionId).SingleAsync());
    }

    [Fact]
    public async Task Mapped_incomplete_problem_report_evidence_preflights_test_engineer_and_preserves_late_grant_chronology()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        // Keep the durable scenario mapping and its controlled artifact, but invalidate the current
        // closure candidate. This leaves a mapped Problem Report with incomplete controlled evidence
        // while retaining the existing valid verification successor. It is the upgrade shape that used
        // to bypass the actor preflight because the mapping itself was present.
        var reportIdText = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == summary.ProgramId && x.StepKey == "scenario-richness/problem-report/06")
            .Select(x => x.Detail).SingleAsync();
        var reportId = Guid.Parse(reportIdText);
        var candidateBefore = await db.ProblemReportClosureCandidates.SingleAsync(x => x.ProblemReportId == reportId
            && x.State == ProblemReportClosureCandidateState.Pending);
        candidateBefore.Invalidate("test.engineer", "Force a retryable incomplete-evidence scenario for qualification.", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var scenarioStep = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness");

        var testEngineer = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
        var testMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == testEngineer.Id
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.TestEngineer && x.EndedAt == null);
        var revisionCountBeforeRefusal = await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId);

        testEngineer.Disable(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var disabled = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.False(disabled.Ready);
        Assert.Equal("showcase_actor_authority_unavailable", disabled.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
        Assert.Equal(revisionCountBeforeRefusal,
            await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId));
        Assert.True(await db.ShowcaseUpgradeSteps.AnyAsync(x => x.Id == scenarioStep.Id));

        testEngineer.Enable();
        var endedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        testMembership.End("operator", endedAt);
        await db.SaveChangesAsync();
        var ended = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.False(ended.Ready);
        Assert.Equal("showcase_actor_authority_unavailable", ended.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
        Assert.Equal(revisionCountBeforeRefusal,
            await db.ProblemReportRevisions.CountAsync(x => x.ProblemReportId == reportId));

        // An explicit operator grant makes the actor current again. The new evidence must follow that
        // actual grant, even though the mapped test execution and Problem Report were authored in 2024.
        var lateGrant = endedAt.AddSeconds(1);
        db.ProgramMemberships.Add(new ProgramMembership(testEngineer.Id, summary.ProgramId,
            ProgramRole.TestEngineer, "operator", lateGrant));
        await db.SaveChangesAsync();
        var ready = await seeder.CheckUpgradeAuthorityAsync(summary.ProgramId);
        Assert.True(ready.Ready, ready.Detail);
        await seeder.UpgradeAsync(summary.ProgramId);

        var candidate = await db.ProblemReportClosureCandidates.AsNoTracking()
            .Where(x => x.ProblemReportId == reportId).OrderByDescending(x => x.Sequence).FirstAsync();
        Assert.Equal(ProblemReportClosureCandidateState.Pending, candidate.State);
        Assert.True(candidate.SelectedAt >= lateGrant,
            $"Closure candidate was selected at {candidate.SelectedAt:O} before the grant at {lateGrant:O}.");
        Assert.True(candidate.SelectedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Concurrent_explicit_upgrades_are_atomic_and_preserve_a_deliberate_leadership_vacancy()
    {
        using var database = showcase.Create();
        var options = database.Options;
        var summary = showcase.Summary;
        Guid programId = summary.ProgramId;
        int priorReportCount;
        Guid endedAssignmentId;
        await using (var initial = new AeroLinkDbContext(options))
        {
            priorReportCount = await initial.ProblemReports.CountAsync(x => x.ProjectId == summary.ProjectId);

            var ended = await initial.ProjectLeadershipAssignments.SingleAsync(x => x.ProgramId == programId
                && x.Position == ProjectLeadershipPosition.SoftwareTestLead && x.EndedAt == null);
            endedAssignmentId = ended.Id;
            ended.End("operator", DateTimeOffset.UtcNow);
            initial.ShowcaseUpgradeSteps.Remove(await initial.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == programId
                && x.StepKey == "leadership-roster"));
            initial.ShowcaseUpgradeSteps.Remove(await initial.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == programId
                && x.StepKey == "scenario-richness/problem-report/01"));
            initial.ShowcaseUpgradeSteps.Remove(await initial.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == programId
                && x.StepKey == "scenario-richness"));
            await initial.SaveChangesAsync();
        }

        await using var firstDb = new AeroLinkDbContext(options);
        await using var secondDb = new AeroLinkDbContext(options);
        var results = await Task.WhenAll(
            new FmsShowcaseSeeder(firstDb).UpgradeAsync(programId),
            new FmsShowcaseSeeder(secondDb).UpgradeAsync(programId));
        Assert.Single(results, result => result.Count > 0);
        Assert.Single(results, result => result.Count == 0);

        await using var verify = new AeroLinkDbContext(options);
        Assert.Equal(8, await verify.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == programId
            && x.StepKey.StartsWith("scenario-richness/problem-report/")));
        Assert.Equal(priorReportCount + 1, await verify.ProblemReports.CountAsync());
        Assert.Single(await verify.ShowcaseUpgradeSteps.Where(x => x.ProgramId == programId
            && x.StepKey == "leadership-roster").ToListAsync());
        var preserved = await verify.ProjectLeadershipAssignments.Where(x => x.ProgramId == programId
            && x.Position == ProjectLeadershipPosition.SoftwareTestLead).ToListAsync();
        var only = Assert.Single(preserved);
        Assert.Equal(endedAssignmentId, only.Id);
        Assert.NotNull(only.EndedAt);
        Assert.False(await verify.ProjectLeadershipAssignments.AnyAsync(x => x.ProgramId == programId
            && x.Position == ProjectLeadershipPosition.SoftwareTestLead && x.EndedAt == null));
    }

    [Fact]
    public async Task Existing_collidable_low_numbers_are_not_mistaken_for_owned_showcase_scenarios()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;
        // Operator-owned content at the levels and numbers the seed used to claim: an Interface change
        // request carrying scenario marker prose, and a Problem Report on a colliding low number. Ownership
        // is durable-step based, so neither is ever adopted, mutated or retired by the showcase (#889).
        var foreignInterface = new SystemChangeRequest("ICDCR-00001", 0, summary.ProjectId, summary.ActiveReleaseId,
            "Operator-owned interface change", "Existing controlled content.", "Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]",
            "Existing controlled disposition.", "engineer.demo", DateTimeOffset.UtcNow, ChangeRequestType.Interface);
        var foreignReport = new ProblemReport(summary.ProjectId, "PR-00001", "Operator-owned problem report",
            "Existing controlled problem.", "Existing controlled analysis.", "engineer.demo", DateTimeOffset.UtcNow,
            targetReleaseId: summary.ActiveReleaseId, responsibleEngineerId: "engineer.demo",
            additionalInformation: "Operator-owned content. [FMSLIVE showcase scenario: problem-report-01]",
            category: ProblemReportCategory.CodeFunctional);
        db.SystemChangeRequests.Add(foreignInterface); db.ProblemReports.Add(foreignReport); await db.SaveChangesAsync();

        // Force the enrichment retry boundary while leaving the foreign rows' copied display breadcrumbs
        // in place. Scenario ownership is durable-step based and the preferred high range is collision-safe,
        // so a user-authored marker cannot be selected for the missing owned scenario or receive its links.
        var missingReportMapping = await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness/problem-report/01");
        db.ShowcaseUpgradeSteps.Remove(missingReportMapping);
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "scenario-richness"));
        await db.SaveChangesAsync();
        await seeder.UpgradeAsync(summary.ProgramId);
        var interfaceScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/interface/");
        var reportScenarioIds = await OwnedScenarioIdsAsync(db, summary.ProgramId, "scenario-richness/problem-report/");
        var ownedReports = await db.ProblemReports.AsNoTracking().Where(x => reportScenarioIds.Contains(x.Id)).ToListAsync();
        Assert.Empty(interfaceScenarioIds);
        Assert.Equal(8, ownedReports.Count);
        Assert.DoesNotContain(ownedReports, x => x.Id == foreignReport.Id);
        Assert.Contains(await db.ProblemReports.AsNoTracking().Select(x => x.ReportNumber).ToListAsync(), x => x == "PR-00001");
        Assert.Equal("Operator-owned content. [FMSLIVE showcase scenario: problem-report-01]", foreignReport.AdditionalInformation);
        Assert.Empty(await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == foreignReport.Id).ToListAsync());
        Assert.All(ownedReports, x => Assert.StartsWith("PR-866", x.ReportNumber));
        // The operator-owned Interface record survives every upgrade path untouched, marker prose or not.
        var foreignInterfaceAfter = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == foreignInterface.Id);
        Assert.Equal("Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]", foreignInterfaceAfter.Analysis);
        Assert.Equal("ICDCR-00001", foreignInterfaceAfter.BaseNumber);
    }

    /// <summary>
    /// Rebuilds what the pre-#889 seeder wrote: eight Interface change requests at the active build, keyed
    /// by their durable <c>scenario-richness/interface/</c> ownership rows, with scenario 04 approved into
    /// the draft active baseline. Uses exactly the aggregate lifecycle the old seeder used, so the
    /// retirement step is exercised against the real legacy shape, not a simplification of it.
    /// </summary>
    private static async Task<List<SystemChangeRequest>> SeedLegacyInterfaceScenariosAsync(AeroLinkDbContext db,
        Guid programId, Guid projectId, Guid activeReleaseId)
    {
        var deterministicAt = new DateTimeOffset(2024, 12, 2, 10, 0, 0, TimeSpan.Zero);
        var baseline = await db.CandidateBaselines.Include(x => x.Selections)
            .SingleAsync(x => x.ProjectId == projectId && x.ReleaseId == activeReleaseId && x.BaseNumber == "SW-01.60");
        var requests = new List<SystemChangeRequest>();
        for (var i = 1; i <= 8; i++)
        {
            var author = i % 2 == 1 ? "systems.author" : "software.author";
            var at = deterministicAt.AddDays(i);
            var request = new SystemChangeRequest($"ICDCR-8660{i}", 0, projectId, activeReleaseId,
                i == 1 ? "Align navigation interface timing contract" : $"FMS 1.6 interface contract scenario {i}",
                "The controlled interface contract needs a documented FMS 1.6 decision.",
                $"The interface impact was reviewed against the current navigation and display boundaries. [FMSLIVE showcase scenario: interface-{i:D2}]",
                "Record the exact interface behaviour and its compatibility decision.", author, at,
                ChangeRequestType.Interface);
            request.AddRequirementChange(author, $"ICDR-8660{i}", 0, RequirementLevel.Interface,
                RequirementChangeKind.Introduce,
                $"The FMS interface shall preserve deterministic navigation exchange behaviour {i:D2}.",
                "The interface requirement is retained as controlled showcase content.", "Not Applicable", at);
            switch (i)
            {
                case 2 or 7:
                    request.SubmitForReview(author, i == 2
                        ? [new ApproverSelection("assurance.reviewer", "Development Assurance Reviewer")]
                        : [new ApproverSelection("lead.reviewer", "Maya Patel"), new ApproverSelection("manager.reviewer", "Olivia Chen")],
                        at.AddHours(1));
                    break;
                case 3:
                    request.SubmitForReview(author, [new ApproverSelection("assurance.reviewer", "Development Assurance Reviewer")], at.AddHours(1));
                    request.ApproveActiveStage("assurance.reviewer", at.AddHours(2));
                    break;
                case 4:
                    request.SubmitForReview(author, [new ApproverSelection("lead.reviewer", "Maya Patel")], at.AddHours(1));
                    request.ApproveActiveStage("lead.reviewer", at.AddHours(2));
                    break;
                case 5:
                    request.Defer(author, "Deferred pending the next interface supplier coordination window.", at.AddHours(1));
                    break;
                case 6:
                    request.Withdraw(author, "Withdrawn after the interface contract was consolidated into another package.", at.AddHours(1));
                    break;
            }
            db.SystemChangeRequests.Add(request);
            db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, $"scenario-richness/interface/{i:D2}",
                request.Id.ToString("D"), request.CreatedAt));
            requests.Add(request);
        }
        await db.SaveChangesAsync();
        baseline.Select(requests[3], "cm.fms", deterministicAt.AddDays(4).AddHours(3));
        await db.SaveChangesAsync();
        return requests;
    }

    [Fact]
    public async Task Upgrade_closes_out_owned_interface_scenarios_without_deleting_any_record()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var legacy = await SeedLegacyInterfaceScenariosAsync(db, summary.ProgramId, summary.ProjectId, summary.ActiveReleaseId);
        // Operator-owned Interface content the seed never owned: it carries scenario marker prose, but no
        // durable ownership row names it, so the retirement must not touch it.
        var foreign = new SystemChangeRequest("ICDCR-00001", 0, summary.ProjectId, summary.ActiveReleaseId,
            "Operator-owned interface change", "Existing controlled content.",
            "Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]",
            "Existing controlled disposition.", "engineer.demo", DateTimeOffset.UtcNow, ChangeRequestType.Interface);
        db.SystemChangeRequests.Add(foreign);
        // A database upgraded by the pre-#889 code carries the Interface ownership rows but has never
        // recorded the retirement step, so its marker must go too for the legacy shape to be honest.
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));
        await db.SaveChangesAsync();
        // A reviewer on scenario 02's open cycle wrote a draft observation and never decided. Cancellation
        // must publish it (carrying no invented decision), or the author loses the analysis outright.
        var inReviewScenario = legacy.Single(x => x.BaseNumber == "ICDCR-86602");
        var openCycle = inReviewScenario.ReviewCycles.Single(x => x.State == ReviewCycleState.Active);
        // The comment carries an application-assigned GUID, so change detection on the tracked cycle reads
        // it as an existing row (the same hazard the product's own comment endpoint answers by adding the
        // comment to the set explicitly before saving).
        var comment = openCycle.AddComment("assurance.reviewer", ReviewCommentAnchor.ChangeCase, null,
            "The timing contract wording needs a compatibility note before this proceeds.", DateTimeOffset.UtcNow);
        db.ReviewComments.Add(comment);
        await db.SaveChangesAsync();
        // Prove the upgrade against a real fresh context, not fixture-tracked children.
        db.ChangeTracker.Clear();
        var legacyIds = legacy.Select(x => x.Id).ToList();
        var legacyRequirementChangeCount = await db.RequirementChanges.AsNoTracking()
            .CountAsync(x => legacyIds.Contains(x.ChangeRequestId));
        var legacyAuditEventCount = await db.AuditEvents.AsNoTracking()
            .CountAsync(x => legacyIds.Contains(x.AggregateId));
        var activeBaseline = await db.CandidateBaselines.AsNoTracking()
            .SingleAsync(x => x.ProjectId == summary.ProjectId && x.ReleaseId == summary.ActiveReleaseId);
        var originalSelectionCount = await db.BaselineSelections.AsNoTracking()
            .CountAsync(x => x.BaselineId == activeBaseline.Id && !legacyIds.Contains(x.ChangeRequestId));

        var applied = await seeder.UpgradeAsync(summary.ProgramId);
        Assert.Contains(applied, x => x.StartsWith("interface-scenario-retirement: Closed out 7 ", StringComparison.Ordinal));

        // Nothing was deleted: every scenario record, its requirement change, its review and audit
        // evidence, and its durable ownership row all remain.
        Assert.Equal(legacy.Count, await db.SystemChangeRequests.AsNoTracking().CountAsync(x => legacyIds.Contains(x.Id)));
        Assert.Equal(legacyRequirementChangeCount, await db.RequirementChanges.AsNoTracking().CountAsync(x => legacyIds.Contains(x.ChangeRequestId)));
        Assert.Equal(8, await db.ShowcaseUpgradeSteps.AsNoTracking().CountAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey.StartsWith("scenario-richness/interface/")));
        Assert.True(await db.ShowcaseUpgradeSteps.AsNoTracking().AnyAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));
        // Every seeded scenario is closed: withdrawn under its own author's identity, with the seven open
        // or approved ones newly withdrawn and the already-withdrawn one untouched.
        var closedStates = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => legacyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.State);
        Assert.All(closedStates.Values, state => Assert.Equal(ChangeRequestState.Withdrawn, state));
        Assert.Contains(await db.AuditEvents.AsNoTracking().Where(x => legacyIds.Contains(x.AggregateId))
            .Select(x => x.EventType).ToListAsync(), x => x == "ChangeRequestWithdrawn");
        Assert.True(await db.AuditEvents.AsNoTracking().CountAsync(x => legacyIds.Contains(x.AggregateId)) > legacyAuditEventCount);
        // The cancelled cycle published the stranded reviewer observation — carrying no invented decision —
        // and the approval recorded before the withdrawal is preserved untouched.
        var cancelledCycle = await db.ReviewCycles.AsNoTracking()
            .SingleAsync(x => x.ChangeRequestId == inReviewScenario.Id && x.State == ReviewCycleState.Cancelled);
        var publishedComment = await db.ReviewComments.AsNoTracking()
            .SingleAsync(x => x.ReviewCycleId == cancelledCycle.Id);
        Assert.Equal(ReviewCommentState.Published, publishedComment.State);
        Assert.False(publishedComment.DecisionRecorded);
        var approvedScenarioId = legacy.Single(x => x.BaseNumber == "ICDCR-86603").Id;
        Assert.True(await db.ApprovalSteps.AsNoTracking().AnyAsync(x => x.ReviewCycleId ==
            db.ReviewCycles.Where(c => c.ChangeRequestId == approvedScenarioId).Select(c => c.Id).Single()
            && x.State == ApprovalStepState.Approved));
        // The draft active baseline no longer carries the unconfigured selection, and its own original
        // selections are untouched.
        Assert.Equal(CandidateBaselineState.Draft, (await db.CandidateBaselines.AsNoTracking()
            .SingleAsync(x => x.Id == activeBaseline.Id)).State);
        Assert.Equal(originalSelectionCount, await db.BaselineSelections.AsNoTracking()
            .CountAsync(x => x.BaselineId == activeBaseline.Id && !legacyIds.Contains(x.ChangeRequestId)));
        Assert.DoesNotContain(await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == activeBaseline.Id)
            .Select(x => x.ChangeRequestDisplayNumber).ToListAsync(), x => x!.StartsWith("ICDCR"));
        // Operator content survives untouched.
        var foreignAfter = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == foreign.Id);
        Assert.Equal("Existing controlled analysis. [FMSLIVE showcase scenario: interface-01]", foreignAfter.Analysis);
        // The showcase postconditions hold with the closed-out history in place, and a second upgrade has
        // nothing left to close.
        Assert.All(await seeder.CheckInvariantsAsync(summary.ProgramId), x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
        Assert.DoesNotContain(await seeder.UpgradeAsync(summary.ProgramId),
            x => x.StartsWith("interface-scenario-retirement:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upgrade_refuses_to_reverse_a_draft_selection_without_current_cm_authority()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var legacy = await SeedLegacyInterfaceScenariosAsync(db, summary.ProgramId, summary.ProjectId, summary.ActiveReleaseId);
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));
        // A deliberate leadership vacancy plus an ended role membership: the roster preflight accepts the
        // vacancy, but the baseline removal is attributed to cm.fms, so its current ConfigurationManager
        // authority must exist before a controlled baseline event is written.
        var cmId = await db.UserAccounts.Where(x => x.UserName == "cm.fms").Select(x => x.Id).SingleAsync();
        var cmAssignment = await db.ProjectLeadershipAssignments.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.Position == ProjectLeadershipPosition.ConfigurationManager && x.EndedAt == null);
        cmAssignment.End("operator", DateTimeOffset.UtcNow.AddSeconds(-2));
        var cmMembership = await db.ProgramMemberships.SingleAsync(x => x.UserId == cmId
            && x.ProgramId == summary.ProgramId && x.Role == ProgramRole.ConfigurationManager && x.EndedAt == null);
        cmMembership.End("operator", cmMembership.GrantedAt.AddDays(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var legacyIds = legacy.Select(x => x.Id).ToList();
        var selectedScenarioId = legacy.Single(x => x.State == ChangeRequestState.SelectedForBaseline).Id;
        var withdrawalEventCount = await db.AuditEvents.AsNoTracking().CountAsync(x => legacyIds.Contains(x.AggregateId)
            && x.EventType == "ChangeRequestWithdrawn");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.UpgradeAsync(summary.ProgramId));
        Assert.Contains("cm.fms", failure.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal happened before any mutation: no withdrawal, no selection reversal, no marker.
        Assert.Equal(ChangeRequestState.SelectedForBaseline, (await db.SystemChangeRequests.AsNoTracking()
            .SingleAsync(x => x.Id == selectedScenarioId)).State);
        Assert.True(await db.BaselineSelections.AsNoTracking().AnyAsync(x => x.ChangeRequestId == selectedScenarioId));
        Assert.Equal(withdrawalEventCount, await db.AuditEvents.AsNoTracking().CountAsync(x => legacyIds.Contains(x.AggregateId)
            && x.EventType == "ChangeRequestWithdrawn"));
        Assert.False(await db.ShowcaseUpgradeSteps.AsNoTracking().AnyAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));

        // Grant the authority and the normal path completes.
        var grant = DateTimeOffset.UtcNow.AddSeconds(-1);
        db.ProgramMemberships.Add(new ProgramMembership(cmId, summary.ProgramId,
            ProgramRole.ConfigurationManager, "operator", grant));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await seeder.UpgradeAsync(summary.ProgramId);
        Assert.Equal(ChangeRequestState.Withdrawn, (await db.SystemChangeRequests.AsNoTracking()
            .SingleAsync(x => x.Id == selectedScenarioId)).State);
        Assert.False(await db.BaselineSelections.AsNoTracking().AnyAsync(x => x.ChangeRequestId == selectedScenarioId));
        Assert.True(await db.ShowcaseUpgradeSteps.AsNoTracking().AnyAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));
    }

    [Fact]
    public async Task Upgrade_leaves_an_interface_selection_frozen_into_a_baseline_untouched()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var summary = showcase.Summary;

        var legacy = await SeedLegacyInterfaceScenariosAsync(db, summary.ProgramId, summary.ProjectId, summary.ActiveReleaseId);
        // Reproduce the persistent installation that progressed 1.6 to an exact frozen candidate while the
        // Interface scenario was still selected: that selection is baseline content now. The product
        // forbids withdrawing a selected request, so the record is left exactly as it stands — selected,
        // approved, and carried by the frozen baseline — while the other scenarios close out.
        var materializedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var activeBaseline = await db.CandidateBaselines
            .Include(x => x.Selections).Include(x => x.ExternalPackageSelections)
            .SingleAsync(x => x.ReleaseId == summary.ActiveReleaseId);
        activeBaseline.Freeze("cm.fms", materializedAt.AddMinutes(-2));
        await db.SaveChangesAsync();
        var policyResolver = new EffectiveProjectLadderPolicyResolver(db);
        await new RequirementBaselineMaterializer(db,
                new VerificationImpactService(db, policyResolver: policyResolver),
                policyResolver: policyResolver)
            .MaterializeAsync(activeBaseline.Id, "cm.fms", materializedAt.AddMinutes(-1), CancellationToken.None);
        // A database upgraded by the pre-#889 code has never recorded the retirement step.
        db.ShowcaseUpgradeSteps.Remove(await db.ShowcaseUpgradeSteps.SingleAsync(x => x.ProgramId == summary.ProgramId
            && x.StepKey == "interface-scenario-retirement"));
        await db.SaveChangesAsync();
        // Prove the upgrade against a real fresh context, not fixture-tracked children.
        db.ChangeTracker.Clear();

        var legacyIds = legacy.Select(x => x.Id).ToList();
        var selectedScenarioId = legacy.Single(x => x.State == ChangeRequestState.SelectedForBaseline).Id;
        await seeder.UpgradeAsync(summary.ProgramId);

        // The frozen selection and its materialized revision stand unchanged; the other scenarios closed.
        var selectedAfter = await db.SystemChangeRequests.AsNoTracking().SingleAsync(x => x.Id == selectedScenarioId);
        Assert.Equal(ChangeRequestState.SelectedForBaseline, selectedAfter.State);
        Assert.True(await db.BaselineSelections.AsNoTracking().AnyAsync(x => x.ChangeRequestId == selectedScenarioId));
        Assert.True(await db.RequirementRevisions.AsNoTracking().AnyAsync(x => x.SourceChangeRequestId == selectedScenarioId));
        var closed = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => legacyIds.Contains(x.Id) && x.Id != selectedScenarioId)
            .ToDictionaryAsync(x => x.Id, x => x.State);
        Assert.All(closed.Values, state => Assert.Equal(ChangeRequestState.Withdrawn, state));
        Assert.All(await seeder.CheckInvariantsAsync(summary.ProgramId), x => Assert.True(x.Holds, $"{x.Key}: {x.Detail}"));
    }

    /// <summary>
    /// The showcase covered all 1,250 of its requirements, so it could never demonstrate the product finding
    /// a verification gap — the question the tool exists to answer. One FMS 1.6 rework item now puts an
    /// approved System procedure back into revision, which is enough to make the coverage it provides stop
    /// counting without disturbing a single released FMS 1.5 record.
    /// </summary>
    [Fact]
    public async Task An_in_work_procedure_revision_creates_suspect_coverage_that_reseeding_does_not_multiply()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);
        var first = showcase.Summary;
        await seeder.EnsureSeededAsync();

        var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.BaseNumber == "SYSTP-000040");
        var revisions = await db.TestProcedureRevisions.AsNoTracking()
            .Where(x => x.ProcedureId == procedure.Id).OrderBy(x => x.Revision).ToListAsync();

        // Seeding twice must leave one in-work revision, not two.
        Assert.Equal([0, 1], revisions.Select(x => x.Revision).ToArray());
        Assert.Equal(TestProcedureState.Approved, revisions[0].State);
        Assert.Equal(TestProcedureState.Draft, revisions[1].State);

        // Released FMS 1.5 is untouched: every effective revision still carries its coverage link.
        Assert.Equal(1250, await db.TestCoverage.Select(x => x.RequirementRevisionId).Distinct().CountAsync());

        var effective = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == first.ReleasedBaselineId).Select(x => x.RevisionId).ToListAsync();
        var states = await VerificationCoverageProjection.StatesAsync(db, effective, default);
        var suspect = states.Where(x => x.Value == RequirementCoverageState.Suspect).Select(x => x.Key).OrderBy(x => x).ToArray();
        var carried = await db.TestCoverage.AsNoTracking()
            .Where(x => x.ProcedureRevisionId == revisions[0].Id).Select(x => x.RequirementRevisionId).ToListAsync();

        // Exactly the requirements that one procedure covers, and nothing else in the programme.
        Assert.Equal(carried.OrderBy(x => x).ToArray(), suspect);
        Assert.Equal(2, suspect.Length);

        // Uncovered is deliberately not seeded — see EnsureVerificationCoverageGapAsync for why.
        Assert.DoesNotContain(RequirementCoverageState.Uncovered, states.Values);
    }

    /// <summary>
    /// The trace-gap diagnostic pins the deliberate negatives into the seed contract: after a seed, the
    /// suspect set is exactly the named SYSTP-000040 1.6 rework pair and nothing reads Uncovered. It must
    /// also bite: the seeder's own gap mechanism (an in-work revision stopping a procedure's coverage from
    /// counting) applied outside the named scenario is drift, and the invariant names it.
    /// </summary>
    [Fact]
    public async Task Trace_gap_inventory_invariant_names_accidental_suspect_coverage_outside_the_named_scenario()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        var before = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var baseline = Assert.Single(before, x => x.Key == "trace-gap-inventory");
        Assert.True(baseline.Holds, baseline.Detail);
        Assert.Contains("SYSR-000040.01", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("SYSR-000115.01", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("SYSTP-000040", baseline.Detail, StringComparison.Ordinal);

        var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.BaseNumber == "SYSTP-000041");
        db.TestProcedureRevisions.Add(new TestProcedureRevision(procedure.Id, 1,
            "Verify the drifted FMS behavior group against revised 1.6 behavior.",
            "Load the FMS 1.6 candidate software.",
            "Stimulate the revised inputs and record each observable output.",
            "Every observed output meets the linked requirement acceptance criteria.",
            TestProcedureState.Draft, "test.author", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var drifted = Assert.Single(after, x => x.Key == "trace-gap-inventory");
        Assert.False(drifted.Holds, drifted.Detail);
        Assert.Contains("Accidental suspect coverage outside the named SYSTP-000040 scenario",
            drifted.Detail, StringComparison.Ordinal);
    }
    /// The family inventory reports the whole cross-family matrix — including the deliberately singular
    /// and retired families, with the reason in the detail — and holds the seed to the repeated-family
    /// minimum. It must also bite: removing the retest leg breaks the pass/fail/retest chain and the
    /// invariant names it instead of silently staying green.
    /// </summary>
    [Fact]
    public async Task Family_inventory_invariant_reports_the_matrix_and_names_a_broken_pass_fail_retest_chain()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        var before = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var baseline = Assert.Single(before, x => x.Key == "family-inventory");
        Assert.True(baseline.Holds, baseline.Detail);
        Assert.Contains("SYSR 150", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("HLR 400", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("LLR 700", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("Interface change control is retired (#889)", baseline.Detail, StringComparison.Ordinal);
        // Every configured family from the resolved ladder profile is enumerated with its own count —
        // zeros included — so a family cannot vanish from the report by losing all of its rows.
        Assert.Contains("System/Procedure 75", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("HighLevel/Case 160", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("LowLevel/Case 280", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("executions per executable family: System/Procedure", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("HighLevel/Case", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("LowLevel/Case", baseline.Detail, StringComparison.Ordinal);
        Assert.Contains("enforced verification families are exactly the resolved ladder profile's configured bindings",
            baseline.Detail, StringComparison.Ordinal);

        var retests = await db.TestExecutions.Where(x => x.RetestOfExecutionId != null).ToListAsync();
        Assert.NotEmpty(retests);
        db.TestExecutions.RemoveRange(retests);
        await db.SaveChangesAsync();

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var drifted = Assert.Single(after, x => x.Key == "family-inventory");
        Assert.False(drifted.Holds, drifted.Detail);
        Assert.Contains("no failed execution with a passing same-artifact retest successor",
            drifted.Detail, StringComparison.Ordinal);
    }
    /// HLRCR and LLRCR are separate controlled families, so reclassifying almost every LLRCR into the
    /// HighLevel family — leaving the Software aggregate far above the minimum — must make the
    /// inventory fail while naming the deficient LowLevel family explicitly.
    /// </summary>
    [Fact]
    public async Task Family_inventory_names_a_deficient_change_request_family_below_the_aggregate()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        var lowLevelRequests = await db.SystemChangeRequests
            .Where(x => x.ProjectId == showcase.Summary.ProjectId
                && x.Type == ChangeRequestType.Software
                && x.SoftwareLevel == RequirementLevel.LowLevel).ToListAsync();
        Assert.True(lowLevelRequests.Count >= 5);
        foreach (var request in lowLevelRequests.Skip(4))
            db.Entry(request).Property(x => x.SoftwareLevel).CurrentValue = RequirementLevel.HighLevel;
        await db.SaveChangesAsync();

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var drifted = Assert.Single(after, x => x.Key == "family-inventory");
        Assert.False(drifted.Holds, drifted.Detail);
        Assert.Contains($"only 4 LowLevel change requests (LLRCR)", drifted.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("HighLevel change requests (HLRCR)", drifted.Detail, StringComparison.Ordinal);
    }
    /// The product exposes a separate Test Results workspace per executable family, so emptying one
    /// family's executions — System here — must fail the inventory with that family named, even though
    /// the other families still hold hundreds of executions and the aggregate volume stays high.
    /// </summary>
    [Fact]
    public async Task Family_inventory_names_an_emptied_executable_results_family()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        var systemExecutions = await (from execution in db.TestExecutions
            join revision in db.TestProcedureRevisions on execution.ProcedureRevisionId equals revision.Id
            join procedure in db.TestProcedures on revision.ProcedureId equals procedure.Id
            where procedure.Level == TestProcedureLevel.System
            select execution).ToListAsync();
        Assert.NotEmpty(systemExecutions);
        db.TestExecutions.RemoveRange(systemExecutions);
        await db.SaveChangesAsync();

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var drifted = Assert.Single(after, x => x.Key == "family-inventory");
        Assert.False(drifted.Holds, drifted.Detail);

        Assert.Contains("only 0 System executions", drifted.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured artifact family emptied straight out of the database - the exact regression the
    /// present-row grouping once hid - is still enumerated at zero and named, while the report keeps the
    /// other families counts visible for the operator.
    /// </summary>
    [Fact]
    public async Task Family_inventory_names_a_configured_artifact_family_emptied_from_the_database()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        /// Raw delete on purpose: this simulates an upgraded database that has lost the family rows,
        /// not a controlled aggregate transition, so no domain lifecycle is involved.
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM test_procedures WHERE Level = 'HighLevel'");
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var drifted = Assert.Single(after, x => x.Key == "family-inventory");
        Assert.False(drifted.Holds, drifted.Detail);
        Assert.Contains("only 0 HighLevel/Case verification artifacts", drifted.Detail, StringComparison.Ordinal);
        Assert.Contains("System/Procedure 75", drifted.Detail, StringComparison.Ordinal);
        Assert.Contains("LowLevel/Case 280", drifted.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <summary>
    /// The migration-only exemption for software-Procedure keys: on a post-cutover profile (simulated
    /// through the seeder's policy seam), those keys enter the configured matrix with zero authored
    /// reviews; the artifact minimum bites immediately while the review/impact minima stay unenforced —
    /// impact items never attach to Procedure reviews, and the review minimum resumes only with authored
    /// provenance, which SQLite's neutral-identity CHECK cannot persist outside the PostgreSQL cutover.
    /// </summary>
    [Fact]
    public async Task Family_inventory_treats_post_cutover_procedure_families_as_migration_only()
    {
        using var database = showcase.Create();
        await using var db = database.Context();
        var seeder = new FmsShowcaseSeeder(db);

        var before = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        Assert.True(Assert.Single(before, x => x.Key == "family-inventory").Holds);

        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == showcase.Summary.ProjectId);
        var resolved = ProjectLadderResolver.Resolve(configuration, LegacyLadderPolicy.Instance);
        var fullProfileSteps = resolved.Steps.Select(x => x.Level
                is RequirementLevel.HighLevel or RequirementLevel.LowLevel
                    ? x with { EnabledArtifactKinds = [VerificationArtifactKind.Case, VerificationArtifactKind.Procedure] }
                    : x).ToArray();
        var fullProfile = new ResolvedProjectLadderPolicy(
            resolved with { Steps = fullProfileSteps }, LegacyLadderPolicy.Instance);
        seeder = new FmsShowcaseSeeder(db, new FixedProjectLadderPolicyResolver(fullProfile));

        var after = await seeder.CheckInvariantsAsync(showcase.Summary.ProgramId);
        var migratedInventory = Assert.Single(after, x => x.Key == "family-inventory");
        Assert.False(migratedInventory.Holds, migratedInventory.Detail);
        Assert.Contains("only 0 HighLevel/Procedure verification artifacts",
            migratedInventory.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("HighLevelSoftware/Procedure test change reviews",
            migratedInventory.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("HighLevelSoftware/Procedure verification impact items",
            migratedInventory.Detail, StringComparison.Ordinal);
    }
}
