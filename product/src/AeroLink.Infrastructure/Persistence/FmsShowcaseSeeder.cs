using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One checked expectation about the showcase, and whether it currently holds.</summary>
public sealed record ShowcaseInvariant(string Key, bool Holds, string Detail);

public sealed record FmsShowcaseSummary(Guid ProgramId, Guid ProjectId, Guid ReleasedBaselineId, Guid ActiveReleaseId,
    int SystemRequirements, int HighLevelRequirements, int LowLevelRequirements, int HistoricalScrs,
    int HistoricalSwcrs, int TraceLinks, int TestProcedures, int TestExecutions, int Documents);

public sealed class FmsShowcaseSeeder(AeroLinkDbContext db)
{
    public const string ProgramCode = "FMSLIVE";
    private static readonly string[] Topics = ["flight plan", "lateral navigation", "vertical navigation", "performance prediction", "navigation database", "guidance", "radio navigation", "position estimation", "fuel management", "crew interface", "departure procedures", "arrival procedures", "approach management", "airspace constraints", "route sequencing"];

    public async Task<FmsShowcaseSummary> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (existing is not null) { await UpgradeAsync(existing.Id, ct); return await SummarizeAsync(existing.Id, ct); }

        var start = new DateTimeOffset(2024, 1, 8, 14, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Flight Management System Live Program", ProgramCode);
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var release15 = new SoftwareRelease(project.Id, "1.5", true); var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        db.AddRange(program, project, release15, release16); await db.SaveChangesAsync(ct);

        var historical = new List<SystemChangeRequest>();
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"SRCR-{i:D5}", ChangeRequestType.System, RequirementLevel.System, 5, (i - 1) * 5, project.Id, release15.Id, start.AddDays(i), "system"));
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"HLRCR-{i:D5}", ChangeRequestType.Software, RequirementLevel.HighLevel, i <= 10 ? 14 : 13, (i - 1) * 13 + Math.Min(i - 1, 10), project.Id, release15.Id, start.AddDays(40 + i), "HLR"));
        for (var i = 1; i <= 45; i++) historical.Add(BuildHistoricalRequest($"LLRCR-{i + 30:D5}", ChangeRequestType.Software, RequirementLevel.LowLevel, i <= 25 ? 16 : 15, (i - 1) * 15 + Math.Min(i - 1, 25), project.Id, release15.Id, start.AddDays(80 + i), "LLR"));
        db.SystemChangeRequests.AddRange(historical); await db.SaveChangesAsync(ct);

        var baseline15 = new CandidateBaseline("SW-01.50", 0, project.Id, release15.Id, null, "FMS 1.5 Released Software Build", "cm.fms", start.AddDays(150));
        foreach (var request in historical) baseline15.Select(request, "cm.fms", start.AddDays(150));
        baseline15.Freeze("cm.fms", start.AddDays(151)); db.CandidateBaselines.Add(baseline15); await db.SaveChangesAsync(ct);
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db)).MaterializeAsync(baseline15.Id, "cm.fms", start.AddDays(152), ct);

        var currentRows = await (from member in db.BaselineRequirements.Where(x => x.BaselineId == baseline15.Id)
                                 join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                                 join revision in db.RequirementRevisions on member.RevisionId equals revision.Id
                                 select new { artifact, revision }).ToListAsync(ct);
        foreach (var row in currentRows.Where(x => x.revision.Revision > 0))
            for (var rev = 0; rev < row.revision.Revision; rev++) db.RequirementRevisions.Add(new RequirementRevision(row.artifact.Id, rev,
                HistoricalStatement(row.artifact.Level, row.artifact.BaseNumber, rev), "Earlier approved wording retained for history.", row.revision.VerificationMethod,
                RequirementRevisionState.Superseded, row.revision.SourceChangeRequestId, baseline15.Id, start.AddDays(10 + rev)));
        await db.SaveChangesAsync(ct);

        var current = currentRows.ToDictionary(x => x.artifact.BaseNumber, x => new CurrentRequirement(x.artifact, x.revision));
        var systems = current.Values.Where(x => x.Artifact.Level == RequirementLevel.System).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var hlrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.HighLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var llrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.LowLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        db.RequirementTraces.AddRange(hlrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, systems[i % systems.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Allocated software behavior satisfies the parent system requirement.", start.AddDays(153))));
        db.RequirementTraces.AddRange(llrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, hlrs[i % hlrs.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Detailed behavior implements the parent high-level requirement.", start.AddDays(153))));
        await db.SaveChangesAsync(ct);

        var build15 = new SoftwareBuild(project.Id, release15.Id, baseline15.Id, "SW-01.50", "Released operational FMS 1.5 software configuration.", "cm.fms", start.AddDays(160));
        db.SoftwareBuilds.Add(build15); await db.SaveChangesAsync(ct);
        var procedures = new List<(TestProcedure Procedure, TestProcedureRevision Revision, List<Guid> Requirements)>();
        procedures.AddRange(BuildProcedures(project.Id, systems.Select(x => x.Revision.Id).ToList(), 75, TestProcedureLevel.System, "SYSTP", start.AddDays(154)));
        procedures.AddRange(BuildProcedures(project.Id, hlrs.Select(x => x.Revision.Id).ToList(), 160, TestProcedureLevel.HighLevel, "HLRTP", start.AddDays(155)));
        procedures.AddRange(BuildProcedures(project.Id, llrs.Select(x => x.Revision.Id).ToList(), 280, TestProcedureLevel.LowLevel, "LLRTP", start.AddDays(156)));
        db.TestProcedures.AddRange(procedures.Select(x => x.Procedure)); db.TestProcedureRevisions.AddRange(procedures.Select(x => x.Revision));
        db.TestCoverage.AddRange(procedures.SelectMany(x => x.Requirements.Select(req => new TestRequirementCoverage(x.Revision.Id, req)))); await db.SaveChangesAsync(ct);
        // This fresh showcase is created after exact procedure manifests exist, so record Build 1.5's
        // configuration before any Build 1.6 draft revision is introduced. Existing historical databases are
        // deliberately not backfilled by an upgrade step: inferring an unrecorded manifest would fabricate
        // controlled history.
        db.BaselineTestProcedures.AddRange(procedures.Select(x =>
            new BaselineTestProcedureSelection(baseline15.Id, x.Procedure.Id, x.Revision.Id)));
        var procedureManifest = string.Join(";", procedures.OrderBy(x => x.Procedure.BaseNumber)
            .Select(x => $"{x.Procedure.BaseNumber}.{x.Revision.Revision:D2}:{x.Revision.Id}"));
        baseline15.MarkTestProceduresMaterialized("cm.fms", Hash(procedureManifest), procedures.Count,
            start.AddDays(158));
        await db.SaveChangesAsync(ct);
        var executionNumber = 0;
        foreach (var item in procedures)
        {
            executionNumber++; var executed = start.AddDays(157).AddMinutes(executionNumber);
            if (executionNumber % 103 == 0)
            {
                var fail = new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Fail, "test.engineer", "FMS integration rig / build 1.5", "Initial observation did not satisfy the expected result.", $"evidence/fms-1.5/fail-{executionNumber:D4}.json", executed, executed, release15.Id);
                db.TestExecutions.Add(fail); db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, fail.Id, TestOutcome.Pass, "test.engineer", "FMS integration rig / corrected configuration", "Retest successfully verified every linked requirement.", $"evidence/fms-1.5/retest-{executionNumber:D4}.json", executed.AddHours(2), executed.AddHours(2), release15.Id));
            }
            else db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Pass, "test.engineer", "FMS integration rig / build 1.5", "Observed results satisfy the approved expected result and linked requirements.", $"evidence/fms-1.5/pass-{executionNumber:D4}.json", executed, executed, release15.Id));
        }
        await db.SaveChangesAsync(ct);

        var docSpecs = new[] {
            (ControlledDocumentType.Sysrd,"SYSRD-000015","FMS System Requirements Document",150),
            (ControlledDocumentType.SwrdHighLevel,"HLRD-000015","FMS High-Level Software Requirements Document",400),
            (ControlledDocumentType.SwrdLowLevel,"LLRD-000015","FMS Low-Level Software Requirements Document",700),
            (ControlledDocumentType.SystemTestProcedures,"SYSTD-000015","FMS System Test Procedures",75),
            (ControlledDocumentType.HighLevelTestProcedures,"HLRTD-000015","FMS HLR Test Procedures",160),
            (ControlledDocumentType.LowLevelTestProcedures,"LLRTD-000015","FMS LLR Test Procedures",280) };
        foreach (var spec in docSpecs) db.ControlledDocuments.Add(new ControlledDocument(project.Id, release15.Id, baseline15.Id, spec.Item1, spec.Item2, spec.Item3, 0, Hash($"{baseline15.RequirementsHash}|{spec.Item1}|{spec.Item4}"), spec.Item4, start.AddDays(159)));

        var activeRequests = BuildActive16Requests(project.Id, release16.Id, current, start.AddDays(300));
        db.SystemChangeRequests.AddRange(activeRequests); await db.SaveChangesAsync(ct);

        // Approval is what raises verification work, and these change requests were approved directly rather
        // than through the endpoint that normally does it. Without this the showcase presents an empty change
        // impact queue while simultaneously showing approved changes that introduce and modify requirements —
        // the one state the product says is impossible.
        var verificationImpact = new VerificationImpactService(db);
        var downstreamImpact = new DownstreamImpactService(db);
        foreach (var request in activeRequests.Where(x => x.State is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline))
        {
            await verificationImpact.RaiseForApprovedChangeRequestAsync(request, start.AddDays(305), ct);
            await downstreamImpact.RaiseForApprovedChangeRequestAsync(request, start.AddDays(305), ct);
        }
        await db.SaveChangesAsync(ct);
        var baseline16 = new CandidateBaseline("SW-01.60", 0, project.Id, release16.Id, baseline15.Id, "FMS 1.6 In-Work Software Build", "cm.fms", start.AddDays(310));
        foreach (var request in activeRequests.Where(x => x.State == ChangeRequestState.Approved).Take(2)) baseline16.Select(request, "cm.fms", start.AddDays(311));
        db.CandidateBaselines.Add(baseline16); await db.SaveChangesAsync(ct);
        // The fresh path runs the same ordered steps, so a database seeded today records them as applied
        // and a later start does not try to reconcile what was just built.
        await UpgradeAsync(program.Id, ct);
        return await SummarizeAsync(program.Id, ct);
    }

    /// <summary>
    /// Brings an already-seeded showcase Program up to the invariants the current seeder produces.
    ///
    /// A fresh seed builds everything in one pass, so nothing here ever ran against a database created
    /// today — which is exactly why the gap went unnoticed. An installation seeded before verification
    /// impact existed kept two approved FMS 1.6 change requests and an empty impact queue, a state the
    /// product describes as impossible, because the code that raises those items shipped afterwards and the
    /// seeder returned early on every subsequent start.
    ///
    /// Each step is keyed, ordered and idempotent, and records itself only after its own work commits. An
    /// interrupted upgrade resumes at the step it stopped on rather than repeating the ones that already
    /// succeeded, and a step added later applies on its own without renumbering anything.
    /// </summary>
    public async Task<IReadOnlyList<string>> UpgradeAsync(Guid programId, CancellationToken ct = default)
    {
        var applied = new List<string>();
        var steps = new (string Key, Func<Guid, CancellationToken, Task<string?>> Run)[]
        {
            ("release-campaign", async (id, token) => { await EnsureReleaseCampaignAsync(id, token); return "Release campaign present."; }),
            ("product-line", async (id, token) => { await EnsureProductLineAsync(id, token); return "Product-line configuration present."; }),
            ("verification-impact", ReconcileVerificationImpactAsync),
            ("downstream-impact", ReconcileDownstreamImpactAsync),
            ("test-change-reviews", EnsureTestChangeReviewsAsync),
            ("problem-report-build-scope", ReconcileProblemReportBuildScopeAsync),
            ("controlled-test-change-identity", ReconcileControlledTestChangeIdentityAsync),
            ("verification-coverage-gap", async (id, token) => { await EnsureVerificationCoverageGapAsync(id, token); return "In-work suspect coverage present."; }),
            ("approver-identity", ReconcileApproverIdentityAsync),
            ("released-campaign", EnsureReleasedCampaignAsync),
            ("code-traceability-demo", EnsureCodeTraceabilityAsync),
        };

        foreach (var step in steps)
        {
            if (await db.ShowcaseUpgradeSteps.AsNoTracking().AnyAsync(x => x.ProgramId == programId && x.StepKey == step.Key, ct)) continue;
            var detail = await step.Run(programId, ct);
            db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, step.Key, detail ?? "No change required.", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            applied.Add($"{step.Key}: {detail ?? "No change required."}");
        }
        return applied;
    }

    private async Task<string?> ReconcileProblemReportBuildScopeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var activeReleases = await db.Releases.Where(x => x.ProjectId == projectId && !x.IsReleased).ToListAsync(ct);
        if (activeReleases.Count != 1) return "No unique active build was available; unscoped records were preserved.";

        var active = activeReleases[0];
        var reports = await db.ProblemReports.Where(x => x.ProjectId == projectId && x.TargetReleaseId == null).ToListAsync(ct);
        var terminal = new[] { ProblemReportState.Closed, ProblemReportState.Duplicate, ProblemReportState.CannotReproduce,
            ProblemReportState.NoFaultFound, ProblemReportState.AcceptedRisk, ProblemReportState.Rejected };
        var reconciled = 0;
        foreach (var report in reports.Where(x => !terminal.Contains(x.State)))
        {
            var now = DateTimeOffset.UtcNow;
            report.Retarget(report.ResponsibleEngineerId, active.Id, now);
            if (!await db.ProblemReportLinks.AnyAsync(x => x.ProblemReportId == report.Id && x.ArtifactType == "Release" && x.Relationship == "BuildScope", ct))
                db.ProblemReportLinks.Add(new ProblemReportLink(report.Id, "Release", active.Id, "BuildScope", "system.workspace", now));
            var snapshot = JsonSerializer.Serialize(new { report.Id, report.ProjectId, report.ReportNumber, report.Revision,
                report.DisplayNumber, report.Title, report.ResponsibleEngineerId, report.TargetReleaseId,
                state = report.State.ToString(), report.Version });
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision, "TargetBuildReconciled",
                "system.workspace", report.CanonicalHash(), snapshot, now));
            reconciled++;
        }
        return $"Scoped {reconciled} active problem report(s) to Build {active.Version}; terminal history was preserved.";
    }

    private async Task<string?> ReconcileControlledTestChangeIdentityAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        // SQLite cannot order DateTimeOffset server-side; this is one Project's bounded TCR collection.
        var reviews = (await db.TestChangeReviews.Where(x => x.ProjectId == projectId).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).ToList();
        var sources = await db.SystemChangeRequests.Where(x => x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.Id, ct);
        var items = await db.VerificationImpactItems.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var numbered = 0;
        // Only rows that have concluded test work is required. An unnumbered row used to mean "raised before
        // controlled numbering existed"; it now also means "raised and not yet assessed", and numbering one
        // of those would answer the assessment on the engineer's behalf.
        foreach (var review in reviews.Where(x => string.IsNullOrEmpty(x.BaseNumber)
            && x.Outcome == TestChangeReviewOutcome.ChangeRequired))
        {
            review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, review.Discipline, ct), DateTimeOffset.UtcNow);
            numbered++;
        }

        var superseded = 0;
        foreach (var legacy in reviews.Where(x => x.State != TestChangeReviewState.Superseded
            && x.Discipline != TestChangeReviewDiscipline.System
            && sources.TryGetValue(x.ChangeRequestId, out var source) && source.Type == ChangeRequestType.System))
        {
            var subjects = items.Where(x => x.TestChangeReviewId == legacy.Id).Select(x => x.SubjectDisplayNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successor = reviews.FirstOrDefault(candidate => candidate.Id != legacy.Id
                && candidate.ReleaseId == legacy.ReleaseId && candidate.Discipline == legacy.Discipline
                && candidate.State != TestChangeReviewState.Superseded
                && sources.TryGetValue(candidate.ChangeRequestId, out var candidateSource) && candidateSource.Type == ChangeRequestType.Software
                && items.Any(item => item.TestChangeReviewId == candidate.Id && subjects.Contains(item.SubjectDisplayNumber)));
            if (successor is null) continue;
            legacy.Supersede(successor.Id,
                $"Replaced by {successor.DisplayNumber}, raised from the correctly classified software change request for the same verification subject.", DateTimeOffset.UtcNow);
            foreach (var item in items.Where(x => x.TestChangeReviewId == legacy.Id)) item.Supersede(DateTimeOffset.UtcNow);
            superseded++;
        }
        return $"Assigned {numbered} legacy controlled TCR number(s) and superseded {superseded} incorrectly classified software package(s).";
    }

    private async Task<string?> EnsureCodeTraceabilityAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releases = await db.Releases.Where(x => x.ProjectId == projectId && (x.Version == "1.5" || x.Version == "1.6")).ToListAsync(ct);
        var released = releases.SingleOrDefault(x => x.Version == "1.5"); var active = releases.SingleOrDefault(x => x.Version == "1.6");
        if (released is null || active is null) return "The showcase build pair is not available.";
        // SQLite cannot order DateTimeOffset server-side. There are only the controlled baselines for one
        // released build here, so materialize that bounded set and make the deterministic choice in memory.
        var baseline = (await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ReleaseId == released.Id && x.RequirementsMaterializedAt != null).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (baseline is null) return "The released LLR baseline is not materialized.";
        var llrs = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                          join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == RequirementLevel.LowLevel) on selection.ArtifactId equals artifact.Id
                          join revision in db.RequirementRevisions.AsNoTracking() on selection.RevisionId equals revision.Id
                          orderby artifact.BaseNumber
                          select new { ArtifactId = artifact.Id, RevisionId = revision.Id }).Take(5).ToListAsync(ct);
        if (llrs.Count < 5) return "Fewer than five LLR revisions are available for the demo scope.";
        // Five sample mappings, against a build that introduced 700 LLR revisions.
        //
        // This is deliberately a sample and no longer pretends to be the whole scope. Build 1.5 is the
        // originating build, so every LLR in its baseline was introduced by one of its own change requests
        // and every one of them owes implementation evidence — the honest number is 700, not five. The gate
        // used to read complete because the projection quietly measured the first five LLRs by number for
        // this Program alone.
        //
        // A released build carrying almost no code evidence is what adopting AeroLink mid-life actually looks
        // like: the code for 1.5 was written before anything recorded the link. Seeding 700 invented merge
        // requests would make the demonstration less truthful, not more.
        var now = new DateTimeOffset(2026, 6, 18, 15, 0, 0, TimeSpan.Zero); var added = 0;
        foreach (var release in new[] { released, active })
        {
            var count = release.Id == released.Id ? 5 : 4;
            for (var index = 0; index < count; index++)
            {
                var llr = llrs[index];
                if (await db.CodeTraceabilityRecords.AnyAsync(x => x.ReleaseId == release.Id && x.RequirementRevisionId == llr.RevisionId, ct)) continue;
                var noCode = index == count - 1;
                var reference = $"!{1842 + index + (release.Id == active.Id ? 20 : 0)}";
                var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{release.Version}:{llr.RevisionId}:{reference}"))).ToLowerInvariant();
                db.CodeTraceabilityRecords.Add(new CodeTraceabilityRecord(projectId, release.Id, llr.ArtifactId, llr.RevisionId,
                    noCode ? CodeTraceDisposition.NoCodeChangeRequired : CodeTraceDisposition.GitLabMerge,
                    "aerolink-demo/fms-navigation", reference, $"Implement exact FMS LLR behavior for Build {release.Version}",
                    $"https://gitlab.com/aerolink-demo/fms-navigation/-/merge_requests/{reference[1..]}", sha,
                    now.AddDays(index), noCode ? "The approved LLR wording clarifies existing behavior; code already conforms and only verification evidence is required." : "",
                    true, "software.lead", now.AddDays(index))); added++;
            }
        }
        return $"Recorded {added} demonstration GitLab traceability mapping(s) as a labelled sample; the released build introduced far more LLR revisions than the sample covers.";
    }

    /// <summary>
    /// Gives the showcase's approval steps back the names of the people who hold them.
    ///
    /// The showcase submitted its reviews naming approvers "Engineering Lead" and "Engineering Manager". Those
    /// are jobs, not people, and an approval step is the one place where the difference matters most: the
    /// panel exists to tell a reader who is being waited on. It answered with a job title and then, having
    /// spent the name on that, had nothing left to say about their authority.
    ///
    /// Only the two literal strings the showcase itself wrote are repaired, matched together with the account
    /// that carries them. A name recorded by an actual reviewer is evidence and is never rewritten here — a
    /// controlled tool that quietly edits who signed something is worse than one that displays it awkwardly.
    /// </summary>
    private async Task<string?> ReconcileApproverIdentityAsync(Guid programId, CancellationToken ct)
    {
        var seededNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lead.reviewer"] = "Maya Patel",
            ["manager.reviewer"] = "Olivia Chen",
        };
        var placeholders = new[] { "Engineering Lead", "Engineering Manager" };
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var cycleIds = await db.ReviewCycles
            .Where(cycle => db.SystemChangeRequests.Any(scr => scr.Id == cycle.ChangeRequestId && scr.ProjectId == projectId))
            .Select(cycle => cycle.Id).ToListAsync(ct);
        var steps = await db.ApprovalSteps
            .Where(step => cycleIds.Contains(step.ReviewCycleId) && placeholders.Contains(step.ApproverName))
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var step in steps)
        {
            if (!seededNames.TryGetValue(step.ApproverId, out var person)) continue;
            // Written through the tracked property rather than the domain, which deliberately keeps a recorded
            // approver name immutable. This is the seeder correcting its own past output, not the product
            // editing an approval.
            db.Entry(step).Property(x => x.ApproverName).CurrentValue = person;
            repaired++;
        }
        if (repaired == 0) return "Approval steps already name people.";
        await db.SaveChangesAsync(ct);
        return $"Named the people behind {repaired} approval steps.";
    }

    private async Task<string?> EnsureTestChangeReviewsAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releases = await db.Releases.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var released = releases.Single(x => x.Version == "1.5");
        var inWork = releases.Single(x => x.Version == "1.6");
        var now = new DateTimeOffset(2024, 11, 28, 14, 0, 0, TimeSpan.Zero);

        var requests = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId
                && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        var existingReviews = await db.TestChangeReviews
            .Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var reviewsByRequestAndDiscipline = existingReviews
            .ToDictionary(x => (x.ChangeRequestId, x.Discipline));
        var raisedChangeIds = (await db.VerificationImpactItems
                .Where(x => x.ProjectId == projectId && x.RequirementChangeId != null)
                .Select(x => x.RequirementChangeId!.Value).ToListAsync(ct))
            .ToHashSet();

        // The deterministic showcase has 105 historical requests. Build the reconciliation from one
        // preloaded graph instead of issuing review/item queries per request.
        var automaticChangeDetection = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var request in requests)
        {
            foreach (var change in request.RequirementChanges.Where(x =>
                         x.Kind is RequirementChangeKind.Introduce or RequirementChangeKind.Modify
                         && !raisedChangeIds.Contains(x.Id)))
            {
                var discipline = change.Level switch
                {
                    RequirementLevel.System => TestChangeReviewDiscipline.System,
                    RequirementLevel.HighLevel => TestChangeReviewDiscipline.HighLevelSoftware,
                    _ => TestChangeReviewDiscipline.LowLevelSoftware
                };
                if (!reviewsByRequestAndDiscipline.TryGetValue((request.Id, discipline), out var review))
                {
                    // The showcase's packages exist precisely because they carry procedure decisions, so they
                    // are seeded as already assessed rather than as questions nobody in the demo will answer.
                    review = new TestChangeReview(projectId, request.TargetReleaseId, request.Id,
                        discipline, request.DisplayNumber, now);
                    review.RecordTestChangeRequired("verification.engineer", now);
                    review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, discipline, ct), now);
                    db.TestChangeReviews.Add(review);
                    reviewsByRequestAndDiscipline.Add((request.Id, discipline), review);
                }
                var display = $"{change.BaseNumber}.{change.Revision:D2}";
                db.VerificationImpactItems.Add(change.Kind == RequirementChangeKind.Introduce
                    ? VerificationImpactItem.ForIntroducedRequirement(projectId, request.TargetReleaseId,
                        request.Id, review.Id, change.Id, display, change.VerificationMethod, now)
                    : VerificationImpactItem.ForModifiedRequirement(projectId, request.TargetReleaseId,
                        request.Id, review.Id, change.Id, display, change.VerificationMethod, now));
                raisedChangeIds.Add(change.Id);
            }
        }
        db.ChangeTracker.DetectChanges();
        db.ChangeTracker.AutoDetectChangesEnabled = automaticChangeDetection;
        await db.SaveChangesAsync(ct);

        var releasedBaselineId = await db.CandidateBaselines
            .Where(x => x.ProjectId == projectId && x.ReleaseId == released.Id && x.RequirementsMaterializedAt != null)
            .Select(x => x.Id).SingleAsync(ct);
        var releasedItems = await db.VerificationImpactItems
            .Where(x => x.ReleaseId == released.Id && x.State != VerificationImpactState.Resolved)
            .ToListAsync(ct);
        var changeIds = releasedItems.Where(x => x.RequirementChangeId is not null)
            .Select(x => x.RequirementChangeId!.Value).ToList();
        var changes = await db.RequirementChanges.Where(x => changeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var exactByBaseNumber = await (from member in db.BaselineRequirements
                                      where member.BaselineId == releasedBaselineId
                                      join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                                      join revision in db.RequirementRevisions on member.RevisionId equals revision.Id
                                      select new { artifact.BaseNumber, artifact.Level, Revision = revision })
            .ToDictionaryAsync(x => x.BaseNumber, ct);
        var exactRevisionIds = exactByBaseNumber.Values.Select(x => x.Revision.Id).ToList();
        var procedureCoverage = (await (from coverage in db.TestCoverage
                                       where exactRevisionIds.Contains(coverage.RequirementRevisionId)
                                       join revision in db.TestProcedureRevisions on coverage.ProcedureRevisionId equals revision.Id
                                       join record in db.TestProcedures on revision.ProcedureId equals record.Id
                                       where revision.State == TestProcedureState.Approved
                                       select new
                                       {
                                           coverage.RequirementRevisionId,
                                           Procedure = record,
                                           Revision = revision
                                       }).ToListAsync(ct))
            .GroupBy(x => x.RequirementRevisionId)
            .ToDictionary(x => x.Key, x => x.First());
        var procedureSequences = (await db.TestProcedures.Where(x => x.ProjectId == projectId)
                .GroupBy(x => x.Level).Select(x => new { Level = x.Key, Count = x.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Level, x => x.Count);

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var item in releasedItems)
        {
            if (item.RequirementChangeId is null || !changes.TryGetValue(item.RequirementChangeId.Value, out var change))
                continue;
            var exact = exactByBaseNumber[change.BaseNumber];
            item.LinkRequirementRevision(exact.Revision.Id, now);

            procedureCoverage.TryGetValue(exact.Revision.Id, out var procedure);
            if (procedure is null)
            {
                var level = exact.Level switch
                {
                    RequirementLevel.System => TestProcedureLevel.System,
                    RequirementLevel.HighLevel => TestProcedureLevel.HighLevel,
                    _ => TestProcedureLevel.LowLevel
                };
                var prefix = level switch
                {
                    TestProcedureLevel.System => "SYSTP",
                    TestProcedureLevel.HighLevel => "HLRTP",
                    _ => "LLRTP"
                };
                var sequence = procedureSequences.GetValueOrDefault(level) + 1;
                procedureSequences[level] = sequence;
                var record = new TestProcedure(projectId, $"{prefix}-{sequence:D6}",
                    $"Verify {change.BaseNumber}", "verification.engineer", now, level);
                var revision = new TestProcedureRevision(record.Id, 0,
                    $"Verify the approved behaviour of {change.BaseNumber}.", "Released FMS test environment",
                    "Exercise the requirement under nominal and boundary conditions.",
                    "Observed behaviour satisfies the approved requirement.", TestProcedureState.Approved,
                    "verification.engineer", now);
                db.AddRange(record, revision, new TestRequirementCoverage(revision.Id, exact.Revision.Id));
                procedure = new
                {
                    RequirementRevisionId = exact.Revision.Id,
                    Procedure = record,
                    Revision = revision
                };
                procedureCoverage[exact.Revision.Id] = procedure;
            }
            item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                $"Procedure alignment completed for released software build SW-01.50 under {change.BaseNumber}.",
                now, procedure.Procedure.Id, procedure.Revision.Id,
                change.Kind == RequirementChangeKind.Introduce
                    ? TestProcedureChangeAction.CreateNew
                    : TestProcedureChangeAction.ModifyExisting,
                preReleaseEvidenceRequired: false);
            db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                item.ResolutionRationale, "verification.engineer", now));
        }
        db.ChangeTracker.DetectChanges();
        db.ChangeTracker.AutoDetectChangesEnabled = automaticChangeDetection;
        await db.SaveChangesAsync(ct);

        // An assessment carrying procedure decisions plainly concluded that test work was required, so it is
        // recorded as having done so and given the controlled number that conclusion earns. Assessments with
        // nothing attached are left unanswered on purpose: the showcase should show both a queue with work
        // waiting to be judged and the test change requests that judging it produced.
        var reviewsWithWork = (await db.VerificationImpactItems
                .Where(x => x.ProjectId == projectId).Select(x => x.TestChangeReviewId).Distinct().ToListAsync(ct))
            .ToHashSet();
        foreach (var review in await db.TestChangeReviews
                     .Where(x => x.ProjectId == projectId && x.Outcome == TestChangeReviewOutcome.Pending)
                     .ToListAsync(ct))
        {
            if (!reviewsWithWork.Contains(review.Id)) continue;
            review.RecordTestChangeRequired("verification.engineer", now);
            if (string.IsNullOrEmpty(review.BaseNumber))
                review.AssignControlledNumber(
                    await IdentifierAllocator.NextTestChangeRequestAsync(db, review.Discipline, ct), now);
        }
        await db.SaveChangesAsync(ct);

        var releasedReviews = await db.TestChangeReviews
            .Where(x => x.ReleaseId == released.Id && x.State == TestChangeReviewState.Open).ToListAsync(ct);
        var incompleteReviewIds = (await db.VerificationImpactItems
                .Where(x => x.ReleaseId == released.Id && x.State != VerificationImpactState.Resolved)
                .Select(x => x.TestChangeReviewId).Distinct().ToListAsync(ct))
            .ToHashSet();
        foreach (var review in releasedReviews)
        {
            // These carry the released build's procedure decisions, so the assessment behind them concluded
            // test work was required. Older showcase databases predate the outcome and are brought forward.
            if (review.Outcome == TestChangeReviewOutcome.Pending)
                review.RecordTestChangeRequired("verification.engineer", now);
            review.Submit("verification.engineer", "assurance.reviewer", !incompleteReviewIds.Contains(review.Id), now);
            review.Approve("assurance.reviewer",
                "Historical procedure changes and exact coverage were approved for released software build SW-01.50.", now);
        }
        await db.SaveChangesAsync(ct);

        var currentReviews = await db.TestChangeReviews.CountAsync(x => x.ReleaseId == inWork.Id, ct);
        return $"{releasedReviews.Count} historical Build 1.5 review(s) completed; {currentReviews} Build 1.6 review(s) remain active.";
    }

    /// <summary>
    /// What the showcase is supposed to contain, checked rather than assumed.
    ///
    /// The upgrade steps report what they did; this reports whether the result is right, which is a
    /// different question. A step that ran and a database that is correct are not the same claim, and the
    /// defect behind this work was precisely a database nobody had checked.
    /// </summary>
    public async Task<IReadOnlyList<ShowcaseInvariant>> CheckInvariantsAsync(Guid programId, CancellationToken ct = default)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return [new ShowcaseInvariant("project", false, "The showcase Program has no Project.")];

        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var materialized = baselines.Where(x => x.RequirementsMaterializedAt is not null).ToList();
        var approved = await db.SystemChangeRequests.AsNoTracking()
            .CountAsync(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline), ct);
        var impacts = await db.VerificationImpactItems.CountAsync(ct);
        var procedures = await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct);
        var executions = await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct);
        var documents = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct);
        var campaigns = await db.ReleaseCampaigns.CountAsync(x => x.ProjectId == projectId, ct);
        var components = await db.ProductLineComponents.CountAsync(x => x.ProjectId == projectId, ct);

        return
        [
            new("releases", releases.Count >= 2, $"{releases.Count} release(s); a released 1.5 and an in-work 1.6 are expected."),
            new("materialized-baseline", materialized.Count >= 1, $"{materialized.Count} materialized baseline(s)."),
            new("documents", documents >= 6, $"{documents} controlled document(s)."),
            new("procedures", procedures >= 500, $"{procedures} test procedure(s)."),
            new("executions", executions >= 500, $"{executions} recorded execution(s)."),
            // The one this work exists for: approved change requests with an empty queue is the state the
            // product calls impossible, and a live installation was sitting in it.
            new("verification-impact", approved == 0 || impacts > 0,
                $"{approved} approved or selected change request(s) and {impacts} verification-impact item(s)."),
            new("release-campaign", campaigns >= 1, $"{campaigns} release campaign(s)."),
            new("product-line", components >= 1, $"{components} product-line component(s)."),
        ];
    }

    /// <summary>
    /// Raises the verification-impact items an approved or selected change request should already have.
    ///
    /// Approval is what raises this work, and these change requests were approved directly in the seed
    /// rather than through the endpoint that normally does it — so a database seeded before the impact
    /// service existed has approved changes introducing and modifying requirements with nothing in the
    /// queue. The service is asked to raise them again; it already declines to duplicate an item that
    /// exists, so this adds only what is missing and leaves anything a user resolved untouched.
    /// </summary>
    private async Task<string?> ReconcileVerificationImpactAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;
        var requests = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        if (requests.Count == 0) return null;

        var before = await db.VerificationImpactItems.CountAsync(ct);
        var service = new VerificationImpactService(db);
        foreach (var request in requests) await service.RaiseForApprovedChangeRequestAsync(request, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        var raised = await db.VerificationImpactItems.CountAsync(ct) - before;
        return raised == 0 ? "Verification impact already complete." : $"Raised {raised} missing verification-impact item(s).";
    }

    private async Task<string?> ReconcileDownstreamImpactAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;
        var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        var before = await db.DownstreamChangeAssessments.CountAsync(ct);
        var service = new DownstreamImpactService(db);
        foreach (var request in requests) await service.RaiseForApprovedChangeRequestAsync(request, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        var raised = await db.DownstreamChangeAssessments.CountAsync(ct) - before;
        return raised == 0 ? "Downstream assessments already complete." : $"Raised {raised} missing downstream assessment(s).";
    }

    /// <summary>
    /// Procedure whose FMS 1.6 rework creates the showcase's suspect coverage.
    ///
    /// Deliberately not SYSTP-000001. Procedures are dealt requirements round-robin, so SYSTP-000001 covers
    /// SYSR-000001 and is therefore the first approved procedure any test that searches for one will find —
    /// putting it into revision took it out of the covering-procedure list and broke the suspect-coverage
    /// journey. A fixture that changes what other journeys discover is not an isolated fixture.
    /// </summary>
    private const string GapProcedureNumber = "SYSTP-000040";

    /// <summary>
    /// A showcase in which all 1,250 requirements are covered can never demonstrate the tool finding a
    /// verification gap, which is the question a verification engineer actually arrives with.
    ///
    /// The gap seeded here is one FMS 1.6 work item: an approved System procedure put back into revision.
    /// Coverage settles only when the procedure it names has no revision in flight, so the two requirements
    /// that procedure covers become Suspect — linked to something that no longer counts — without altering a
    /// single released FMS 1.5 record. The approved revision 0 is untouched, its coverage links are
    /// untouched, and the 1.5 baseline, build, executions and controlled documents all still agree.
    ///
    /// The Uncovered state is deliberately not seeded. Reaching it would take either removing coverage from
    /// a released requirement — a released baseline that failed its own coverage gate, which is a worse
    /// untruth than a missing demonstration state — or materializing the FMS 1.6 baseline, which would
    /// discard the WaitingForPrerequisite lifecycle position DEC-066 exists to show. Uncovered becomes
    /// reachable the moment somebody materializes 1.6, which is a governed action the product already
    /// offers; the requirements awaiting that step are already visible as verification-impact items.
    ///
    /// Idempotent, and safe to apply to a database seeded before this existed: it no-ops when the procedure
    /// is absent or already has a revision in flight.
    /// </summary>
    private async Task EnsureVerificationCoverageGapAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return;
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.BaseNumber == GapProcedureNumber, ct);
        if (procedure is null) return;
        if (await db.TestProcedureRevisions.AnyAsync(x => x.ProcedureId == procedure.Id && x.State != TestProcedureState.Approved, ct)) return;

        db.TestProcedureRevisions.Add(new TestProcedureRevision(procedure.Id, 1,
            "Verify oceanic round-robin waypoint sequencing against the revised FMS 1.6 behavior.",
            "Load the FMS 1.6 candidate software and the approved navigation database.",
            "Initialize oceanic mode, stimulate the revised sequencing inputs, and record each observable output.",
            "Every observed output meets the linked requirement acceptance criteria.",
            TestProcedureState.Draft, "test.author", new DateTimeOffset(2024, 11, 18, 9, 30, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureProductLineAsync(Guid programId,CancellationToken ct)
    {
        var projectId=await db.Projects.Where(x=>x.ProgramId==programId).Select(x=>x.Id).SingleAsync(ct);if(await db.ProductLineComponents.AnyAsync(x=>x.ProjectId==projectId,ct)){await EnsureProductLineCompletionAsync(projectId,ct);return;}var now=new DateTimeOffset(2024,11,20,14,0,0,TimeSpan.Zero);
        var guidance=new ProductLineComponent(projectId,"COMP-00001","Guidance computation core","Reusable lateral and vertical guidance behavior shared by the released and next-generation FMS configurations.","cm.fms",now);var display=new ProductLineComponent(projectId,"COMP-00002","Flight-deck display adapter","Controlled crew-interface adaptation for the active display platform.","cm.fms",now);var main=new ComponentStream(guidance.Id,"MAIN","Released guidance line","cm.fms",now);var next=new ComponentStream(guidance.Id,"NEXT","FMS 1.6 guidance line","cm.fms",now);var displayMain=new ComponentStream(display.Id,"MAIN","Display production line","cm.fms",now);var baseContent="{\"guidanceMode\":\"released\",\"roundRobin\":false,\"integrityMonitoring\":true}";var nextContent="{\"guidanceMode\":\"next\",\"roundRobin\":true,\"integrityMonitoring\":true}";var displayContent="{\"displayPlatform\":\"DU-4\",\"annunciationProfile\":\"certified\"}";var baseRevision=new ComponentStreamRevision(main.Id,1,baseContent,Hash(baseContent),"cm.fms",now);var nextRevision=new ComponentStreamRevision(next.Id,1,nextContent,Hash(nextContent),"cm.fms",now);var displayRevision=new ComponentStreamRevision(displayMain.Id,1,displayContent,Hash(displayContent),"cm.fms",now);guidance.Approve("cm.fms",now);display.Approve("cm.fms",now);var released=new ProductVariant(projectId,"FMS-1.5","Released FMS 1.5 configuration","{\"release\":\"1.5\",\"aircraft\":\"fleet\"}","cm.fms",now);var active=new ProductVariant(projectId,"FMS-1.6","Active FMS 1.6 configuration","{\"release\":\"1.6\",\"aircraft\":\"fleet\"}","cm.fms",now);var selections=new[]{new VariantComponentSelection(released.Id,baseRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(released.Id,displayRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(active.Id,nextRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(active.Id,displayRevision.Id,"{\"required\":true}","cm.fms",now)};released.Approve(now);active.Approve(now);var decision=new ComponentPropagationDecision(active.Id,nextRevision.Id,PropagationDecisionKind.Accept,"FMS 1.6 accepts the round-robin guidance capability after controlled impact analysis.","cm.fms",now);var releasedManifest=$"{{\"variant\":\"FMS-1.5\",\"components\":[\"{baseRevision.ManifestHash}\",\"{displayRevision.ManifestHash}\"]}}";var activeManifest=$"{{\"variant\":\"FMS-1.6\",\"components\":[\"{nextRevision.ManifestHash}\",\"{displayRevision.ManifestHash}\"]}}";var baselines=new[]{new ProductVariantBaseline(released.Id,1,releasedManifest,Hash(releasedManifest),"cm.fms",now),new ProductVariantBaseline(active.Id,1,activeManifest,Hash(activeManifest),"cm.fms",now)};var change=new ConfigurationChangeSet(projectId,"CCS-00001","Propagate round-robin guidance","Controlled propagation from the NEXT stream into the active product configuration.","cm.fms",now);change.ConfigureMerge(guidance.Id,next.Id,baseRevision.Id,nextRevision.Id,nextRevision.Id,nextContent,null,now);change.Close(now);db.AddRange(guidance,display,main,next,displayMain,baseRevision,nextRevision,displayRevision,released,active);db.VariantComponentSelections.AddRange(selections);db.ComponentPropagationDecisions.Add(decision);db.ProductVariantBaselines.AddRange(baselines);db.ConfigurationChangeSets.Add(change);await db.SaveChangesAsync(ct);await EnsureProductLineCompletionAsync(projectId,ct);
    }

    private async Task EnsureProductLineCompletionAsync(Guid projectId,CancellationToken ct)
    {
        if(await db.ControlledLibraries.AnyAsync(x=>x.ProjectId==projectId,ct))return;
        var now=new DateTimeOffset(2024,11,22,14,0,0,TimeSpan.Zero);const string actor="cm.fms";
        const string jpeg="/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EH//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EH//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EH//2Q==";
        string Content(string statement,string threshold)=>JsonSerializer.Serialize(new{requirements=new[]{new{id="LIB-SYSR-00001.00",statement,verification="Test",richContent=new{blocks=new object[]{new{type="paragraph",text="The reusable integrity monitor is allocated identically across applicable FMS configurations."},new{type="table",rows=new[]{new[]{"Monitor","Threshold","Response"},new[]{"Navigation integrity",threshold,"Annunciate and inhibit"}}},new{type="symbol",value="P(alert) < 1E-5 per flight hour"},new{type="reference",label="Navigation integrity allocation",target="ARP4754A safety assessment §4.3"},new{type="image",dataUri="data:image/jpeg;base64,"+jpeg,alt="Navigation integrity monitor architecture",caption="Figure 1 - Controlled navigation integrity monitor architecture"}}}}},traces=new[]{new{source="LIB-SYSR-00001.00",target="FMS-SAFETY-ALLOC-01",type="Satisfies"}},tests=new[]{new{id="LIB-TP-00001",title="Verify integrity monitor alert threshold",covers=new[]{"LIB-SYSR-00001.00"},status="Passed"}}});
        var library=new ControlledLibrary(projectId,"LIB-00001","Navigation integrity assurance","Approved reusable requirements and verification evidence for navigation integrity monitoring.",actor,now);var content1=Content("The FMS shall annunciate loss of navigation integrity within 2 seconds of detecting an invalid solution.","2 seconds");var revision1=new ControlledLibraryRevision(library.Id,1,content1,Hash(content1),actor,now);library.Approve(actor,now);var content2=Content("The FMS shall annunciate loss of navigation integrity within 1 second of detecting an invalid solution.","1 second");var revision2=new ControlledLibraryRevision(library.Id,2,content2,Hash(content2),actor,now.AddDays(1));
        var variants=await db.ProductVariants.Where(x=>x.ProjectId==projectId).OrderBy(x=>x.VariantKey).ToListAsync(ct);var released=variants.Single(x=>x.VariantKey=="FMS-1.5");var active=variants.Single(x=>x.VariantKey=="FMS-1.6");var releasedReuse=new VariantLibraryReuse(released.Id,library.Id,revision1.Id,VariantReuseMode.SynchronizedCopy,"{\"releases\":[\"1.5\"]}",actor,now);var activeReuse=new VariantLibraryReuse(active.Id,library.Id,revision1.Id,VariantReuseMode.SynchronizedCopy,"{\"releases\":[\"1.6\"]}",actor,now);releasedReuse.NotifyUpstream(revision2.Id,now.AddDays(1));activeReuse.NotifyUpstream(revision2.Id,now.AddDays(1));releasedReuse.Decide(PropagationDecisionKind.Defer,revision2.Id,"FMS 1.5 retains its certified two-second response until the next maintenance baseline.",actor,now.AddDays(2));activeReuse.Decide(PropagationDecisionKind.Accept,revision2.Id,"FMS 1.6 accepts the improved one-second integrity response after impact analysis.",actor,now.AddDays(2));
        var releasedDecision=new LibraryPropagationDecision(releasedReuse.Id,released.Id,library.Id,revision1.Id,revision2.Id,PropagationDecisionKind.Defer,"FMS 1.5 retains its certified two-second response until the next maintenance baseline.",actor,now.AddDays(2));var activeDecision=new LibraryPropagationDecision(activeReuse.Id,active.Id,library.Id,revision1.Id,revision2.Id,PropagationDecisionKind.Accept,"FMS 1.6 accepts the improved one-second integrity response after impact analysis.",actor,now.AddDays(2));db.AddRange(library,revision1,revision2,releasedReuse,activeReuse,releasedDecision,activeDecision);await db.SaveChangesAsync(ct);
        foreach(var (variant,reuse) in new[]{(released,releasedReuse),(active,activeReuse)})
        {var components=await db.VariantComponentSelections.AsNoTracking().Where(x=>x.VariantId==variant.Id).Select(x=>new{revisionId=x.ComponentRevisionId,x.ApplicabilityJson}).ToListAsync(ct);var manifest=JsonSerializer.Serialize(new{format="AeroLink product-variant-manifest/v2",variant=variant.VariantKey,components,libraries=new[]{new{reuseId=reuse.Id,libraryId=library.Id,selectedRevisionId=reuse.SelectedRevisionId,latestUpstreamRevisionId=reuse.LatestUpstreamRevisionId,mode=reuse.Mode.ToString(),syncState=reuse.SynchronizationState.ToString(),reuse.ApplicabilityJson}}});var next=(await db.ProductVariantBaselines.Where(x=>x.VariantId==variant.Id).MaxAsync(x=>(int?)x.Revision,ct)??0)+1;db.ProductVariantBaselines.Add(new ProductVariantBaseline(variant.Id,next,manifest,Hash(manifest),actor,now.AddDays(3)));}
        var templateBody=JsonSerializer.Serialize(new{titlePrefix="Configured System Requirements",subtitle="Exact product-line requirements, traceability, verification evidence, and controlled rich content"});var template=new DocumentTemplate(projectId,"TPL-00001","AeroLink configured SYSRD",templateBody,actor,now);var templateRevision=template.Approve(actor,now);var templateSnapshot=JsonSerializer.Serialize(new{template.TemplateNumber,template.Title,templateKind="SYSRD",organization="AeroLink Flight Systems",body=JsonSerializer.Deserialize<object>(templateBody)});db.AddRange(template,new DocumentTemplateRevision(template.Id,templateRevision,"SYSRD","AeroLink Flight Systems",templateBody,Hash(templateSnapshot),actor,now));await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The campaign that shipped Build 1.5, complete and closed.
    ///
    /// Only the in-work build had one, so opening the decision room on a released build answered "Release
    /// readiness is not configured" — which reads as a fault in the product rather than as what it was, a
    /// page with nothing to describe. A released build is the one case where the decision room has the whole
    /// story to tell: everything that was being tracked, and every approval that let it ship.
    ///
    /// Built by driving the same lifecycle a real campaign goes through — verification, an ordered review,
    /// each approval in turn, then release — rather than by writing the finished state into the tables. A
    /// closed campaign assembled by hand would show the right words above evidence that never happened, and
    /// the invariants that guard the real path would never have been asked.
    ///
    /// Nothing on the page can be acted on afterwards, and that needs no work here: a released campaign
    /// refuses every mutation in the domain, which is where it belongs rather than in the buttons.
    ///
    /// Dated to the week after the 1.5 software build was produced, so the story reads in the order it
    /// happened and no approval is signed after the release it authorized.
    /// </summary>
    private async Task<string?> EnsureReleasedCampaignAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct);
        var release = await db.Releases.SingleOrDefaultAsync(x => x.ProjectId == project.Id && x.Version == "1.5", ct);
        if (release is null) return "This Program has no released build.";
        if (await db.ReleaseCampaigns.AnyAsync(x => x.ReleaseId == release.Id, ct)) return "The released build already has its campaign.";
        var baseline = await db.CandidateBaselines.SingleOrDefaultAsync(x => x.ReleaseId == release.Id, ct);
        var build = await db.SoftwareBuilds.SingleOrDefaultAsync(x => x.ReleaseId == release.Id, ct);
        // Without both, there is nothing real to point the campaign at, and a campaign referring to nothing
        // would be worse than the empty page it replaces.
        if (baseline is null || build is null) return "The released build has no baseline or software build to describe.";

        var now = new DateTimeOffset(2024, 6, 17, 14, 0, 0, TimeSpan.Zero);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "FMS 1.5 Release Campaign", "release.manager", now);
        campaign.StartVerification("release.manager", now.AddHours(1));
        campaign.SelectVerificationBuild(build.Id, "release.manager", now.AddHours(2));
        campaign.RecordExecutionProgress("VerificationCompleted",
            "Every procedure required for the 1.5 configuration was executed and its determination recorded.",
            "test.engineer", now.AddDays(1));
        db.ReleaseCampaigns.Add(campaign);

        var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .Where(x => x.TargetReleaseId == release.Id).OrderBy(x => x.BaseNumber).ToListAsync(ct);
        var addressed = 0;
        foreach (var request in requests)
        {
            var dispositions = request.RequirementChanges
                .Select(change => new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Requirement, change.DisplayNumber,
                    $"Confirm the proposed {change.Kind} requirement revision is complete and correctly allocated."))
                .ToList();
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Traceability, request.DisplayNumber,
                "Update and review all upstream and downstream trace links affected by this change."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Verification, request.DisplayNumber,
                "Update test coverage and execute the required verification on the released 1.5 build."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Document, request.DisplayNumber,
                "Regenerate every controlled output affected by this change."));
            // All addressed: this is a build that shipped, and an outstanding item on it would say the
            // opposite of what the record shows.
            foreach (var item in dispositions)
                item.Disposition(ImpactDispositionState.Addressed,
                    "Completed and verified before the 1.5 release review opened.", "release.manager", now.AddDays(1).AddHours(1));
            db.ImpactDispositions.AddRange(dispositions);
            addressed += dispositions.Count;
        }

        var manifest = Hash($"FMS 1.5 release manifest {baseline.ContentHash} {build.BuildNumber}");
        campaign.BeginReleaseReview("release.manager",
            [("program.manager", "Olivia Chen"), ("cm.fms", "Daniel Reyes")], manifest, now.AddDays(2));
        campaign.Approve("program.manager", now.AddDays(3));
        campaign.Approve("cm.fms", now.AddDays(4));
        campaign.Release(build.Id, manifest, "release.manager", now.AddDays(5));
        await db.SaveChangesAsync(ct);
        return $"Recorded the closed 1.5 release campaign with {addressed} addressed impacts and two signed approvals.";
    }

    private async Task EnsureReleaseCampaignAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct); var release = await db.Releases.SingleAsync(x => x.ProjectId == project.Id && x.Version == "1.6", ct);
        if (await db.ReleaseCampaigns.AnyAsync(x => x.ReleaseId == release.Id, ct)) return;
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == release.Id, ct); var now = new DateTimeOffset(2024, 11, 15, 14, 0, 0, TimeSpan.Zero);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "FMS 1.6 Release Campaign", "release.manager", now); campaign.StartVerification("release.manager", now.AddMinutes(1));
        db.ReleaseCampaigns.Add(campaign); var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges).Where(x => x.TargetReleaseId == release.Id).OrderBy(x => x.BaseNumber).ToListAsync(ct);
        foreach (var request in requests)
        {
            var dispositions = request.RequirementChanges.Select(change => new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Requirement, change.DisplayNumber, $"Confirm the proposed {change.Kind} requirement revision is complete and correctly allocated.")).ToList();
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Traceability, request.DisplayNumber, "Update and review all upstream and downstream trace links affected by this change."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Verification, request.DisplayNumber, "Update test coverage and execute the required verification on the selected 1.6 build."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Document, request.DisplayNumber, "Regenerate every controlled output affected by this change."));
            if (request.State == ChangeRequestState.SelectedForBaseline) foreach (var item in dispositions) item.Disposition(ImpactDispositionState.Addressed, "Completed during approved change integration; final release verification remains governed by campaign gates.", "release.manager", now.AddDays(1));
            db.ImpactDispositions.AddRange(dispositions);
        }
        await db.SaveChangesAsync(ct);
    }

    private static SystemChangeRequest BuildHistoricalRequest(string number, ChangeRequestType type, RequirementLevel level, int count, int offset, Guid projectId, Guid releaseId, DateTimeOffset now, string label)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, $"Establish FMS {label} requirement group {number[^2..]}", "The product baseline requires controlled FMS behavior.", "Operational and assurance needs were analyzed and allocated.", "Introduce the approved requirement set with verification criteria.", type == ChangeRequestType.System ? "systems.author" : "software.author", now, type, softwareLevel: type == ChangeRequestType.Software ? level : null);
        for (var j = 1; j <= count; j++) { var index = offset + j; var prefix = level == RequirementLevel.System ? "SYSR" : level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var revision = index % 11 == 0 ? 2 : index % 5 == 0 ? 1 : 0;
            request.AddRequirementChange(request.AuthorId, $"{prefix}-{index:D6}", revision, level, RequirementChangeKind.Introduce, CurrentStatement(level, index), $"Allocated {Topics[(index - 1) % Topics.Length]} capability for the FMS 1.5 baseline.", "Test", now); }
        request.SubmitForReview(request.AuthorId, [new("assurance.reviewer", "Development Assurance Reviewer")], now.AddHours(2)); request.ApproveActiveStage("assurance.reviewer", now.AddDays(1)); return request;
    }

    private static List<(TestProcedure, TestProcedureRevision, List<Guid>)> BuildProcedures(Guid projectId, List<Guid> requirements, int count, TestProcedureLevel level, string prefix, DateTimeOffset now)
    {
        var buckets = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList(); for (var i = 0; i < requirements.Count; i++) buckets[i % count].Add(requirements[i]);
        return buckets.Select((ids, i) => { var number = $"{prefix}-{i + 1:D6}"; var procedure = new TestProcedure(projectId, number, $"Verify {level} FMS behavior group {i + 1:D3}", "test.author", now, level);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify all linked FMS requirement revisions.", "Load released FMS 1.5 software and the approved navigation database.", "Initialize the applicable mode, stimulate the defined inputs, and record each observable output.", "Every observed output meets the linked requirement acceptance criteria.", TestProcedureState.Approved, "test.author", now); return (procedure, revision, ids); }).ToList();
    }

    private static List<SystemChangeRequest> BuildActive16Requests(Guid projectId, Guid releaseId, Dictionary<string, CurrentRequirement> current, DateTimeOffset now)
    {
        var result = new List<SystemChangeRequest>();
        for (var i = 1; i <= 8; i++)
        {
            // The number names the level, so it is derived from the same rule the application uses rather
            // than written out by hand. i <= 4 is HLR work and the rest is LLR, matching the requirement
            // changes each request goes on to carry.
            var system = i <= 2; var type = system ? ChangeRequestType.System : ChangeRequestType.Software;
            var packageLevel = system ? (RequirementLevel?)null : i <= 4 ? RequirementLevel.HighLevel : RequirementLevel.LowLevel;
            var number = $"{ChangeRequestNumbering.Prefix(type, packageLevel)}-{(system ? 30 + i : 75 + i - 2):D5}";
            var request = new SystemChangeRequest(number, 0, projectId, releaseId, i == 1 ? "Introduce oceanic round-robin waypoint sequencing" : $"FMS 1.6 change package {i}", "Operational feedback or a product improvement requires controlled change.", "The impact to requirements, traces, and verification has been assessed.", "Update the applicable FMS behavior and verification assets.", type == ChangeRequestType.System ? "systems.author" : "software.author", now.AddDays(i), type, softwareLevel: packageLevel);
            if (i == 1) request.AddRequirementChange(request.AuthorId, "SYSR-000151", 0, RequirementLevel.System, RequirementChangeKind.Introduce, "The FMS shall support configurable round-robin sequencing of eligible oceanic waypoints.", "New FMS 1.6 capability.", "Test", now);
            else { var level = system ? RequirementLevel.System : i <= 4 ? RequirementLevel.HighLevel : RequirementLevel.LowLevel; var prefix = level == RequirementLevel.System ? "SYSR" : level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var max = level == RequirementLevel.System ? 150 : level == RequirementLevel.HighLevel ? 400 : 700; var idx = ((i * 37) % max) + 1; var row = current[$"{prefix}-{idx:D6}"]; request.AddRequirementChange(request.AuthorId, $"{prefix}-{idx:D6}", row.Revision.Revision + 1, level, RequirementChangeKind.Modify, CurrentStatement(level, idx) + " The behavior shall include the approved FMS 1.6 refinement.", "Product improvement or corrective action.", "Test", now); }
            if (i <= 2) { request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel")], now.AddDays(i).AddHours(1)); request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2)); }
            else if (i == 3) { request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel"), new("manager.reviewer", "Olivia Chen")], now.AddDays(i).AddHours(1)); request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2)); request.ApproveActiveStage("manager.reviewer", now.AddDays(i).AddHours(3)); }
            else if (i == 4) request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel"), new("manager.reviewer", "Olivia Chen")], now.AddDays(i).AddHours(1));
            else if (i == 8) request.Defer(request.AuthorId, "Deferred from FMS 1.6 pending operational priority confirmation.", now.AddDays(i).AddHours(2));
            result.Add(request);
        }
        return result;
    }

    private async Task<FmsShowcaseSummary> SummarizeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct); var release16 = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == "1.6").Select(x => x.Id).SingleAsync(ct);
        var baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.Name.Contains("1.5 Released")).Select(x => x.Id).SingleAsync(ct);
        return new(programId, projectId, baselineId, release16,
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.System, ct),
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.HighLevel, ct),
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.LowLevel, ct),
            await db.SystemChangeRequests.CountAsync(x => x.ProjectId == projectId && x.Type == ChangeRequestType.System && x.TargetReleaseId != release16, ct),
            await db.SystemChangeRequests.CountAsync(x => x.ProjectId == projectId && x.Type == ChangeRequestType.Software && x.TargetReleaseId != release16, ct),
            await db.RequirementTraces.CountAsync(x => x.ProjectId == projectId, ct), await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct),
            await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct), await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct));
    }

    private static string CurrentStatement(RequirementLevel level, int index) => level switch { RequirementLevel.System => $"The FMS shall provide controlled {Topics[(index - 1) % Topics.Length]} capability {index:D3} throughout the applicable operational modes.", RequirementLevel.HighLevel => $"The FMS software shall compute and manage {Topics[(index - 1) % Topics.Length]} behavior H{index:D3} using validated inputs and deterministic state transitions.", _ => $"The FMS low-level component shall implement {Topics[(index - 1) % Topics.Length]} algorithm L{index:D3} with bounded execution and explicit status reporting." };
    private static string HistoricalStatement(RequirementLevel level, string baseNumber, int revision) => $"Historical revision {revision:D2} of {baseNumber} defined the earlier approved {level} FMS behavior.";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private sealed record CurrentRequirement(RequirementArtifact Artifact, RequirementRevision Revision);
}
