using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// Verification: procedures, executions, evidence, and the trace links that connect a
/// requirement to whatever demonstrates it.
///
/// AeroLink records a determination somebody made. It never runs a test and never decides an outcome.
/// </summary>
public static class VerificationEndpoints
{
    public static void MapVerificationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/traceability/{baselineId:guid}/download", async (Guid baselineId,string? format,HttpContext http,AeroLinkDbContext db,ControlledOutputGenerator generator,CancellationToken ct) =>
        {
            var projectId=await db.CandidateBaselines.Where(x=>x.Id==baselineId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var output=await generator.GenerateTraceabilityAsync(baselineId,format??"pdf",ct);return output is null?Results.NotFound():Results.File(output.Content,output.ContentType,output.FileName);
        });

        app.MapPost("/api/evidence", async (HttpRequest http, AeroLinkDbContext db, IdentityService identity, EvidenceFileStore store, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) return Results.BadRequest(new { error = "Use multipart form data." }); var form = await http.ReadFormAsync(ct); var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "Select a non-empty evidence file." }); if (!Guid.TryParse(form["projectId"], out var projectId) || !await db.Projects.AnyAsync(x => x.Id == projectId, ct)) return Results.BadRequest(new { error = "A valid project is required." }); var uploadedBy = http.HttpContext.UserAccount().UserName;
            if (!await http.HttpContext.HasProjectRoleAsync(db, identity, projectId, ct, ProgramRole.TestEngineer)) return Results.Forbid();
            try { await using var stream = file.OpenReadStream(); var stored = await store.StoreAsync(stream, file.FileName, file.ContentType, ct); var evidence = new EvidenceRecord(projectId, stored.OriginalFileName, stored.ContentType, stored.Size, stored.Sha256, stored.StorageKey, uploadedBy, DateTimeOffset.UtcNow); db.EvidenceRecords.Add(evidence); await db.SaveChangesAsync(ct); return Results.Created($"/api/evidence/{evidence.Id}", new { evidence.Id, evidence.OriginalFileName, evidence.ContentType, evidence.Size, evidence.Sha256, evidence.UploadedBy, evidence.UploadedAt }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();

        app.MapPost("/api/test-executions/{executionId:guid}/evidence/{evidenceId:guid}", async (Guid executionId, Guid evidenceId, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var execution = await db.TestExecutions.AsNoTracking().Where(x => x.Id == executionId).Select(x => new { x.ProjectId, x.SoftwareBuildId }).SingleOrDefaultAsync(ct);
            var evidenceProject = await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == evidenceId).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (execution is null || evidenceProject is null) return Results.NotFound();
            if (execution.ProjectId != evidenceProject) return Results.BadRequest(new { error = "Evidence and execution must belong to the same project." });
            if (!await http.HasProjectRoleAsync(db, identity, execution.ProjectId, ct, ProgramRole.TestEngineer)) return Results.Forbid();
            if (execution.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == execution.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (await db.TestExecutionEvidence.AnyAsync(x => x.TestExecutionId == executionId && x.EvidenceId == evidenceId, ct)) return Results.Conflict(new { error = "Evidence is already linked." }); db.TestExecutionEvidence.Add(new TestExecutionEvidence(executionId, evidenceId)); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapGet("/api/evidence/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, EvidenceFileStore store, CancellationToken ct) =>
        {
            var projectId = await db.EvidenceRecords.AsNoTracking().Where(x => x.Id == id)
                .Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var evidence = await db.EvidenceRecords.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.File(store.OpenRead(evidence.StorageKey), evidence.ContentType, evidence.OriginalFileName,
                enableRangeProcessing: true);
        });

        app.MapPost("/api/trace-links", async (CreateTraceLinkRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.Engineer, ProgramRole.ConfigurationManager)) return Results.Forbid();
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.Id == request.SourceRevisionId || x.Id == request.TargetRevisionId)
                                   join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                   select new { revision.Id, artifact.ProjectId }).ToListAsync(ct);
            if (revisions.Count != 2) return Results.BadRequest(new { error = "Both exact requirement revisions must exist." });
            if (revisions.Any(x => x.ProjectId != request.ProjectId)) return Results.BadRequest(new { error = "Both revisions must belong to the selected project." });
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (await (from member in db.BaselineRequirements.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId))
                       join campaign in db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == request.ProjectId && x.State == ReleaseCampaignState.InReview) on member.BaselineId equals campaign.BaselineId
                       select member.Id).AnyAsync(ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            try { var link = new RequirementTraceLink(request.ProjectId, request.SourceRevisionId, request.TargetRevisionId, request.Type, request.Rationale, DateTimeOffset.UtcNow); db.RequirementTraces.Add(link); await db.SaveChangesAsync(ct); return Results.Created($"/api/trace-links/{link.Id}", new { link.Id }); }
            catch (Exception ex) when (ex is DomainException or DbUpdateException) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/trace-links/{id:guid}", async (Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var link = await db.RequirementTraces.SingleOrDefaultAsync(x => x.Id == id, ct); if (link is null) return Results.NotFound();
            if(!await http.HasProjectRoleAsync(db,identity,link.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
            var revisionIds = new[] { link.SourceRevisionId, link.TargetRevisionId };
            if(await db.BaselineRequirements.AsNoTracking().AnyAsync(x=>revisionIds.Contains(x.RevisionId),ct))
                return Results.Conflict(new{error="Trace links involving a baselined requirement revision are controlled history and cannot be deleted. Create the corrected revision and superseding link instead.",code="controlled_trace_history"});
            db.RequirementTraces.Remove(link); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        app.MapGet("/api/traceability", async (Guid projectId, Guid? baselineId, string? search, int page, int pageSize, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            page = Math.Max(1, page == 0 ? 1 : page); pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
            if (baselineId is null) baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null).OrderByDescending(x => x.FrozenAt).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (baselineId is null) return Results.Ok(new { page, pageSize, totalCount = 0, items = Array.Empty<object>() });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId.Value, ct);
            var effectiveProcedureRevisionIds = procedureEffectivity?.RevisionIds ?? [];
            var source = from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                         join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                         join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                         select new { artifact, revision };
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct); var selected = await source.OrderBy(x => x.artifact.BaseNumber).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var selectedIds = selected.Select(x => x.revision.Id).ToList(); var links = await db.RequirementTraces.AsNoTracking().Where(x => selectedIds.Contains(x.SourceRevisionId) || selectedIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
            var relatedIds = links.SelectMany(x => new[] { x.SourceRevisionId, x.TargetRevisionId }).Distinct().ToList();
            var related = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => relatedIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id select new { revision.Id, artifact.BaseNumber, revision.Revision, level = artifact.Level.ToString() }).ToDictionaryAsync(x => x.Id, ct);
            var coverage = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, selectedIds, ct,
                buildScoped: true, effectiveProcedureRevisionIds: effectiveProcedureRevisionIds);
            var procedureRevisionIds=coverage.Select(x=>x.ProcedureRevisionId).Distinct().ToList();
            var executionQuery=db.TestExecutions.AsNoTracking().Where(x=>procedureRevisionIds.Contains(x.ProcedureRevisionId));
            var executions=await(db.Database.IsSqlite()?executionQuery.OrderByDescending(x=>x.Id):executionQuery.OrderByDescending(x=>x.ExecutedAt)).ToListAsync(ct);
            var executionIds=executions.Select(x=>x.Id).ToList();
            var evidence=await(from link in db.TestExecutionEvidence.AsNoTracking().Where(x=>executionIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new{link.TestExecutionId,item.Id,item.OriginalFileName,item.Sha256,item.Size,item.UploadedAt}).ToListAsync(ct);
            var items = selected.Select(x => new { x.artifact.Id, revisionId = x.revision.Id, displayNumber = x.artifact.BaseNumber + "." + x.revision.Revision.ToString("D2"), level = x.artifact.Level.ToString(), x.revision.Statement,
                parents = links.Where(l => l.SourceRevisionId == x.revision.Id).Select(l => new { id = l.TargetRevisionId, displayNumber = related[l.TargetRevisionId].BaseNumber + "." + related[l.TargetRevisionId].Revision.ToString("D2"), related[l.TargetRevisionId].level, type = l.Type.ToString() }),
                children = links.Where(l => l.TargetRevisionId == x.revision.Id).Select(l => new { id = l.SourceRevisionId, displayNumber = related[l.SourceRevisionId].BaseNumber + "." + related[l.SourceRevisionId].Revision.ToString("D2"), related[l.SourceRevisionId].level, type = l.Type.ToString() }),
                testCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && !c.IsSuspect),
                suspectTestCount = coverage.Count(c => c.RequirementRevisionId == x.revision.Id && c.IsSuspect),
                tests=coverage.Where(c=>c.RequirementRevisionId==x.revision.Id).Select(c=>new{procedureId=c.ProcedureId,revisionId=c.ProcedureRevisionId,c.DisplayNumber,c.Title,c.Level,state=c.ProcedureState,c.IsSuspect,c.CoverageState,executions=executions.Where(e=>e.ProcedureRevisionId==c.ProcedureRevisionId).Select(e=>new{e.Id,outcome=e.Outcome.ToString(),e.ExecutedBy,e.ExecutedAt,e.RecordedAt,e.SoftwareBuildId,e.RetestOfExecutionId,e.Determination,e.EvidenceReference,evidence=evidence.Where(a=>a.TestExecutionId==e.Id).Select(a=>new{a.Id,a.OriginalFileName,a.Sha256,a.Size,a.UploadedAt})})}) });
            return Results.Ok(new { baselineId, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize), items });
        });

        // A compact, exact end-to-end thread for the selected baseline. The general traceability endpoint above
        // remains the exploration surface; this projection answers the separate assurance question "show me one
        // complete controlled path" without implying that a nearby requirement, procedure, or build is related.
        app.MapGet("/api/traceability/path", async (Guid projectId, Guid baselineId, Guid? requirementRevisionId,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var baseline = await db.CandidateBaselines.AsNoTracking()
                .Where(x => x.Id == baselineId && x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.DisplayNumber, x.Name })
                .SingleOrDefaultAsync(ct);
            if (baseline is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();

            var nodes = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                               join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                               join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                               select new
                               {
                                   id = artifact.Id,
                                   revisionId = revision.Id,
                                   displayNumber = artifact.BaseNumber + "." + revision.Revision.ToString("D2"),
                                   level = artifact.Level.ToString(),
                                   revision.Statement,
                               }).ToListAsync(ct);
            if (nodes.Count == 0) return Results.Ok(new { baselineId, nodes = Array.Empty<object>() });

            var byRevision = nodes.ToDictionary(x => x.revisionId);
            var revisionIds = byRevision.Keys.ToList();
            var links = await db.RequirementTraces.AsNoTracking()
                .Where(x => revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId))
                .ToListAsync(ct);
            var coveredIds = await db.TestCoverage.AsNoTracking()
                .Where(x => revisionIds.Contains(x.RequirementRevisionId) && !x.IsSuspect)
                .Select(x => x.RequirementRevisionId).Distinct().ToListAsync(ct);
            var covered = coveredIds.ToHashSet();

            var focus = requirementRevisionId is Guid requested && byRevision.TryGetValue(requested, out var selected)
                ? selected
                : nodes.OrderBy(x => x.level == "System" ? 0 : x.level == "HighLevel" ? 1 : 2)
                    .ThenBy(x => x.displayNumber).First();

            // Source is the child and Target is its parent. Walk both directions from the reader's focus so the
            // path always includes it. Prefer a covered descendant, then use the stable controlled number.
            var ancestors = new List<Guid>();
            var cursor = focus.revisionId;
            var seen = new HashSet<Guid> { cursor };
            while (links.Where(x => x.SourceRevisionId == cursor)
                       .OrderBy(x => byRevision[x.TargetRevisionId].displayNumber)
                       .Select(x => (Guid?)x.TargetRevisionId).FirstOrDefault() is Guid parent
                   && seen.Add(parent))
            {
                ancestors.Add(parent);
                cursor = parent;
            }
            ancestors.Reverse();

            var descendants = new List<Guid>();
            cursor = focus.revisionId;
            while (links.Where(x => x.TargetRevisionId == cursor)
                       .Select(x => x.SourceRevisionId)
                       .OrderByDescending(x => covered.Contains(x))
                       .ThenBy(x => byRevision[x].displayNumber)
                       .Select(x => (Guid?)x).FirstOrDefault() is Guid child
                   && seen.Add(child))
            {
                descendants.Add(child);
                cursor = child;
            }
            var pathIds = ancestors.Append(focus.revisionId).Concat(descendants).ToList();
            var verificationRequirementId = pathIds.AsEnumerable().Reverse().FirstOrDefault(covered.Contains);

            var buildQuery = db.SoftwareBuilds.AsNoTracking().Where(x => x.BaselineId == baselineId);
            var buildRecord = db.Database.IsSqlite()
                ? (await buildQuery.ToListAsync(ct)).OrderByDescending(x => x.RecordedAt).FirstOrDefault()
                : await buildQuery.OrderByDescending(x => x.RecordedAt).FirstOrDefaultAsync(ct);
            Guid? selectedBuildId = buildRecord?.Id;

            IReadOnlyList<PathProcedureCandidate> procedureCandidates = verificationRequirementId == Guid.Empty
                ? Array.Empty<PathProcedureCandidate>()
                : await (from coverage in db.TestCoverage.AsNoTracking().Where(x => x.RequirementRevisionId == verificationRequirementId && !x.IsSuspect)
                         join revision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals revision.Id
                         join item in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals item.Id
                         select new PathProcedureCandidate(
                             item.Id,
                             revision.Id,
                             item.BaseNumber + "." + revision.Revision.ToString("D2"),
                             item.Title,
                             item.Level.ToString(),
                             revision.State.ToString())).ToListAsync(ct);

            var candidateRevisionIds = procedureCandidates.Select(x => x.RevisionId).ToList();
            IReadOnlyList<TestExecution> candidateRuns = candidateRevisionIds.Count == 0
                ? Array.Empty<TestExecution>()
                : await db.TestExecutions.AsNoTracking()
                    .Where(x => candidateRevisionIds.Contains(x.ProcedureRevisionId) && x.ReleaseId == baseline.ReleaseId
                        && x.SoftwareBuildId == selectedBuildId)
                    .ToListAsync(ct);
            var latestByProcedure = candidateRuns.GroupBy(x => x.ProcedureRevisionId)
                .ToDictionary(x => x.Key, x => x
                    .OrderByDescending(run => run.ExecutedAt)
                    .ThenByDescending(run => run.RecordedAt).First());
            var candidateRunIds = latestByProcedure.Values.Select(x => x.Id).ToList();
            IReadOnlyList<Guid> evidencedRunIds = candidateRunIds.Count == 0
                ? Array.Empty<Guid>()
                : await db.TestExecutionEvidence.AsNoTracking().Where(x => candidateRunIds.Contains(x.TestExecutionId))
                    .Select(x => x.TestExecutionId).Distinct().ToListAsync(ct);
            var evidenced = evidencedRunIds.ToHashSet();

            // Prefer a genuinely complete path, then any build-scoped result, and use the controlled number only
            // as a stable tie-breaker. Choosing the first procedure number could report a gap while another exact
            // confirmed procedure already carried the result and immutable evidence the reader asked to see.
            var procedure = procedureCandidates
                .OrderByDescending(x => latestByProcedure.TryGetValue(x.RevisionId, out var run) && evidenced.Contains(run.Id))
                .ThenByDescending(x => latestByProcedure.ContainsKey(x.RevisionId))
                .ThenBy(x => x.DisplayNumber)
                .FirstOrDefault();

            object? execution = null;
            if (procedure is not null)
            {
                latestByProcedure.TryGetValue(procedure.RevisionId, out var run);
                if (run is not null)
                {
                    var files = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => x.TestExecutionId == run.Id)
                                       join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id
                                       select new { item.Id, item.OriginalFileName, item.Sha256, item.Size, item.UploadedAt }).ToListAsync(ct);
                    execution = new
                    {
                        run.Id,
                        outcome = run.Outcome.ToString(),
                        run.ExecutedBy,
                        run.ExecutedAt,
                        run.Determination,
                        run.EvidenceReference,
                        evidence = files,
                    };
                }
            }

            var build = buildRecord is null ? null : new
            {
                buildRecord.Id,
                buildRecord.BuildNumber,
                state = buildRecord.State.ToString(),
                buildRecord.RecordedAt,
                buildRecord.ReleasedAt,
            };

            return Results.Ok(new
            {
                baselineId,
                baseline = new { baseline.DisplayNumber, baseline.Name },
                focusRevisionId = focus.revisionId,
                nodes = pathIds.Select(id => byRevision[id]),
                procedure = procedure is null ? null : new
                {
                    id = procedure.Id,
                    revisionId = procedure.RevisionId,
                    displayNumber = procedure.DisplayNumber,
                    procedure.Title,
                    level = procedure.Level,
                    state = procedure.State,
                },
                execution,
                build,
            });
        });

        // Discussion on a procedure, the same conversation a requirement carries.
        //
        // ArtifactComment was already generic — it keys on an artifact type and identifier — so only the route
        // was requirement-shaped. These two store "TestProcedure" against the same table rather than adding a
        // parallel comment record, which would have been a second thing to keep in step for no gain.
        app.MapGet("/api/test-procedures/{id:guid}/comments", async (Guid id, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var projectId = await db.TestProcedures.Where(x => x.Id == id).Select(x => (Guid?)x.ProjectId).SingleOrDefaultAsync(ct);
            if (projectId is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var comments = await db.ArtifactComments.AsNoTracking()
                .Where(x => x.ArtifactId == id && x.ArtifactType == "TestProcedure").ToListAsync(ct);
            return Results.Ok(comments.OrderBy(x => x.CreatedAt).Select(x => new
            {
                x.Id, x.RevisionId, x.ParentCommentId, x.Body, x.MentionsJson, state = x.State.ToString(),
                x.CreatedBy, x.CreatedAt, x.ResolvedBy, x.ResolvedAt, x.Disposition,
            }));
        });

        app.MapPost("/api/test-procedures/{id:guid}/comments", async (Guid id, CreateProcedureCommentRequest request,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (procedure is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();
            if (request.RevisionId is not null
                && !await db.TestProcedureRevisions.AnyAsync(x => x.Id == request.RevisionId && x.ProcedureId == id, ct))
                return Results.BadRequest(new { error = "The comment revision is not part of this procedure." });
            if (request.ParentCommentId is not null
                && !await db.ArtifactComments.AnyAsync(x => x.Id == request.ParentCommentId && x.ArtifactId == id, ct))
                return Results.BadRequest(new { error = "The parent comment is not part of this procedure." });
            try
            {
                var actor = http.UserAccount().UserName;
                var now = DateTimeOffset.UtcNow;
                var mentions = request.Mentions ?? [];
                var comment = new ArtifactComment(procedure.ProjectId, "TestProcedure", id, request.RevisionId,
                    request.ParentCommentId, request.Body, JsonSerializer.Serialize(mentions), actor, now);
                db.ArtifactComments.Add(comment);

                // Mentioning somebody has to reach them, or the discussion is only identical to a requirement's
                // in appearance. Procedures carry no watch list, so the audience is who was named plus whoever
                // is being replied to.
                var requested = mentions.Select(x => x.Trim().ToLowerInvariant()).ToHashSet();
                if (request.ParentCommentId is not null)
                    requested.Add((await db.ArtifactComments.Where(x => x.Id == request.ParentCommentId)
                        .Select(x => x.CreatedBy).SingleAsync(ct)).ToLowerInvariant());
                var recipients = await db.UserAccounts.AsNoTracking()
                    .Where(x => requested.Contains(x.UserName) && x.UserName != actor)
                    .Select(x => x.UserName).ToListAsync(ct);
                foreach (var recipient in recipients)
                    db.UserNotifications.Add(new(procedure.ProjectId, recipient, "TestProcedureComment",
                        $"Discussion on {procedure.BaseNumber}", $"{actor}: {request.Body}",
                        $"testProcedure:{id}", id, now));

                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/test-procedures/{id}/comments/{comment.Id}",
                    new { comment.Id, notified = recipients.Count });
            }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        /// How a procedure came to say what it says.
        ///
        /// A procedure is read by somebody deciding whether to trust it, and "who wrote this, when, and what
        /// made them change it" is most of that decision. Its revisions were reachable only by reading the
        /// procedure itself, one revision at a time, with no way to see what drove any of them.
        ///
        /// The change request behind a revision is not recorded on the revision — it is reached through the
        /// verification decision that resolved to it, which is the record that actually connects the two. A
        /// revision written outside that path has no change request, and says so rather than guessing.
        app.MapGet("/api/test-procedures/{id:guid}/history", async (Guid id, Guid? revisionId, Guid? releaseId, HttpContext http,
            AeroLinkDbContext db, CancellationToken ct) =>
        {
            var procedure = await db.TestProcedures.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.Id, x.ProjectId, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt })
                .SingleOrDefaultAsync(ct);
            if (procedure is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, procedure.ProjectId, ct)) return Results.Forbid();

            var revisions = (await db.TestProcedureRevisions.AsNoTracking()
                .Where(x => x.ProcedureId == id).ToListAsync(ct))
                .OrderByDescending(x => x.Revision).ToList();
            var revisionIds = revisions.Select(x => x.Id).ToList();
            if (revisionId is not null && !revisionIds.Contains(revisionId.Value)) return Results.NotFound();
            Guid? effectiveRevisionId = null;
            if (releaseId is not null)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, procedure.ProjectId, releaseId.Value, ct);
                if (effectivity is not null && effectivity.RevisionByProcedure.TryGetValue(id, out var carriedRevisionId))
                    effectiveRevisionId = carriedRevisionId;
                // A request for one exact revision is a build-effectivity assertion and must match the
                // manifest. Omitting revisionId is the broad historical view: legacy and draft procedures
                // remain readable there even when no build ever carried them.
                if (revisionId is not null && revisionId != effectiveRevisionId) return Results.NotFound();
            }

            // What each revision answered for: the verification decision that resolved to it, the package
            // that decision belonged to, and the change request that package was raised from.
            var drivers = await (from item in db.VerificationImpactItems.AsNoTracking()
                                 join review in db.TestChangeReviews.AsNoTracking() on item.TestChangeReviewId equals review.Id
                                 where item.ResolvedProcedureRevisionId != null && revisionIds.Contains(item.ResolvedProcedureRevisionId.Value)
                                 select new
                                 {
                                     RevisionId = item.ResolvedProcedureRevisionId!.Value,
                                     item.SubjectDisplayNumber,
                                     ChangeRequest = review.SourceChangeRequestNumber,
                                     Package = review.BaseNumber,
                                     Action = item.ProcedureChangeAction,
                                 }).ToListAsync(ct);

            // The requirements each revision covers, so a reader sees what it is for without leaving the page.
            var coverage = await (from link in db.TestCoverage.AsNoTracking()
                                  join revision in db.RequirementRevisions.AsNoTracking() on link.RequirementRevisionId equals revision.Id
                                  join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id
                                  where revisionIds.Contains(link.ProcedureRevisionId)
                                  select new { link.ProcedureRevisionId, artifact.BaseNumber, revision.Revision }).ToListAsync(ct);

            return Results.Ok(new
            {
                procedure.Id,
                procedure.BaseNumber,
                procedure.Title,
                level = procedure.Level.ToString(),
                procedure.OwnerId,
                procedure.CreatedAt,
                selectedRevisionId = revisionId ?? effectiveRevisionId,
                revisions = revisions.Select(revision => new
                {
                    revision.Id,
                    displayNumber = $"{procedure.BaseNumber}.{revision.Revision:D2}",
                    revision.Revision,
                    state = revision.State.ToString(),
                    revision.AuthorId,
                    revision.CreatedAt,
                    revision.Objective,
                    revision.Preconditions,
                    revision.Steps,
                    revision.ExpectedResult,
                    selected = revision.Id == revisionId,
                    drivenBy = drivers.Where(x => x.RevisionId == revision.Id)
                        .Select(x => new { x.ChangeRequest, x.Package, x.SubjectDisplayNumber, action = x.Action.ToString() })
                        .Distinct().ToList(),
                    covers = coverage.Where(x => x.ProcedureRevisionId == revision.Id)
                        .Select(x => $"{x.BaseNumber}.{x.Revision:D2}").Distinct().OrderBy(x => x).ToList(),
                }).ToList(),
            });
        });

        // The workspace rendered every procedure it was given — 440 cards on the software side — with no
        // search, filter or page. This returns a bounded page and the total, and every predicate below runs
        // in the database, because a page of twenty-five that costs a full table read is not paging.
        app.MapGet("/api/test-procedures", async (Guid projectId, Guid? releaseId, string? search, string? scope, string? state,
            string? owner, string? outcome, Guid? requirementRevisionId, string? sort, int? page, int? pageSize,
            HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            // This endpoint read a Project's controlled procedures without checking the caller was in it.
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var currentPage = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            var source = db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId);
            Dictionary<Guid, Guid>? scopedRevisions = null;
            if(releaseId is not null)
            {
                var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
                if(effectivity is null)return Results.Ok(new{page=currentPage,pageSize=size,totalCount=0,totalPages=0,items=Array.Empty<object>()});
                scopedRevisions = effectivity.RevisionByProcedure.ToDictionary(x => x.Key, x => x.Value);
                var effectiveProcedureIds = scopedRevisions.Keys.ToList();
                source=source.Where(x=>effectiveProcedureIds.Contains(x.Id));
            }
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel||x.Level==TestProcedureLevel.LowLevel);
            else if(string.Equals(scope,"HighLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.HighLevel);
            else if(string.Equals(scope,"LowLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.Level==TestProcedureLevel.LowLevel);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLower();
                // Deep links use the controlled display number, including its revision suffix, while the
                // procedure owns only the base number. Let either form find the same controlled procedure.
                var requestedRevision = -1;
                var hasRevision = q.Length > 3 && q[^3] == '.' && int.TryParse(q[^2..], out requestedRevision);
                var baseQuery = hasRevision ? q[..^3] : q;
                source = source.Where(x => x.BaseNumber.ToLower().Contains(baseQuery) || x.Title.ToLower().Contains(q));
                if (hasRevision && scopedRevisions is not null)
                {
                    var scopedRevisionIds = scopedRevisions.Values.ToList();
                    var matchingProcedureIds = await db.TestProcedureRevisions.AsNoTracking()
                        .Where(x => scopedRevisionIds.Contains(x.Id) && x.Revision == requestedRevision)
                        .Select(x => x.ProcedureId).ToListAsync(ct);
                    source = source.Where(x => matchingProcedureIds.Contains(x.Id));
                }
            }
            if (!string.IsNullOrWhiteSpace(owner)) { var o = owner.Trim().ToLower(); source = source.Where(x => x.OwnerId.ToLower() == o); }
            // Lifecycle state belongs to the current revision, so the predicate names it rather than matching
            // any revision a procedure has ever had.
            if (!string.IsNullOrWhiteSpace(state) && Enum.TryParse<TestProcedureState>(state, true, out var parsedState))
            {
                var scopedRevisionIds = scopedRevisions?.Values.ToList();
                source = scopedRevisionIds is null
                    ? source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                        && r.Revision == db.TestProcedureRevisions.Where(o => o.ProcedureId == x.Id).Max(o => o.Revision)
                        && r.State == parsedState))
                    : source.Where(x => db.TestProcedureRevisions.Any(r => r.ProcedureId == x.Id
                        && scopedRevisionIds.Contains(r.Id) && r.State == parsedState));
            }
            if (requirementRevisionId is not null)
                source = source.Where(x => db.TestCoverage.Any(c => c.RequirementRevisionId == requirementRevisionId
                    && db.TestProcedureRevisions.Any(r => r.Id == c.ProcedureRevisionId && r.ProcedureId == x.Id)));
            // Latest outcome means the most recent run, not any run the procedure ever had — a procedure that
            // failed and was then fixed must answer to Pass and not to Fail.
            //
            // SQLite can neither order nor aggregate a DateTimeOffset, so "most recent" cannot be expressed in
            // SQL here. The comparison is made in memory over the ids the other predicates have already
            // narrowed to, and only when this filter is actually used; the page itself is still taken in the
            // database.
            if (!string.IsNullOrWhiteSpace(outcome) && Enum.TryParse<TestOutcome>(outcome, true, out var parsedOutcome))
            {
                var candidateIds = await source.Select(x => x.Id).ToListAsync(ct);
                var scopedRevisionIds = scopedRevisions?.Where(x => candidateIds.Contains(x.Key)).Select(x => x.Value).ToList();
                var runs = await (from execution in db.TestExecutions.AsNoTracking()
                                  join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                                  where candidateIds.Contains(revision.ProcedureId)
                                      && (scopedRevisionIds == null || scopedRevisionIds.Contains(revision.Id))
                                  select new { revision.ProcedureId, execution.Outcome, execution.ExecutedAt, execution.RecordedAt }).ToListAsync(ct);
                var matching = runs.GroupBy(x => x.ProcedureId)
                    .Where(group => group.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.RecordedAt).First().Outcome == parsedOutcome)
                    .Select(group => group.Key).ToList();
                source = source.Where(x => matching.Contains(x.Id));
            }

            var totalCount = await source.CountAsync(ct);
            // Every sort ends on the controlled number, so a page boundary cannot depend on tie order.
            var ordered = sort?.ToLowerInvariant() switch
            {
                "title" => source.OrderBy(x => x.Title).ThenBy(x => x.BaseNumber),
                "owner" => source.OrderBy(x => x.OwnerId).ThenBy(x => x.BaseNumber),
                "level" => source.OrderBy(x => x.Level).ThenBy(x => x.BaseNumber),
                _ => source.OrderBy(x => x.BaseNumber).ThenBy(x => x.BaseNumber),
            };
            var items = await ordered.Skip((currentPage - 1) * size).Take(size)
                .Select(x => new { x.Id, x.BaseNumber, x.Title, x.OwnerId, x.Level, x.CreatedAt }).ToListAsync(ct);
            var ids = items.Select(x => x.Id).ToList(); var revisions = await db.TestProcedureRevisions.AsNoTracking().Where(x => ids.Contains(x.ProcedureId)).ToListAsync(ct);
            var selectedRevisionIds = scopedRevisions is null
                ? revisions.GroupBy(x => x.ProcedureId).Select(group => group.OrderByDescending(x => x.Revision).First().Id).ToList()
                : scopedRevisions.Where(x => ids.Contains(x.Key)).Select(x => x.Value).ToList();
            var coverage = await db.TestCoverage.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            var executions = await db.TestExecutions.AsNoTracking().Where(x => selectedRevisionIds.Contains(x.ProcedureRevisionId)).ToListAsync(ct);
            return Results.Ok(new { page = currentPage, pageSize = size, totalCount, totalPages = (int)Math.Ceiling(totalCount / (double)size),
                items = items.Select(x => { var latest = scopedRevisions is not null && scopedRevisions.TryGetValue(x.Id, out var selectedRevisionId)
                        ? revisions.SingleOrDefault(r => r.Id == selectedRevisionId)
                        : revisions.Where(r => r.ProcedureId == x.Id).OrderByDescending(r => r.Revision).FirstOrDefault();
                    var lastRun = latest is null ? null : executions.Where(e => e.ProcedureRevisionId == latest.Id).OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                return new { x.Id, displayNumber = latest is null ? x.BaseNumber : x.BaseNumber + "." + latest.Revision.ToString("D2"), x.Title, x.OwnerId, level = x.Level.ToString(),
                    revisionId = latest?.Id, revision = latest?.Revision, state = latest?.State.ToString(), objective = latest?.Objective,
                    // No selectedApproverId. It existed to route a procedure-level signature, and that
                    // signature is gone; the package's approver is the one who approved this work. The stored
                    // value stays on legacy revisions as the honest record of who was once named.
                    requirementCount = latest is null ? 0 : coverage.Count(c => c.ProcedureRevisionId == latest.Id), lastOutcome = lastRun?.Outcome.ToString(), lastExecutedAt = lastRun?.ExecutedAt }; }) });
        });

        // No procedure-level approval route. The test change request carrying this procedure is what gets
        // approved, and materialisation writes the revision as Approved on that authority — a separate
        // signature on the procedure revision would be a second approval of the same controlled work. The
        // decision that asked for the procedure is settled by the materialiser, which is where the approved
        // revision now comes into existence.

        app.MapPost("/api/test-executions", async (RecordTestExecutionRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.TestEngineer))return Results.Forbid();
            var revision = await db.TestProcedureRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ProcedureRevisionId, ct); if (revision is null) return Results.NotFound();
            if (revision.State != TestProcedureState.Approved) return Results.BadRequest(new { error = "Only an approved test procedure revision can be executed." });
            var procedure = await db.TestProcedures.AsNoTracking().SingleAsync(x => x.Id == revision.ProcedureId, ct); if (procedure.ProjectId != request.ProjectId) return Results.BadRequest(new { error = "The test procedure belongs to a different project." });
            Guid? softwareBuildReleaseId = null;
            if (request.SoftwareBuildId is not null)
            {
                softwareBuildReleaseId = await db.SoftwareBuilds.AsNoTracking()
                    .Where(x => x.Id == request.SoftwareBuildId && x.ProjectId == request.ProjectId)
                    .Select(x => (Guid?)x.ReleaseId).SingleOrDefaultAsync(ct);
                if (softwareBuildReleaseId is null) return Results.BadRequest(new { error = "The software build belongs to a different project." });
            }
            Guid? activeReleaseId = Guid.TryParse(http.Request.Headers["X-AeroLink-Build-Context"].FirstOrDefault(), out var parsedReleaseId)
                ? parsedReleaseId
                : null;
            if (activeReleaseId is not null && softwareBuildReleaseId is not null && softwareBuildReleaseId != activeReleaseId)
                return Results.Conflict(new { error = "The software build belongs to a different active build workspace.", code = "cross_build_resource" });
            var executionReleaseId = activeReleaseId ?? softwareBuildReleaseId;
            // A released build is read-only, and this endpoint has to say so itself.
            //
            // The workspace middleware already refuses this, but only when the caller supplies the build-context
            // header. That is a browser guarantee, not a product one: a service account, an integration or a
            // script that omits the header reached the final validation with the released boundary never
            // checked, and a well-formed request would have written an immutable determination against a
            // released build. Checked here, before the campaign-freeze and retest rules, so no unrelated
            // failure can mask the refusal and make the endpoint look protected when it is not.
            if (executionReleaseId is not null && await db.Releases.AsNoTracking()
                    .AnyAsync(x => x.Id == executionReleaseId && x.ProjectId == request.ProjectId && x.IsReleased, ct))
            {
                var version = await db.Releases.AsNoTracking().Where(x => x.Id == executionReleaseId)
                    .Select(x => x.Version).SingleAsync(ct);
                return Results.Conflict(new
                {
                    error = $"Build {version} is released and read-only. Exit this workspace and select an in-work build to make changes.",
                    code = "released_build_read_only"
                });
            }
            if (request.SoftwareBuildId is not null && await db.ReleaseCampaigns.AsNoTracking().AnyAsync(x => x.SoftwareBuildId == request.SoftwareBuildId && x.State == ReleaseCampaignState.InReview, ct))
                return Results.Conflict(new { error = "The release package is frozen while approval is in progress.", code = "release_package_frozen" });
            if (request.RetestOfExecutionId is not null && !await db.TestExecutions.AnyAsync(x => x.Id == request.RetestOfExecutionId && x.ProcedureRevisionId == request.ProcedureRevisionId, ct)) return Results.BadRequest(new { error = "A retest must reference an earlier execution of the same procedure revision." });
            try { var execution = new TestExecution(request.ProjectId, request.ProcedureRevisionId, request.SoftwareBuildId, request.RetestOfExecutionId,
                request.Outcome, http.UserAccount().UserName, request.Configuration, request.Determination, request.EvidenceReference, request.ExecutedAt, DateTimeOffset.UtcNow, executionReleaseId);
                db.TestExecutions.Add(execution); await db.SaveChangesAsync(ct); return Results.Created($"/api/test-executions/{execution.Id}", new { execution.Id, outcome = execution.Outcome.ToString() }); }
            catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/test-executions", async (Guid projectId, Guid? releaseId, Guid? buildId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (releaseId is not null && !await db.Releases.AsNoTracking()
                    .AnyAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new
                {
                    error = "The selected release does not belong to this Project.",
                    code = "release_project_mismatch"
                });
            if (buildId is not null)
            {
                var build = await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == buildId)
                    .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                if (build is null || build.ProjectId != projectId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to this Project.",
                        code = "build_project_mismatch"
                    });
                if (releaseId is not null && build.ReleaseId != releaseId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to the selected release.",
                        code = "build_release_mismatch"
                    });
            }
            var source = db.TestExecutions.AsNoTracking().Where(x => x.ProjectId == projectId && (buildId == null || x.SoftwareBuildId == buildId)
                && (releaseId == null || x.ReleaseId == releaseId
                    || x.ReleaseId == null && x.SoftwareBuildId != null && db.SoftwareBuilds.Any(b => b.Id == x.SoftwareBuildId && b.ReleaseId == releaseId)));
            var rowsQuery = from execution in source join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id
                              join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                              select new { execution.Id, procedureRevisionId = revision.Id, displayNumber = procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                  procedure.Title, outcome = execution.Outcome.ToString(), execution.ExecutedBy, execution.Configuration, execution.Determination,
                                  execution.EvidenceReference, execution.ExecutedAt, execution.RecordedAt, execution.ReleaseId, execution.SoftwareBuildId, execution.RetestOfExecutionId };
            var rows = await (db.Database.IsSqlite() ? rowsQuery.OrderByDescending(x => x.Id) : rowsQuery.OrderByDescending(x => x.ExecutedAt)).ToListAsync(ct); var rowIds = rows.Select(x => x.Id).ToList();
            var evidence = await (from link in db.TestExecutionEvidence.AsNoTracking().Where(x => rowIds.Contains(x.TestExecutionId)) join item in db.EvidenceRecords.AsNoTracking() on link.EvidenceId equals item.Id select new { link.TestExecutionId, item.Id, item.OriginalFileName, item.Size, item.Sha256, item.UploadedAt }).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new { x.Id, x.procedureRevisionId, x.displayNumber, x.Title, x.outcome, x.ExecutedBy, x.Configuration, x.Determination, x.EvidenceReference, x.ExecutedAt, x.RecordedAt, x.ReleaseId, x.SoftwareBuildId, x.RetestOfExecutionId, evidence = evidence.Where(e => e.TestExecutionId == x.Id).Select(e => new { e.Id, e.OriginalFileName, e.Size, e.Sha256, e.UploadedAt }) }));
        });

        app.MapGet("/api/verification-coverage", async (Guid projectId, Guid? baselineId, Guid? buildId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            if (buildId is not null)
            {
                var build = await db.SoftwareBuilds.AsNoTracking().Where(x => x.Id == buildId)
                    .Select(x => new { x.ProjectId, x.BaselineId }).SingleOrDefaultAsync(ct);
                if (build is null || build.ProjectId != projectId)
                    return Results.BadRequest(new
                    {
                        error = "The selected software build does not belong to this Project.",
                        code = "build_project_mismatch"
                    });
                if (baselineId is not null && baselineId != build.BaselineId)
                    return Results.BadRequest(new
                    {
                        error = "The selected baseline is not the baseline carried by this software build.",
                        code = "baseline_build_mismatch"
                    });
                baselineId = build.BaselineId;
            }
            if (baselineId is null) return Results.BadRequest(new { error = "Select a materialized baseline or software build." });
            if (!await db.CandidateBaselines.AsNoTracking().AnyAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct))
                return Results.BadRequest(new { error = "The selected baseline does not belong to this Project.", code = "baseline_project_mismatch" });
            var requirements = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                                      join artifact in db.Requirements.AsNoTracking() on member.ArtifactId equals artifact.Id
                                      join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                                      orderby artifact.BaseNumber select new { artifact.Id, revisionId = revision.Id, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, revision.Statement }).ToListAsync(ct);
            var requirementIds = requirements.Select(x => x.revisionId).ToList();
            var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId.Value, ct);
            var coverageLinks = await VerificationCoverageProjection.ForRequirementRevisionsAsync(db, requirementIds, ct,
                buildScoped: true, effectiveProcedureRevisionIds: procedureEffectivity?.RevisionIds ?? []);
            var procedureRevisionIds = coverageLinks.Select(x => x.ProcedureRevisionId).Distinct().ToList();
            var executions = await db.TestExecutions.AsNoTracking().Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId) && (buildId == null || x.SoftwareBuildId == buildId)).ToListAsync(ct);
            var items = requirements.Select(req =>
            {
                var coveredBy = coverageLinks.Where(x => x.RequirementRevisionId == req.revisionId).Select(link =>
                {
                    var latest = executions.Where(e => e.ProcedureRevisionId == link.ProcedureRevisionId)
                        .OrderByDescending(e => e.ExecutedAt).ThenByDescending(e => e.RecordedAt).FirstOrDefault();
                    return new
                    {
                        procedureId = link.ProcedureId,
                        revisionId = link.ProcedureRevisionId,
                        link.DisplayNumber,
                        link.Title,
                        state = link.ProcedureState,
                        link.IsSuspect,
                        link.CoverageState,
                        latestOutcome = latest?.Outcome.ToString(),
                        latestExecutionId = latest?.Id
                    };
                }).ToList();
                var disposition = coveredBy.Any(x => x.CoverageState == "Confirmed")
                    ? RequirementCoverageState.Covered
                    : coveredBy.Count != 0 ? RequirementCoverageState.Suspect : RequirementCoverageState.Uncovered;
                var covered = disposition == RequirementCoverageState.Covered;
                return new { req.Id, req.revisionId, req.displayNumber, req.Statement, disposition, covered, verified = coveredBy.Any(x => x.CoverageState == "Confirmed" && x.latestOutcome == "Pass"), coveredBy };
            }).ToList();
            return Results.Ok(new
            {
                baselineId,
                buildId,
                total = items.Count,
                covered = items.Count(x => x.disposition == RequirementCoverageState.Covered),
                suspect = items.Count(x => x.disposition == RequirementCoverageState.Suspect),
                verified = items.Count(x => x.verified),
                uncovered = items.Count(x => x.disposition == RequirementCoverageState.Uncovered),
                items
            });
        });
    }

    private sealed record PathProcedureCandidate(
        Guid Id,
        Guid RevisionId,
        string DisplayNumber,
        string Title,
        string Level,
        string State);
}
