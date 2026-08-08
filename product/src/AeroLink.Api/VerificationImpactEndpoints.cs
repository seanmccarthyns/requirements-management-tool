using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AeroLink.Api;

public sealed record AssignVerificationImpactRequest(string EngineerId);
public sealed record ResolveVerificationImpactRequest(VerificationImpactOutcome Outcome, string Rationale, Guid? ProcedureId,
    TestProcedureChangeAction? ProcedureChangeAction = null, bool PreReleaseEvidenceRequired = false,
    Guid? RetargetedRequirementRevisionId = null);
public sealed record ReopenVerificationImpactRequest(string Rationale);
public sealed record IncludeChangeRequestRequest(Guid ChangeRequestId, long? ExpectedVersion = null);
public sealed record CreateTestChangeRequestRequest(TestChangeReviewDiscipline Discipline, Guid[] ChangeRequestIds,
    Guid[]? ProblemReportIds = null, string Title = "", string Problem = "", string Analysis = "",
    string Solution = "", string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null);
public sealed record WriteTestChangeRequestCaseRequest(string Title, string Problem, string Analysis,
    string Solution, string? ProblemRich = null, string? AnalysisRich = null, string? SolutionRich = null,
    long? ExpectedVersion = null);
public sealed record LinkProblemReportsRequest(Guid[] ProblemReportIds, long? ExpectedVersion = null);
public sealed record SubmitTestChangeReviewRequest(string ApproverId,
    IReadOnlyList<TestChangeRequestApproverRequest>? Approvers = null, long? ExpectedVersion = null);
/// <summary>One person chosen for one configured review stage.</summary>
public sealed record TestChangeRequestApproverRequest(string UserId, string Name = "");
/// <param name="Rationale">Why no test work is needed. Required only when concluding that none is.</param>
public sealed record TestAssessmentConclusionRequest(bool TestChangeRequired, string? Rationale,
    long? ExpectedVersion = null);
public sealed record ApproveTestChangeReviewRequest(string Rationale);
/// <summary>
/// One proposed change to one procedure. <paramref name="BaseNumber"/> is omitted when introducing — the
/// number is allocated here so two engineers cannot pick the same one — and required otherwise, because a
/// modification or retirement has to name the procedure it acts on.
/// </summary>
public sealed record ProposeProcedureChangeRequest(TestProcedureChangeKind Kind, string? BaseNumber, int Revision,
    string Title, string Objective, string Preconditions, string Steps, string ExpectedResult, string Rationale,
    Guid[]? DrivingRequirementRevisionIds, long? ExpectedVersion = null);
public sealed record ReturnTestChangeReviewRequest(string Rationale);

public static class VerificationImpactEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkVerificationImpactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/releases/{releaseId:guid}/verification-impact", async (Guid releaseId, bool? outstandingOnly,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var projectId = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId == Guid.Empty) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var items = outstandingOnly == true
                ? await service.OutstandingForReleaseAsync(releaseId, ct)
                : await service.ForReleaseAsync(releaseId, ct);
            return Results.Ok(await MapAsync(items, db, ct));
        });

        app.MapGet("/api/releases/{releaseId:guid}/test-change-reviews", async (Guid releaseId,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, release.ProjectId, ct)) return Results.Forbid();
            var actor = http.UserAccount().UserName;
            var canTest = !release.IsReleased && await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct,
                ProgramRole.TestEngineer, ProgramRole.TestLead);
            var isLead = !release.IsReleased && await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct,
                ProgramRole.TestLead);
            // Ordered in memory on purpose: SQLite cannot translate an ORDER BY over a DateTimeOffset and
            // throws, which took this whole endpoint to a 500 and left the workspace looking simply empty.
            var reviews = (await db.TestChangeReviews.AsNoTracking()
                    .Include(x => x.AdditionalSources)
                    .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                    .Where(x => x.ReleaseId == releaseId)
                    .ToListAsync(ct))
                .OrderBy(x => x.State).ThenBy(x => x.Discipline).ThenBy(x => x.CreatedAt)
                .ToList();
            var reviewIds = reviews.Select(x => x.Id).ToList();
            var changeRequestIds = reviews.Select(x => x.ChangeRequestId)
                .Concat(reviews.SelectMany(x => x.AdditionalSources).Select(x => x.ChangeRequestId)).Distinct().ToList();
            var changeRequests = await db.SystemChangeRequests.AsNoTracking().Where(x => changeRequestIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Title, ct);
            var items = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => reviewIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
            var reportLinks = await db.ProblemReportLinks.AsNoTracking()
                .Where(x => x.ArtifactType == "TestChangeRequest" && reviewIds.Contains(x.ArtifactId))
                .ToListAsync(ct);
            var reportIds = reportLinks.Select(x => x.ProblemReportId).Distinct().ToList();
            var reportDirectory = await db.ProblemReports.AsNoTracking().Where(x => reportIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => new { x.Id, x.DisplayNumber, x.Title, state = x.State.ToString() }, ct);
            return Results.Ok(new
            {
                canCreate = canTest,
                items = reviews.Select(review => new
                {
                    review.Id,
                    review.ProjectId,
                    review.ReleaseId,
                    review.ChangeRequestId,
                    discipline = review.Discipline.ToString(),
                    state = review.State.ToString(),
                    review.SourceChangeRequestNumber,
                    review.DisplayNumber,
                    review.Title,
                    review.Problem,
                    review.Analysis,
                    review.Solution,
                    review.ProblemRich,
                    review.AnalysisRich,
                    review.SolutionRich,
                    // Every change request this package answers for, the one it was raised from first. A reader
                    // scanning the list needs to see that two changes are being tested together without opening it.
                    coveredChangeRequests = new[] { new { id = review.ChangeRequestId, number = review.SourceChangeRequestNumber, title = changeRequests.GetValueOrDefault(review.ChangeRequestId) ?? "Source change request", originating = true } }
                        .Concat(review.AdditionalSources.OrderBy(x => x.ChangeRequestNumber)
                            .Select(x => new { id = x.ChangeRequestId, number = x.ChangeRequestNumber, title = changeRequests.GetValueOrDefault(x.ChangeRequestId) ?? "Source change request", originating = false })),
                    review.AssignedEngineerId,
                    outcome = review.Outcome.ToString(),
                    review.NoChangeRationale,
                    review.DecidedBy,
                    review.DecidedAt,
                    review.SubmittedBy,
                    review.SelectedApproverId,
                    version = review.Version,
                    review.SubmittedAt,
                    review.ApprovedBy,
                    review.ApprovedAt,
                    review.ApprovalRationale,
                    review.SupersededByTestChangeRequestId,
                    review.SupersededReason,
                    totalItems = items.Count(x => x.TestChangeReviewId == review.Id),
                    resolvedItems = items.Count(x => x.TestChangeReviewId == review.Id && x.State == VerificationImpactState.Resolved),
                    preReleaseEvidenceItems = items.Count(x => x.TestChangeReviewId == review.Id && x.PreReleaseEvidenceRequired)
                    ,problemReports = reportLinks.Where(x => x.ArtifactId == review.Id)
                        .Select(x => reportDirectory.GetValueOrDefault(x.ProblemReportId)).Where(x => x is not null)
                        .DistinctBy(x => x!.Id)
                    ,reviewCycle = LatestNonCancelledCycle(review) is { } cycle
                        ? new
                        {
                            cycle.Id,
                            cycle.Sequence,
                            mode = cycle.Mode.ToString(),
                            state = cycle.State.ToString(),
                            cycle.WorkflowId,
                            cycle.WorkflowLogicalId,
                            cycle.WorkflowName,
                            cycle.WorkflowVersion,
                            steps = cycle.Steps.OrderBy(x => x.Position).Select(step => new
                            {
                                step.Position,
                                stageName = step.StageName,
                                authority = step.Authority,
                                approverId = step.ApproverId,
                                approverName = step.ApproverName,
                                state = step.State.ToString(),
                                step.DecidedAt
                            })
                        }
                        : null
                    ,capabilities = new
                    {
                        // Unheld or held by this reader. Taking a package on used to be a step of its own before
                        // any of its work was offered; answering it is what takes it now, so an unheld package is
                        // open to anybody with the authority and a held one stays with whoever answered first.
                        // Answering/claiming an unheld package is open to any test engineer; Test Leads can
                        // additionally assign it elsewhere through the assign endpoint.
                        canAssign = canTest && review.State == TestChangeReviewState.Open && review.AssignedEngineerId == null,
                        canDecide = canTest && review.State == TestChangeReviewState.Open
                            && (review.AssignedEngineerId == null
                                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                                || isLead),
                        canSubmit = canTest && review.State == TestChangeReviewState.Open
                            && (review.AssignedEngineerId == null
                                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                                || isLead),
                        // Approval and return authority come from the active review-cycle step, not from the
                        // legacy single-approver fields. A configured workflow can have one active step
                        // (sequential) or several (parallel), and the stage may require any Program role.
                        canApprove = !release.IsReleased && review.State == TestChangeReviewState.InReview
                            && (LatestNonCancelledCycle(review) is { State: ReviewCycleState.Active } activeCycle
                                && activeCycle.Steps.Any(step => step.State == ApprovalStepState.Active
                                    && string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase))),
                        canReturn = !release.IsReleased && review.State == TestChangeReviewState.InReview
                            && (LatestNonCancelledCycle(review) is { State: ReviewCycleState.Active } activeCycleReturn
                                && activeCycleReturn.Steps.Any(step => step.State == ApprovalStepState.Active
                                    && string.Equals(step.ApproverId, actor, StringComparison.OrdinalIgnoreCase)))
                    }
                })
            });
        });

        /// Additively links Problem Reports to an Open test change request.
        ///
        /// Linking is additive: existing links are never deleted here, and the governed link set is part of
        /// the review snapshot, so any actually-added link advances the package Version in the same unit of
        /// work. A link-versus-submit race therefore collapses to one winner: whichever side saves second
        /// hits the concurrency token and receives 409 stale_version.
        app.MapPost("/api/test-change-reviews/{id:guid}/problem-reports", async (Guid id,
            LinkProblemReportsRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity,
            ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            // Stale writes are refused before lifecycle checks: a caller who loaded an older version must be
            // told to refresh even when the package has also moved InReview.
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its Problem Report links.",
                    code = "stale_version"
                });
            if (review.State != TestChangeReviewState.Open)
                return Results.Conflict(new { error = "Problem Report links can be changed only while the test change request is Open." });
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            var error = await problemReports.ValidateSelectionAsync(review.ProjectId, review.ReleaseId,
                request.ProblemReportIds, ct);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                var now = DateTimeOffset.UtcNow;
                var before = (await db.ProblemReportLinks.AsNoTracking()
                        .Where(x => x.ArtifactType == "TestChangeRequest" && x.ArtifactId == review.Id)
                        .Select(x => x.ProblemReportId).ToListAsync(ct))
                    .ToHashSet();
                await problemReports.LinkTestChangeRequestAsync(review.Id, request.ProblemReportIds,
                    http.UserAccount().UserName, now, ct);
                var added = (request.ProblemReportIds?.Distinct() ?? []).Any(id => !before.Contains(id));
                if (added) review.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, linkedProblemReports = (request.ProblemReportIds?.Distinct() ?? []) });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// <summary>
        /// The test assessment's conclusion, and the point at which a test change request comes into being.
        ///
        /// Mirrors the requirements-side downstream assessment exactly, because it is the same question asked
        /// of the verification discipline: does this approved change need work here or not. Concluding that
        /// it does allocates the controlled SYSTCR, HLRTCR or LLRTCR number; concluding that it does not
        /// produces nothing, and so is the conclusion that goes for approval.
        /// </summary>
        app.MapPost("/api/test-change-reviews/{id:guid}/conclusion", async (Guid id, TestAssessmentConclusionRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var actor = http.UserAccount().UserName;
            if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            if (review.AssignedEngineerId is not null
                && !string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                && !await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead))
                return Results.Forbid();
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before recording its conclusion.",
                    code = "stale_version"
                });
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Answering an unheld package is what takes it on. The claim is no longer a step of its own,
                // but the record of who holds it still has to be true — the next reader needs to see that
                // somebody is on it, and submission and approval both key on the holder.
                if (review.AssignedEngineerId is null) review.Assign(actor, actor, now);
                if (request.TestChangeRequired)
                {
                    review.RecordTestChangeRequired(actor, now);
                    review.AssignControlledNumber(
                        await IdentifierAllocator.NextTestChangeRequestAsync(db, review.Discipline, ct), now);
                }
                else review.RecordNoTestChangeRequired(actor, request.Rationale ?? "", now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id, outcome = review.Outcome.ToString(), review.BaseNumber, review.DisplayNumber,
                    review.NoChangeRationale, review.DecidedBy, review.DecidedAt, state = review.State.ToString()
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        // The procedure decisions a test change request carries — what the workspace reads and writes, and the
        // test-side counterpart of the requirement changes a change request carries.
        app.MapGet("/api/test-change-reviews/{id:guid}/procedure-changes", async (Guid id,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.AsNoTracking().Include(x => x.ProcedureChanges)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct)) return Results.Forbid();
            // Derived here rather than inferred by the client from a broad role. The workspace was offering
            // authoring controls to anyone with test authority while these same rules refused them, which is
            // an invitation to an error message.
            var actor = http.UserAccount().UserName;
            var released = await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct);
            var isTester = !released && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead);
            var isLead = !released && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead);
            var holdsIt = review.AssignedEngineerId is null
                || string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
                || isLead;
            var mayAuthor = isTester && holdsIt;
            // The requirements this discipline's procedures may be written against, so the authoring form can
            // offer a choice instead of asking an engineer to know an identifier.
            var wanted = ApiMap.RequirementLevelFor(review.ProcedureLevel());
            var candidates = await (from item in db.VerificationImpactItems.AsNoTracking()
                                    where item.TestChangeReviewId == review.Id && item.RequirementRevisionId != null
                                    join revision in db.RequirementRevisions.AsNoTracking() on item.RequirementRevisionId equals revision.Id
                                    join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                    where artifact.ProjectId == review.ProjectId && artifact.Level == wanted
                                    select new { revisionId = revision.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, revision.Statement })
                .Distinct().ToListAsync(ct);
            // Modify and Retire act on what this target build carries, not on the newest procedure revision
            // anywhere in the Project. Coverage is not membership and a later build is not an authoring menu.
            var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                db, review.ProjectId, review.ReleaseId, ct);
            var targetRevisionIds = effectivity?.RevisionIds ?? [];
            var targets = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                     .Where(x => targetRevisionIds.Contains(x.Id))
                                 join procedure in db.TestProcedures.AsNoTracking()
                                     .Where(x => x.ProjectId == review.ProjectId && x.Level == review.ProcedureLevel())
                                     on revision.ProcedureId equals procedure.Id
                                 orderby procedure.BaseNumber
                                 select new
                                 {
                                     procedure.BaseNumber,
                                     procedure.Title,
                                     CurrentRevision = revision.Revision
                                 }).Take(500).ToListAsync(ct);
            return Results.Ok(new
            {
                capabilities = new
                {
                    canProposeProcedureChange = mayAuthor && review.State == TestChangeReviewState.Open
                        && review.Outcome == TestChangeReviewOutcome.ChangeRequired,
                    canWithdrawProcedureChange = mayAuthor && review.State == TestChangeReviewState.Open,
                    canRevise = mayAuthor && review.State == TestChangeReviewState.Approved,
                },
                drivingRequirementChoices = candidates,
                procedureTargets = targets.Select(x => new
                {
                    x.BaseNumber, x.Title,
                    currentRevision = x.CurrentRevision,
                }),
                review.Id, review.DisplayNumber, review.BaseNumber, review.Revision,
                discipline = review.Discipline.ToString(), state = review.State.ToString(),
                outcome = review.Outcome.ToString(), procedureLevel = review.ProcedureLevel().ToString(),
                review.SourceChangeRequestNumber, review.AssignedEngineerId,
                version = review.Version,
                review.Title, review.Problem, review.Analysis, review.Solution,
                review.ProblemRich, review.AnalysisRich, review.SolutionRich,
                procedureChanges = review.ProcedureChanges
                    .OrderBy(x => x.BaseNumber)
                    .Select(x => new
                    {
                        x.Id, x.DisplayNumber, x.BaseNumber, x.Revision, kind = x.Kind.ToString(),
                        level = x.Level.ToString(), x.Title, x.Objective, x.Preconditions, x.Steps,
                        x.ExpectedResult, x.Rationale,
                        drivingRequirementRevisionIds = DrivingRequirements(x.DrivingRequirementRevisionIdsJson)
                    }).ToList()
            });
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/procedure-changes", async (Guid id,
            ProposeProcedureChangeRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.ReviewCycles)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before proposing a procedure change.",
                    code = "stale_version"
                });
            try
            {
                var now = DateTimeOffset.UtcNow;
                // Introducing allocates; modifying or retiring names what already exists. Letting the caller
                // choose a number for a new procedure would let two engineers pick the same one.
                var baseNumber = request.Kind == TestProcedureChangeKind.Introduce
                    ? await IdentifierAllocator.NextTestProcedureAsync(db, review.ProcedureLevel(), ct)
                    : (request.BaseNumber ?? "").Trim();
                if (request.Kind != TestProcedureChangeKind.Introduce && baseNumber.Length == 0)
                    return Results.BadRequest(new { error = "A modification or retirement must name the procedure it acts on." });
                // Deliberately no "must name a requirement revision" rule here, though the direct-create route
                // that this replaced had one. That route wrote a controlled procedure immediately, so it
                // needed its coverage at that moment; a package only proposes, and a proposal's driving
                // revisions become real coverage at materialization. Whether a package may introduce a
                // procedure that names nothing is a product question, not a gap to be closed by reflex.
                // A modification or retirement names a controlled procedure, so the server proves it is one:
                // that it exists, belongs to this project, sits at this discipline's level, and that the
                // proposed revision advances the one it actually has. Left unchecked, a typo survived approval
                // and failed at materialization, which puts an authoring mistake in the release path.
                if (request.Kind != TestProcedureChangeKind.Introduce)
                {
                    var target = await db.TestProcedures.AsNoTracking()
                        .Where(x => x.BaseNumber == baseNumber)
                        .Select(x => new { x.Id, x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (target is null)
                        return Results.BadRequest(new { error = $"{baseNumber} is not a controlled test procedure." });
                    if (target.ProjectId != review.ProjectId)
                        return Results.BadRequest(new { error = $"{baseNumber} belongs to another project." });
                    if (target.Level != review.ProcedureLevel())
                        return Results.BadRequest(new { error = $"{baseNumber} is a {target.Level} procedure and cannot be changed by a {review.Discipline} test change request." });
                    var effectivity = await TestProcedureEffectivity.ForReleaseAsync(
                        db, review.ProjectId, review.ReleaseId, ct);
                    if (effectivity is null || !effectivity.RevisionByProcedure.TryGetValue(target.Id, out var carriedRevisionId))
                        return Results.BadRequest(new
                        {
                            error = $"{baseNumber} is not carried by the target software build.",
                            code = "procedure_not_carried_by_build"
                        });
                    var current = await db.TestProcedureRevisions.AsNoTracking()
                        .Where(x => x.Id == carriedRevisionId).Select(x => (int?)x.Revision)
                        .SingleOrDefaultAsync(ct);
                    if (current is null)
                        return Results.BadRequest(new
                        {
                            error = $"The target build's selected revision for {baseNumber} no longer exists.",
                            code = "procedure_manifest_revision_missing"
                        });
                    if (request.Revision != current.Value + 1)
                        return Results.BadRequest(new
                        {
                            error = $"{baseNumber}.{current.Value:D2} is carried by the target build. The proposed revision must be {current.Value + 1:D2}.",
                            code = "procedure_revision_not_next_for_build"
                        });
                }

                // The requirements a procedure is written against have to be this project's, and at this
                // discipline's level. A cross-project identifier would otherwise become controlled coverage at
                // materialization and claim verification that belongs to another program.
                var driving = request.DrivingRequirementRevisionIds ?? [];
                if (driving.Length != 0)
                {
                    var known = await (from revision in db.RequirementRevisions.AsNoTracking()
                                       join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                       where driving.Contains(revision.Id)
                                       select new { revision.Id, artifact.ProjectId, artifact.Level })
                        .ToDictionaryAsync(x => x.Id, ct);
                    var wanted = ApiMap.RequirementLevelFor(review.ProcedureLevel());
                    foreach (var drivingId in driving.Distinct())
                    {
                        if (!known.TryGetValue(drivingId, out var requirement))
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} does not exist." });
                        if (requirement.ProjectId != review.ProjectId)
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} belongs to another project." });
                        if (requirement.Level != wanted)
                            return Results.BadRequest(new { error = $"Requirement revision {drivingId} is a {requirement.Level} requirement, which a {review.Discipline} procedure does not verify." });
                    }
                }
                var change = review.AddProcedureChange(http.UserAccount().UserName, new TestProcedureChangeDraft(
                    baseNumber, request.Revision, review.ProcedureLevel(), request.Kind, request.Title ?? "",
                    request.Objective ?? "", request.Preconditions ?? "", request.Steps ?? "",
                    request.ExpectedResult ?? "", request.Rationale ?? "",
                    JsonSerializer.Serialize(request.DrivingRequirementRevisionIds ?? [])), now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    change.Id, change.DisplayNumber, change.BaseNumber, change.Revision,
                    kind = change.Kind.ToString(), level = change.Level.ToString(), change.Title
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        app.MapDelete("/api/test-change-reviews/{id:guid}/procedure-changes/{changeId:guid}", async (Guid id,
            Guid changeId, long? expectedVersion, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (expectedVersion is not null && review.Version != expectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before withdrawing a procedure change.",
                    code = "stale_version"
                });
            try
            {
                review.RemoveProcedureChange(changeId, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, remaining = review.ProcedureChanges.Count });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        // Reopening approved test work to correct it, exactly as a change request advances to its next revision.
        app.MapPost("/api/test-change-reviews/{id:guid}/revise", async (Guid id, HttpContext http,
            AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            // AdditionalSources as well as ProcedureChanges: the successor takes the folded-in claims with it,
            // and an unloaded collection is an empty one. The claims would stay on the predecessor and the new
            // revision would quietly cover less, which is the exact outcome moving them is meant to prevent.
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.AdditionalSources)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            // Correcting approved work belongs to the engineer who holds it. A lead may still step in, which is
            // supervision rather than a second author, but an unrelated engineer starting a successor revision
            // would change the lineage of somebody else's controlled package without anyone deciding it should.
            var reviseRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (reviseRefusal is not null) return reviseRefusal;
            try
            {
                var released = await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct);
                var now = DateTimeOffset.UtcNow;
                var next = review.StartNextRevision(http.UserAccount().UserName, now, released);
                db.TestChangeReviews.Add(next);
                // Superseded in the same unit of work as its successor is created. Two revisions both reading
                // as current is worse than either state on its own: configuration management could carry the
                // obsolete one into a build while an engineer is correcting it.
                review.Supersede(next.Id, $"Superseded by controlled revision {next.DisplayNumber}.", now);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    next.Id, next.DisplayNumber, next.Revision, state = next.State.ToString(),
                    outcome = next.Outcome.ToString(), procedureChanges = next.ProcedureChanges.Count,
                    coveredChangeRequests = next.CoveredChangeRequestIds.Count(),
                });
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "A later revision of this test change request already exists. Refresh to see the current record." });
            }
            catch (DomainException problem) { return Results.BadRequest(new { error = problem.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });

            var actor = http.UserAccount().UserName;
            var selfClaim = string.Equals(actor, request.EngineerId, StringComparison.OrdinalIgnoreCase);
            var mayClaim = selfClaim && await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer);
            var mayAssign = await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead);
            if (!mayClaim && !mayAssign) return Results.Forbid();

            var target = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserName == request.EngineerId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
            if (target is null) return Results.BadRequest(new { error = "Select an active AeroLink test engineer." });
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                .Select(x => x.ProgramId).SingleAsync(ct);
            if (!await identity.HasRoleAsync(target.Id, programId, ProgramRole.TestEngineer, DateTimeOffset.UtcNow, ct))
                return Results.BadRequest(new { error = $"{target.DisplayName} does not hold Test Engineer authority for this Program." });

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var items = await db.VerificationImpactItems.Where(x => x.TestChangeReviewId == id).ToListAsync(ct);
                if (items.Count == 0) return Results.BadRequest(new { error = "This test change request has no verification decisions to assign." });
                review.Assign(actor, target.UserName, now);
                var assignedItems = items.Where(x => x.State != VerificationImpactState.Resolved).ToList();
                foreach (var item in assignedItems)
                    item.AssignToEngineer(actor, target.UserName, now);
                db.SecurityAuditEvents.Add(new("TestChangeReviewAssigned", actor, review.Id.ToString(), "Success",
                    $"{review.DisplayNumber} assigned to {target.UserName}; {assignedItems.Count} open decisions assigned atomically.",
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown", now));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(new { review.Id, review.AssignedEngineerId, assignedItems = assignedItems.Count });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/assign", async (Guid id, AssignVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct, ProgramRole.TestLead))
                return Results.Forbid();
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (review is null) return Results.NotFound();
            if (review.State != TestChangeReviewState.Open)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is Open." });
            try
            {
                var now = DateTimeOffset.UtcNow;
                item.AssignToEngineer(http.UserAccount().UserName, request.EngineerId, now);
                review.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/resolve", async (Guid id, ResolveVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, BuildTestSetService testSets, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (review is null) return Results.NotFound();
            if (review.State != TestChangeReviewState.Open)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is Open." });
            var decisionRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (decisionRefusal is not null) return decisionRefusal;

            ApprovedProcedureSelection? selectedProcedure = null;
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && request.ProcedureId is not null)
                selectedProcedure = await service.FindApprovedProcedureAsync(item.ProjectId, request.ProcedureId.Value, ct);
            if (request.Outcome == VerificationImpactOutcome.ProcedureCoverageConfirmed && selectedProcedure is null)
                return Results.BadRequest(new
                {
                    error = "Coverage can only be confirmed against an approved procedure in this Project."
                });

            // A procedure can only be moved onto a requirement that is actually in this Project and still
            // active. Without this check a stale identifier from a reloaded page would attach verification to
            // a requirement that had itself been retired, which is the fault this decision exists to avoid.
            if (request.Outcome == VerificationImpactOutcome.ProcedureRetargeted)
            {
                if (request.RetargetedRequirementRevisionId is null)
                    return Results.BadRequest(new { error = "Moving a procedure requires the requirement revision it now covers." });
                var reachable = await (from revision in db.RequirementRevisions.AsNoTracking()
                                       join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                       where revision.Id == request.RetargetedRequirementRevisionId
                                             && artifact.ProjectId == item.ProjectId
                                             && revision.State == RequirementRevisionState.Active
                                       select revision.Id).AnyAsync(ct);
                if (!reachable)
                    return Results.BadRequest(new
                    {
                        error = "A procedure can only be moved onto an active requirement revision in this Project."
                    });
            }

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                // Answering a decision takes its package on, the same as concluding the assessment does. This
                // is the path a package that already has a number is worked through — its conclusion was
                // recorded when it was raised — so without this the commonest way of answering leaves the
                // package unheld, missing from My Work and unsubmittable.
                if (review.AssignedEngineerId is null) review.Assign(actor, actor, now);
                item.Resolve(actor, request.Outcome, request.Rationale, now,
                    selectedProcedure?.ProcedureId, selectedProcedure?.RevisionId,
                    request.ProcedureChangeAction, request.PreReleaseEvidenceRequired,
                    request.RetargetedRequirementRevisionId);
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    item.ResolutionRationale, actor, now));
                await service.ApplyResolvedCoverageAsync(item, now, ct);
                await service.ApplyRetargetedCoverageAsync(item, now, ct);
                review.RecordControlledContentChange(now);
                // Asking for evidence before release is saying this build must run that procedure, so it goes
                // into the build's test set — which is what the release gate now measures. Setting only the
                // flag would leave the decision recorded and unenforced, because the gate stopped reading it.
                if (item.PreReleaseEvidenceRequired && item.ResolvedProcedureRevisionId is not null)
                {
                    var discipline = await db.TestChangeReviews.AsNoTracking()
                        .Where(x => x.Id == item.TestChangeReviewId)
                        .Select(x => (TestChangeReviewDiscipline?)x.Discipline).SingleOrDefaultAsync(ct);
                    if (discipline is not null)
                    {
                        var sets = await testSets.EnsureForReleaseAsync(item.ProjectId, item.ReleaseId, ct);
                        sets.SingleOrDefault(x => x.Discipline == discipline)?.Include(actor,
                            item.ResolvedProcedureRevisionId.Value, TestSelectionReason.ChangedRequirement,
                            $"Required before release by {item.SubjectDisplayNumber}.", now);
                    }
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Raising a test change request deliberately.
        ///
        /// One is raised automatically whenever a change request is approved, so nothing ever goes unnoticed.
        /// That is not the only way work arrives: a verification engineer may decide a set of changes is best
        /// tested as one package of their own making, and until now the only way to express that was to let
        /// the automatic packages appear and then fold them together.
        ///
        /// It takes the change requests it answers for up front, because a package that covers nothing has
        /// nothing to decide and would sit in the queue looking like work.
        app.MapPost("/api/releases/{releaseId:guid}/test-change-requests", async (Guid releaseId,
            CreateTestChangeRequestRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, ProblemReportLinkService problemReports, CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (release.IsReleased) return Results.Conflict(new { error = "A released build takes no new test change requests." });
            if (!await http.HasProjectRoleAsync(db, identity, release.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            if (request.ChangeRequestIds.Length == 0)
                return Results.BadRequest(new { error = "Name the change requests this package answers for." });
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.BadRequest(new { error = "A manually raised test change request needs a title that says what it is for." });

            var changes = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => request.ChangeRequestIds.Contains(x.Id) && x.ProjectId == release.ProjectId
                    && x.TargetReleaseId == releaseId
                    && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
                .Select(x => new { x.Id, x.DisplayNumber }).ToListAsync(ct);
            if (changes.Count != request.ChangeRequestIds.Length)
                return Results.BadRequest(new
                {
                    error = "A test change request can only answer for approved change requests allocated to this build.",
                    code = "change_request_not_selectable"
                });
            // The first change the caller names is the package's base; the rest are folded in. The database
            // row order is not the caller's order, so it is restored explicitly rather than trusted.
            changes = request.ChangeRequestIds.Select(id => changes.Single(x => x.Id == id)).ToList();
            var problemReportError = await problemReports.ValidateSelectionAsync(release.ProjectId, releaseId,
                request.ProblemReportIds, ct);
            if (problemReportError is not null) return Results.BadRequest(new { error = problemReportError });

            // Already covered, by the package it was raised from or by one it was folded into. The check names
            // the holder, so an engineer is told where the work went rather than told to try again.
            //
            // Originating cover is per discipline — one change request legitimately has System, HLR and LLR
            // packages — while a folded-in claim is exclusive outright.
            foreach (var change in changes)
            {
                var origin = await db.TestChangeReviews.AsNoTracking()
                    .Where(x => x.ChangeRequestId == change.Id && x.Discipline == request.Discipline
                        && x.State != TestChangeReviewState.Superseded
                        && (x.State != TestChangeReviewState.Open || x.Outcome != TestChangeReviewOutcome.Pending))
                    .Select(x => x.DisplayNumber).FirstOrDefaultAsync(ct);
                var claimed = await db.TestChangeRequestClaims.AsNoTracking()
                    .Where(x => x.ChangeRequestId == change.Id)
                    .Join(db.TestChangeReviews.AsNoTracking(), claim => claim.TestChangeReviewId, review => review.Id, (_, review) => review.DisplayNumber)
                    .FirstOrDefaultAsync(ct);
                // Two different situations, and one sentence could not tell them apart once an unassessed
                // package took its name from the change it was raised from: being covered by somebody else's
                // package is not the same as already having an assessment of your own, and the second read
                // as "X is already covered by X".
                if (!string.IsNullOrEmpty(origin))
                    return Results.Conflict(new { error = $"{change.DisplayNumber} already has a {request.Discipline} test assessment." });
                if (!string.IsNullOrEmpty(claimed))
                    return Results.Conflict(new { error = $"{change.DisplayNumber} is already covered by {claimed}." });
            }
            // Raising a package by hand is itself the conclusion that test work is required (DEC-095), so an
            // unassessed automatic review of one of the selected changes is not a rival: the first change's
            // review becomes this package, and the others are superseded rather than duplicated. History keeps
            // them; nothing is deleted.
            var pendingAutomatic = await db.TestChangeReviews
                .Where(x => request.ChangeRequestIds.Contains(x.ChangeRequestId) && x.Discipline == request.Discipline
                    && x.State == TestChangeReviewState.Open && x.Outcome == TestChangeReviewOutcome.Pending)
                .ToListAsync(ct);

            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount().UserName;
                var first = changes[0];
                // Raising one by hand is itself the conclusion that test work is required, so it is numbered
                // immediately rather than waiting to be assessed by the person who just decided it. When the
                // change already carries an unassessed automatic review, that review is the package — one
                // row, one ChangeRequestId, one Revision, one answer.
                var review = pendingAutomatic.SingleOrDefault(x => x.ChangeRequestId == first.Id);
                if (review is null)
                {
                    review = new TestChangeReview(release.ProjectId, releaseId, first.Id, request.Discipline,
                        first.DisplayNumber, now);
                    db.TestChangeReviews.Add(review);
                }
                review.RecordTestChangeRequired(actor, now);
                review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, request.Discipline, ct), now);
                foreach (var extra in changes.Skip(1))
                    review.IncludeChangeRequest(actor, extra.Id, extra.DisplayNumber, now);
                review.WriteCase(actor, request.Title, request.Problem, request.Analysis, request.Solution, now,
                    request.ProblemRich, request.AnalysisRich, request.SolutionRich);
                // DEC-102: raising the package is itself taking it on. The engineer who built it holds it,
                // so it appears in My Work and can be worked without a meaningless "Take it on" step.
                review.Assign(actor, actor, now);
                foreach (var automatic in pendingAutomatic.Where(x => x.Id != review.Id))
                    automatic.Supersede(review.Id,
                        $"{review.DisplayNumber} was raised manually and concludes that test work is required; it supersedes this unassessed automatic review.",
                        now);
                // The folded sources' verification work must stay actionable from the surviving package.
                // Moving the items — identity, attribution, decision and history preserved — is what keeps
                // "this change is covered by this TCR" true instead of stranding work behind a superseded
                // assessment that the queue no longer shows.
                var supersededIds = pendingAutomatic.Where(x => x.Id != review.Id).Select(x => x.Id).ToList();
                if (supersededIds.Count != 0)
                {
                    var moved = await db.VerificationImpactItems
                        .Where(x => supersededIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
                    foreach (var item in moved) item.MoveToReview(review.Id, now);
                }
                await problemReports.LinkTestChangeRequestAsync(review.Id, request.ProblemReportIds, actor, now, ct);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/test-change-reviews/{review.Id}", new
                {
                    review.Id,
                    review.DisplayNumber,
                    discipline = review.Discipline.ToString(),
                    state = review.State.ToString(),
                    covered = review.CoveredChangeRequestIds,
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// Editing the engineering case of an open test change request.
        ///
        /// A deliberately raised package is authored with its case up front, and stays correctable while it
        /// is being worked — the same window a change request's own draft edit has. Once a reviewer is
        /// holding it, the case is fixed, because the approval has to be provably of the content the
        /// reviewer read.
        app.MapPost("/api/test-change-reviews/{id:guid}/case", async (Guid id,
            WriteTestChangeRequestCaseRequest request, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            var refusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (refusal is not null) return refusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before editing its case.",
                    code = "stale_version"
                });
            try
            {
                review.WriteCase(http.UserAccount().UserName, request.Title, request.Problem, request.Analysis,
                    request.Solution, DateTimeOffset.UtcNow, request.ProblemRich, request.AnalysisRich,
                    request.SolutionRich);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    review.Title,
                    review.Problem,
                    review.Analysis,
                    review.Solution,
                    review.ProblemRich,
                    review.AnalysisRich,
                    review.SolutionRich
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// A truthful read model for the manual-raise source picker.
        ///
        /// The mutation endpoint remains authoritative; this projection exists so the client can explain
        /// before submission why an approved change cannot be added — already assessed, already claimed, or
        /// not eligible for this build. No cross-project information is exposed.
        app.MapGet("/api/releases/{releaseId:guid}/test-change-request-sources", async (Guid releaseId,
            TestChangeReviewDiscipline discipline, HttpContext http, AeroLinkDbContext db,
            CancellationToken ct) =>
        {
            var release = await db.Releases.AsNoTracking().Where(x => x.Id == releaseId)
                .Select(x => new { x.ProjectId, x.IsReleased }).SingleOrDefaultAsync(ct);
            if (release is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, release.ProjectId, ct)) return Results.Forbid();

            var changes = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.ProjectId == release.ProjectId && x.TargetReleaseId == releaseId
                    && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
                .Select(x => new { x.Id, x.DisplayNumber, x.Title, x.State }).ToListAsync(ct);
            var ids = changes.Select(x => x.Id).ToList();

            var concluded = await db.TestChangeReviews.AsNoTracking()
                .Where(x => ids.Contains(x.ChangeRequestId) && x.Discipline == discipline
                    && x.State != TestChangeReviewState.Superseded
                    && (x.State != TestChangeReviewState.Open || x.Outcome != TestChangeReviewOutcome.Pending))
                .GroupBy(x => x.ChangeRequestId)
                .ToDictionaryAsync(group => group.Key, group => group.First().DisplayNumber, ct);
            var claims = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => ids.Contains(x.ChangeRequestId))
                .Join(db.TestChangeReviews.AsNoTracking(), claim => claim.TestChangeReviewId,
                    review => review.Id, (claim, review) => new { claim.ChangeRequestId, review.DisplayNumber })
                .ToListAsync(ct);
            var claimedBy = claims.GroupBy(x => x.ChangeRequestId)
                .ToDictionary(group => group.Key, group => group.First().DisplayNumber);

            return Results.Ok(changes.OrderBy(x => x.DisplayNumber).Select(change =>
            {
                string? reason = concluded.TryGetValue(change.Id, out var origin)
                    ? $"Already has a {discipline} test assessment."
                    : claimedBy.TryGetValue(change.Id, out var holder)
                        ? $"Already covered by {holder}."
                        : null;
                return new
                {
                    changeRequestId = change.Id,
                    displayNumber = change.DisplayNumber,
                    title = change.Title,
                    state = change.State.ToString(),
                    selectable = reason is null,
                    reason
                };
            }));
        });

        /// Folding another change request's test work into this package, and taking it back out.
        ///
        /// Whole change requests, because an engineer takes on a change's test work or they do not; splitting
        /// one across two packages would leave "is this change covered?" with a partial answer that neither
        /// package could give. A change already claimed elsewhere is refused by name rather than by a unique
        /// index violation, so the engineer is told which package has it instead of being told to try again.
        app.MapPost("/api/test-change-reviews/{id:guid}/change-requests", async (Guid id, IncludeChangeRequestRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var foldRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (foldRefusal is not null) return foldRefusal;

            var change = await db.SystemChangeRequests.AsNoTracking()
                .Where(x => x.Id == request.ChangeRequestId && x.ProjectId == review.ProjectId)
                .Select(x => new { x.Id, x.DisplayNumber, x.TargetReleaseId }).SingleOrDefaultAsync(ct);
            if (change is null) return Results.NotFound(new { error = "That change request is not in this Project." });
            // A package governs one build's test work. Folding in a change allocated to a different build
            // would put its procedures behind the wrong release gate.
            if (change.TargetReleaseId != review.ReleaseId)
                return Results.BadRequest(new { error = $"{change.DisplayNumber} is allocated to a different build." });

            var claimedBy = await db.TestChangeRequestClaims.AsNoTracking()
                .Where(x => x.ChangeRequestId == request.ChangeRequestId)
                .Select(x => x.TestChangeReviewId).FirstOrDefaultAsync(ct);
            if (claimedBy != Guid.Empty && claimedBy != id)
            {
                var holder = await db.TestChangeReviews.AsNoTracking().Where(x => x.Id == claimedBy)
                    .Select(x => x.DisplayNumber).SingleOrDefaultAsync(ct);
                return Results.Conflict(new { error = $"{change.DisplayNumber} is already covered by {holder}." });
            }
            // A change whose assessment has already been concluded is a real package of decisions, not a
            // pending automatic review. Folding it in would strand those decisions; the engineer should work
            // them where they were made. Checked after claims so a holder is named when one exists.
            var concluded = await db.TestChangeReviews.AsNoTracking()
                .Where(x => x.ChangeRequestId == request.ChangeRequestId && x.Discipline == review.Discipline
                    && x.State != TestChangeReviewState.Superseded
                    && (x.State != TestChangeReviewState.Open || x.Outcome != TestChangeReviewOutcome.Pending))
                .Select(x => x.DisplayNumber).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(concluded))
                return Results.Conflict(new { error = $"{change.DisplayNumber} already has a {review.Discipline} test assessment." });
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its source set.",
                    code = "stale_version"
                });

            try
            {
                var now = DateTimeOffset.UtcNow;
                review.IncludeChangeRequest(http.UserAccount().UserName, change.Id, change.DisplayNumber, now);
                // Taking a change's test work into this package takes its pending automatic assessment with
                // it: the assessment is superseded as history and its items move here so the package's
                // workspace can see and settle them.
                var automatic = await db.TestChangeReviews
                    .Where(x => x.ChangeRequestId == request.ChangeRequestId && x.Discipline == review.Discipline
                        && x.State == TestChangeReviewState.Open && x.Outcome == TestChangeReviewOutcome.Pending)
                    .ToListAsync(ct);
                foreach (var pending in automatic)
                    pending.Supersede(review.Id,
                        $"{review.DisplayNumber} took over this change's test work when it was folded in; this unassessed automatic review is superseded as history.",
                        now);
                var automaticIds = automatic.Select(x => x.Id).ToList();
                if (automaticIds.Count != 0)
                {
                    var items = await db.VerificationImpactItems
                        .Where(x => automaticIds.Contains(x.TestChangeReviewId)).ToListAsync(ct);
                    foreach (var item in items) item.MoveToReview(review.Id, now);
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/test-change-reviews/{id:guid}/change-requests/{changeRequestId:guid}", async (Guid id,
            Guid changeRequestId, long? expectedVersion, HttpContext http, AeroLinkDbContext db,
            IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.AdditionalSources).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
            var unfoldRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (unfoldRefusal is not null) return unfoldRefusal;
            if (expectedVersion is not null && review.Version != expectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before changing its source set.",
                    code = "stale_version"
                });
            try
            {
                var now = DateTimeOffset.UtcNow;
                review.ExcludeChangeRequest(changeRequestId, now);
                // The change is no longer claimed by this package, so the work this package moved in from its
                // automatic assessment must go back to an actionable assessment of its own — a fresh Open
                // record at the next review revision. The superseded assessment stays as history.
                var change = await db.SystemChangeRequests.AsNoTracking()
                    .Where(x => x.Id == changeRequestId && x.ProjectId == review.ProjectId)
                    .Select(x => new { x.Id, x.DisplayNumber }).SingleOrDefaultAsync(ct);
                if (change is not null)
                {
                    var stranded = await db.VerificationImpactItems
                        .Where(x => x.ChangeRequestId == changeRequestId && x.TestChangeReviewId == id)
                        .ToListAsync(ct);
                    // A fresh current Open/Pending assessment is restored unconditionally — even when the
                    // source has no impact items — so the change is never left without an actionable
                    // assessment and can be selected or folded again. The superseded assessment remains history.
                    var nextRevision = await db.TestChangeReviews
                        .Where(x => x.ChangeRequestId == changeRequestId && x.Discipline == review.Discipline)
                        .Select(x => (int?)x.Revision).MaxAsync(ct) ?? -1;
                    var fresh = new TestChangeReview(review.ProjectId, review.ReleaseId, change.Id,
                        review.Discipline, change.DisplayNumber, now, revision: nextRevision + 1);
                    db.TestChangeReviews.Add(fresh);
                    foreach (var item in stranded) item.MoveToReview(fresh.Id, now);
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, review.DisplayNumber, covered = review.CoveredChangeRequestIds });
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/submit", async (Guid id, SubmitTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ProcedureChanges).Include(x => x.ReviewCycles)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            var submitRefusal = await RefuseUnlessAuthoredBy(review, http, db, identity, ct);
            if (submitRefusal is not null) return submitRefusal;
            if (request.ExpectedVersion is not null && review.Version != request.ExpectedVersion)
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh before submitting it for review.",
                    code = "stale_version"
                });
            try
            {
                // Newly authored packages carry a full case: Title, Problem, Analysis and Solution are what
                // the reviewer is asked to judge. Packages that predate case authoring — or that were raised
                // automatically and never written up — remain submittable as history; this gate applies only
                // to work that has begun to state its case.
                if (!string.IsNullOrWhiteSpace(review.Title)
                    && (string.IsNullOrWhiteSpace(review.Problem) || string.IsNullOrWhiteSpace(review.Analysis)
                        || string.IsNullOrWhiteSpace(review.Solution)))
                    return Results.BadRequest(new { error = "Complete the test change request case (Problem, Analysis and Solution) before sending it for review." });
                // A package concluding that procedure work is required, and then naming none, asks an approver
                // to approve nothing. The workspace already tells the engineer a package is unfinished until it
                // says what the work is; this is that sentence enforced.
                if (review.Outcome == TestChangeReviewOutcome.ChangeRequired && review.ProcedureChanges.Count == 0)
                    return Results.BadRequest(new { error = $"{review.DisplayNumber} concluded that procedure work is required but names none. Add the procedure decisions it carries before sending it for review." });
                var allResolved = await db.VerificationImpactItems
                    .Where(x => x.TestChangeReviewId == id)
                    .AllAsync(x => x.State == VerificationImpactState.Resolved, ct);
                // The project's recorded procedure for this discipline decides the stages. Where none is
                // recorded the chosen approver stands alone, exactly as before — a rule nobody has written
                // down must not become a rule that blocks work.
                var workflow = await WorkflowEndpoints.ActiveSpecificationAsync(db, review.ProjectId, review.Discipline, ct);
                List<ApproverSelection> selections;
                if (workflow is null)
                {
                    if (string.IsNullOrWhiteSpace(request.ApproverId))
                        return Results.BadRequest(new { error = "Select an independent test change request approver." });
                    var approver = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
                        x.UserName == request.ApproverId.Trim().ToLowerInvariant() && x.State == AccountState.Active, ct);
                    if (approver is null)
                        return Results.BadRequest(new { error = "Select an active AeroLink test change request approver." });
                    var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                        .Select(x => x.ProgramId).SingleAsync(ct);
                    if (!await identity.HasRoleAsync(approver.Id, programId, ProgramRole.Approver, DateTimeOffset.UtcNow, ct))
                        return Results.BadRequest(new { error = $"{approver.DisplayName} does not hold Approver authority for this Program." });
                    selections = [new ApproverSelection(approver.UserName, approver.DisplayName, ProgramRole.Approver)];
                }
                else
                {
                    var requested = request.Approvers ?? [];
                    if (requested.Count != workflow.Stages.Count)
                        return Results.BadRequest(new
                        {
                            error = $"{workflow.Name} v{workflow.Version} requires {workflow.Stages.Count} approver{(workflow.Stages.Count == 1 ? "" : "s")}, one for each stage: " +
                                string.Join(", ", workflow.Stages.Select(x => x.Name)) + "."
                        });
                    var ids = requested.Select(x => x.UserId.Trim().ToLowerInvariant()).ToList();
                    var accounts = await db.UserAccounts.AsNoTracking()
                        .Where(x => ids.Contains(x.UserName) && x.State == AccountState.Active)
                        .Select(x => new { x.Id, x.UserName, x.DisplayName }).ToListAsync(ct);
                    if (accounts.Count != ids.Count)
                        return Results.BadRequest(new { error = "Every stage approver must be an active AeroLink user." });
                    var directory = accounts.ToDictionary(x => x.UserName, StringComparer.OrdinalIgnoreCase);
                    selections = new List<ApproverSelection>();
                    for (var index = 0; index < requested.Count; index++)
                    {
                        var chosen = requested[index];
                        var account = directory[chosen.UserId.Trim().ToLowerInvariant()];
                        var role = await WorkflowEndpoints.StageAuthorityAsync(db, review.ProjectId, account.Id,
                            workflow.Stages[index].RequiredRole, ct);
                        selections.Add(new ApproverSelection(account.UserName, account.DisplayName, role));
                    }
                }
                var now = DateTimeOffset.UtcNow;
                var problemReportIds = await db.ProblemReportLinks.AsNoTracking()
                    .Where(x => x.ArtifactType == "TestChangeRequest" && x.ArtifactId == review.Id)
                    .Select(x => x.ProblemReportId).ToListAsync(ct);
                var impactItems = await db.VerificationImpactItems.AsNoTracking()
                    .Where(x => x.TestChangeReviewId == review.Id).ToListAsync(ct);
                var impactDecisions = impactItems.Select(x => new VerificationImpactSnapshot(
                    x.Id, x.ChangeRequestId, x.Trigger, x.RequirementChangeId, x.RequirementRevisionId,
                    x.ProcedureId, x.SubjectDisplayNumber, x.Outcome, x.ProcedureChangeAction,
                    x.ResolutionRationale, x.ResolvedProcedureId, x.ResolvedProcedureRevisionId,
                    x.RetargetedRequirementRevisionId, x.PreReleaseEvidenceRequired)).ToList();
                var cycle = review.SubmitForReview(http.UserAccount().UserName, selections, allResolved, now,
                    workflow?.Mode ?? ReviewMode.Sequential, workflow, problemReportIds, impactDecisions);
                foreach (var step in cycle.Steps.Where(x => x.State == ApprovalStepState.Active))
                    db.UserNotifications.Add(new(review.ProjectId, step.ApproverId, "TestChangeRequestApprovalRequested",
                        $"Review {review.DisplayNumber}", $"{http.UserAccount().DisplayName} selected you to approve this test change request.",
                        "verification", review.Id, now));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    state = review.State.ToString(),
                    cycleId = cycle.Id,
                    sequence = cycle.Sequence,
                    stageCount = cycle.Steps.Count
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                // A governed mutation committed between this request's load and save (for example a Problem
                // Report link or an impact decision). The whole unit of work is rolled back: no cycle, no
                // notification, no signature is persisted.
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh it before submitting it for review.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/approve", async (Guid id, ApproveTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct))
                return Results.Forbid();
            try
            {
                var now = DateTimeOffset.UtcNow;
                var actor = http.UserAccount();
                var cycle = review.ActiveReviewCycle
                    ?? throw new DomainException("This test change request has no active review.");
                var snapshotHash = cycle.SnapshotHash;
                var activeBefore = cycle.Steps.Where(x => x.State == ApprovalStepState.Active)
                    .Select(x => x.ApproverId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                review.ApproveActiveStage(actor.UserName, request.Rationale, now);
                var programId = await db.Projects.AsNoTracking().Where(x => x.Id == review.ProjectId)
                    .Select(x => x.ProgramId).SingleAsync(ct);
                var activated = review.ActiveReviewCycle?.Steps
                    .Where(x => x.State == ApprovalStepState.Active && !activeBefore.Contains(x.ApproverId))
                    .ToList() ?? [];
                foreach (var step in activated)
                    db.UserNotifications.Add(new(review.ProjectId, step.ApproverId, "ReviewActivated",
                        $"Review {review.DisplayNumber}",
                        $"The prior stage is complete. You are now authorized to review {review.DisplayNumber}.",
                        "verification", review.Id, now));
                db.ElectronicSignatures.Add(new(actor.Id, actor.UserName, actor.DisplayName, programId,
                    "TestChangeRequest", review.Id, review.DisplayNumber, "Approve", request.Rationale,
                    snapshotHash, http.Connection.RemoteIpAddress?.ToString() ?? "local", now));
                await db.SaveChangesAsync(ct);
                return Results.Ok(new
                {
                    review.Id,
                    state = review.State.ToString(),
                    cycleState = review.ActiveReviewCycle?.State.ToString()
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/test-change-reviews/{id:guid}/return", async (Guid id, ReturnTestChangeReviewRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var review = await db.TestChangeReviews.Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
                .SingleOrDefaultAsync(x => x.Id == id, ct);
            if (review is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build test change reviews are read-only." });
            if (!await http.HasProjectAccessAsync(db, review.ProjectId, ct))
                return Results.Forbid();
            try
            {
                review.RequestChanges(http.UserAccount().UserName, request.Rationale, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { review.Id, state = review.State.ToString() });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/verification-impact/{id:guid}/reopen", async (Guid id, ReopenVerificationImpactRequest request,
            HttpContext http, AeroLinkDbContext db, IdentityService identity, VerificationImpactService service, CancellationToken ct) =>
        {
            var item = await db.VerificationImpactItems.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (await db.Releases.AnyAsync(x => x.Id == item.ReleaseId && x.IsReleased, ct))
                return Results.Conflict(new { error = "Released software-build verification records are read-only." });
            if (!await http.HasProjectRoleAsync(db, identity, item.ProjectId, ct,
                    ProgramRole.TestEngineer, ProgramRole.TestLead))
                return Results.Forbid();
            var reopenReview = await db.TestChangeReviews.SingleOrDefaultAsync(x => x.Id == item.TestChangeReviewId, ct);
            if (reopenReview is null) return Results.NotFound();
            if (reopenReview.State != TestChangeReviewState.Open)
                return Results.Conflict(new { error = "Verification decisions can be changed only while the test change request is Open." });
            var reopenRefusal = await RefuseUnlessAuthoredBy(reopenReview, http, db, identity, ct);
            if (reopenRefusal is not null) return reopenRefusal;
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                    item.Id, VerificationImpactHistoryAction.Reopened, item.Outcome,
                    item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                    request.Rationale, actor, now));
                await service.ReopenResolvedCoverageAsync(item, request.Rationale, now, ct);
                item.Reopen(actor, request.Rationale, now);
                reopenReview.RecordControlledContentChange(now);
                await db.SaveChangesAsync(ct);
                return Results.Ok((await MapAsync([item], db, ct)).Single());
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new
                {
                    error = "This test change request changed after it was opened. Refresh and try again.",
                    code = "stale_version"
                });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    private static async Task<IReadOnlyList<object>> MapAsync(
        IReadOnlyCollection<VerificationImpactItem> items, AeroLinkDbContext db, CancellationToken ct)
    {
        var revisionIdsForSubjects = items.Where(x => x.RequirementRevisionId is not null)
            .Select(x => x.RequirementRevisionId!.Value).Distinct().ToList();
        var revisionSubjects = await db.RequirementRevisions.AsNoTracking()
            .Where(x => revisionIdsForSubjects.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Statement, ct);
        var changeIdsForSubjects = items.Where(x => x.RequirementChangeId is not null)
            .Select(x => x.RequirementChangeId!.Value).Distinct().ToList();
        var changeSubjects = await db.RequirementChanges.AsNoTracking()
            .Where(x => changeIdsForSubjects.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Statement, ct);
        var exactIds = items.Where(x => x.ResolvedProcedureRevisionId is not null)
            .Select(x => x.ResolvedProcedureRevisionId!.Value).Distinct().ToList();

        // Which resolved items still owe the evidence they designated as a prerequisite to releasing.
        //
        // `BlocksBaselineApproval` answers one question — is this decision still outstanding — and a reader
        // took it for the whole answer. An item resolved with "evidence required before release" reported
        // that it blocked nothing, and the workspace queue, which filters on exactly that, stopped showing
        // it. The release could not ship until its evidence arrived, and the one place a verification
        // engineer looks for outstanding work had quietly dropped it.
        //
        // Computed the same way the release readiness gate computes it, against the latest run for the
        // procedure revision the item resolved to, so the queue and the gate cannot disagree.
        var evidenceOwedIds = new HashSet<Guid>();
        var pendingEvidence = items
            .Where(x => x.PreReleaseEvidenceRequired && x.ResolvedProcedureRevisionId is not null)
            .ToList();
        if (pendingEvidence.Count != 0)
        {
            var revisionIds = pendingEvidence.Select(x => x.ResolvedProcedureRevisionId!.Value).Distinct().ToList();
            // Materialized before ordering: SQLite cannot translate an ORDER BY over a DateTimeOffset.
            var runs = (await db.TestExecutions.AsNoTracking()
                    .Where(x => revisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct))
                .GroupBy(x => x.ProcedureRevisionId)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).First().Id);
            var runIds = runs.Values.ToList();
            var evidenced = (await db.TestExecutionEvidence.AsNoTracking()
                .Where(x => runIds.Contains(x.TestExecutionId)).Select(x => x.TestExecutionId).Distinct().ToListAsync(ct))
                .ToHashSet();
            foreach (var item in pendingEvidence)
                if (!runs.TryGetValue(item.ResolvedProcedureRevisionId!.Value, out var runId) || !evidenced.Contains(runId))
                    evidenceOwedIds.Add(item.Id);
        }
        var exactRows = exactIds.Count == 0
            ? []
            : await LoadProcedureRowsAsync(db, exactIds, [], ct);
        var exact = exactRows.ToDictionary(x => x.RevisionId, ToSelection);

        var legacyIds = items.Where(x => x.ResolvedProcedureRevisionId is null && x.ResolvedProcedureId is not null)
            .Select(x => x.ResolvedProcedureId!.Value).Distinct().ToList();
        var legacyRows = legacyIds.Count == 0
            ? []
            : (await LoadProcedureRowsAsync(db, [], legacyIds, ct))
                .Where(x => x.State == TestProcedureState.Approved).ToList();
        var legacy = legacyRows.GroupBy(x => x.ProcedureId).ToDictionary(x => x.Key,
            x => ToSelection(x.OrderByDescending(y => y.Revision).First()));

        var itemIds = items.Select(x => x.Id).ToList();
        var historyRows = await db.VerificationImpactDecisionHistory.AsNoTracking()
            .Where(x => itemIds.Contains(x.VerificationImpactItemId))
            .ToListAsync(ct);
        var history = historyRows.OrderBy(x => x.OccurredAt).ToList();

        return items.Select(x =>
        {
            ApprovedProcedureSelection? procedure = null;
            if (x.ResolvedProcedureRevisionId is not null)
                exact.TryGetValue(x.ResolvedProcedureRevisionId.Value, out procedure);
            else if (x.ResolvedProcedureId is not null)
                legacy.TryGetValue(x.ResolvedProcedureId.Value, out procedure);
            return (object)new
            {
                x.Id,
                x.ReleaseId,
                x.ChangeRequestId,
                x.TestChangeReviewId,
                trigger = x.Trigger.ToString(),
                state = x.State.ToString(),
                x.SubjectDisplayNumber,
                subjectStatement = x.RequirementRevisionId is not null && revisionSubjects.TryGetValue(x.RequirementRevisionId.Value, out var revisionStatement)
                    ? revisionStatement
                    : x.RequirementChangeId is not null && changeSubjects.TryGetValue(x.RequirementChangeId.Value, out var changeStatement)
                        ? changeStatement : "",
                x.DeclaredVerificationMethod,
                x.RequirementChangeId,
                x.RequirementRevisionId,
                x.ProcedureId,
                x.AssignedEngineerId,
                x.AssignedByLeadId,
                x.AssignedAt,
                outcome = x.Outcome?.ToString(),
                procedureChangeAction = x.ProcedureChangeAction?.ToString(),
                x.PreReleaseEvidenceRequired,
                x.ResolvedProcedureId,
                x.ResolvedProcedureRevisionId,
                resolvedProcedure = procedure is null ? null : new
                {
                    id = procedure.ProcedureId,
                    revisionId = procedure.RevisionId,
                    procedure.DisplayNumber,
                    procedure.Title,
                    procedure.Level,
                    procedure.State,
                    configuration = new { x.RequirementRevisionId, procedureRevisionId = procedure.RevisionId }
                },
                x.ResolutionRationale,
                x.ResolvedBy,
                x.ResolvedAt,
                x.RaisedAt,
                x.RetargetedRequirementRevisionId,
                x.BlocksBaselineApproval,
                // Resolved, and still holding the release until its designated evidence is captured.
                awaitsPreReleaseEvidence = evidenceOwedIds.Contains(x.Id),
                // What a reader actually wants to know: is this item holding the build, for any reason.
                holdsRelease = x.BlocksBaselineApproval || evidenceOwedIds.Contains(x.Id),
                decisionHistory = history.Where(h => h.VerificationImpactItemId == x.Id).Select(h => new
                {
                    h.Id,
                    action = h.Action.ToString(),
                    outcome = h.Outcome?.ToString(),
                    h.ProcedureId,
                    h.ProcedureRevisionId,
                    h.Rationale,
                    actor = h.ActorId,
                    h.OccurredAt
                })
            };
        }).ToList();
    }

    private static async Task<List<ProcedureRow>> LoadProcedureRowsAsync(
        AeroLinkDbContext db, IReadOnlyCollection<Guid> revisionIds,
        IReadOnlyCollection<Guid> procedureIds, CancellationToken ct)
    {
        var rows = await (
            from revision in db.TestProcedureRevisions.AsNoTracking()
            join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
            where revisionIds.Contains(revision.Id) || procedureIds.Contains(procedure.Id)
            select new
            {
                ProcedureId = procedure.Id,
                RevisionId = revision.Id,
                revision.Revision,
                procedure.BaseNumber,
                procedure.Title,
                procedure.Level,
                revision.State
            }).ToListAsync(ct);
        return rows.Select(x => new ProcedureRow(x.ProcedureId, x.RevisionId, x.Revision, x.BaseNumber,
            x.Title, x.Level.ToString(), x.State)).ToList();
    }

    /// <summary>
    /// The gate every write to a test change request's procedure decisions passes.
    ///
    /// Extracted rather than repeated because the three rules travel together and are easy to get partly
    /// right: a released build is read-only, the actor holds test authority, and an assigned package is the
    /// assignee's to edit unless a lead is doing it.
    /// </summary>
    private static async Task<IResult?> RefuseUnlessAuthoredBy(TestChangeReview review, HttpContext http,
        AeroLinkDbContext db, IdentityService identity, CancellationToken ct)
    {
        if (await db.Releases.AnyAsync(x => x.Id == review.ReleaseId && x.IsReleased, ct))
            return Results.Conflict(new { error = "Released software-build test change requests are read-only." });
        if (!await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestEngineer, ProgramRole.TestLead))
            return Results.Forbid();
        var actor = http.UserAccount().UserName;
        if (review.AssignedEngineerId is not null
            && !string.Equals(review.AssignedEngineerId, actor, StringComparison.OrdinalIgnoreCase)
            && !await http.HasProjectRoleAsync(db, identity, review.ProjectId, ct, ProgramRole.TestLead))
            return Results.Forbid();
        return null;
    }

    /// <summary>
    /// The newest review cycle by Sequence that was not cancelled. Explicit and deterministic: it never
    /// depends on EF navigation order, and a cancelled cycle is a dead end that must not drive capabilities.
    /// </summary>
    private static ReviewCycle? LatestNonCancelledCycle(TestChangeReview review) =>
        review.ReviewCycles
            .Where(x => x.State != ReviewCycleState.Cancelled)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefault();

    private static IReadOnlyList<Guid> DrivingRequirements(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static ApprovedProcedureSelection ToSelection(ProcedureRow row) =>
        new(row.ProcedureId, row.RevisionId, row.Revision, $"{row.BaseNumber}.{row.Revision:D2}",
            row.Title, row.Level, row.State.ToString());

    private sealed record ProcedureRow(Guid ProcedureId, Guid RevisionId, int Revision, string BaseNumber,
        string Title, string Level, TestProcedureState State);
}
