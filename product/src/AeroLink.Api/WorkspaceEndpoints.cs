using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

/// <summary>
/// The shell everything else hangs from — programs, projects, releases — with the queues and
/// dashboards that tell somebody where their attention is needed.
///
/// These are the reads that run on every navigation, so what they cost is what the product feels like.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        // Unsubscribe is reachable without signing in, because it is followed from a mail client. The signed
        // token is what proves the link came from this deployment; without it anyone could silence anyone else's
        // approval notices. Always answers the same way, so the endpoint cannot be used to discover who exists.
        app.MapGet("/api/notifications/unsubscribe", async (string? recipient, string? token, AeroLinkDbContext db, UnsubscribeTokenService tokens, CancellationToken ct) =>
        {
            const string answer = "If that link was valid, email notification is now off for that account. Sign in to AeroLink to turn it back on.";
            if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(token) || !tokens.Validate(recipient, token))
                return Results.Text(answer);
            var name = recipient.Trim().ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var preference = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.Recipient == name, ct);
            if (preference is null) { preference = new NotificationPreference(name, now); db.NotificationPreferences.Add(preference); }
            preference.SetEmailEnabled(false, now);
            db.SecurityAuditEvents.Add(new("NotificationEmailDisabled", name, name, "Success", "Email notification turned off from an unsubscribe link.", "local", now));
            await db.SaveChangesAsync(ct);
            return Results.Text(answer);
        }).AllowAnonymous();

        // The practice Program is seeded here as well as at boot, because a demonstration database is seeded
        // at boot while the journeys seed through this endpoint. Before the identities, which grant the demo
        // directory membership of every Program that exists by then.
        app.MapPost("/api/showcase/seed", async (HttpContext http,FmsShowcaseSeeder seeder, ImportPracticeSeeder practice, IdentitySeeder identities, ManagedDocumentShowcaseSeeder documents, EnterpriseRequirementsService workspace, IConfiguration configuration, CancellationToken ct) => {if(!http.UserAccount().IsAdministrator)return Results.Forbid();if(!configuration.GetValue<bool>("Identity:SeedDemoAccounts"))return Results.NotFound();var result=await seeder.EnsureSeededAsync(ct); await practice.EnsureSeededAsync(ct); await identities.EnsureSeededAsync(ct); await workspace.SynchronizeProjectAsync(result.ProjectId,"system.workspace",ct); await documents.EnsureSeededAsync(ct); return Results.Ok(result); });

        // What the showcase upgrade has and has not applied to this installation, and whether the invariants
        // it is meant to guarantee actually hold. An upgrade that reports success is not the same as a
        // database that is correct, so this reports the two separately and an operator can read both.
        app.MapGet("/api/showcase/upgrade-state", async (HttpContext http, AeroLinkDbContext db, FmsShowcaseSeeder seeder, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == FmsShowcaseSeeder.ProgramCode, ct);
            if (program is null) return Results.Ok(new { seeded = false, steps = Array.Empty<object>(), invariants = Array.Empty<object>() });
            var steps = (await db.ShowcaseUpgradeSteps.AsNoTracking().Where(x => x.ProgramId == program.Id).ToListAsync(ct))
                .OrderBy(x => x.AppliedAt).Select(x => new { x.StepKey, x.Detail, x.AppliedAt }).ToList();
            var invariants = await seeder.CheckInvariantsAsync(program.Id, ct);
            return Results.Ok(new { seeded = true, programId = program.Id, steps, healthy = invariants.All(x => x.Holds), invariants });
        });

        // The repair command for an existing local showcase: apply any outstanding steps and report what
        // changed. Safe to run repeatedly, and safe to run again after an interrupted attempt.
        app.MapPost("/api/showcase/upgrade", async (HttpContext http, AeroLinkDbContext db, FmsShowcaseSeeder seeder, CancellationToken ct) =>
        {
            if (!http.UserAccount().IsAdministrator) return Results.Forbid();
            var program = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == FmsShowcaseSeeder.ProgramCode, ct);
            if (program is null) return Results.NotFound(new { error = "No showcase Program is installed.", code = "showcase_absent" });
            var applied = await seeder.UpgradeAsync(program.Id, ct);
            var invariants = await seeder.CheckInvariantsAsync(program.Id, ct);
            return Results.Ok(new { applied, healthy = invariants.All(x => x.Holds), invariants });
        });

        app.MapGet("/api/programs", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet();
            return Results.Ok(await db.Programs.AsNoTracking().Where(p=>allowed==null||allowed.Contains(p.Id)).Select(p => new { p.Id, p.Name, p.Code }).ToListAsync(ct));
        });

        app.MapPost("/api/workspaces", async (CreateWorkspaceRequest request, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if(!http.UserAccount().IsAdministrator)return Results.Forbid();
            if (await db.Programs.AnyAsync(x => x.Code == request.ProgramCode.Trim().ToUpper(), ct))
                return Results.Conflict(new { error = "A program with that code already exists." });
            try
            {
                var program = new ProgramRecord(request.ProgramName, request.ProgramCode);
                var project = new ProjectRecord(program.Id, request.ProjectName, request.SoftwareProduct);
                var release = new SoftwareRelease(project.Id, request.InitialRelease, request.InitialReleaseIsReleased);
                db.AddRange(program, project, release);
                var actor = http.UserAccount(); db.ProgramMemberships.Add(new ProgramMembership(actor.Id, program.Id, ProgramRole.Administrator, actor.UserName, DateTimeOffset.UtcNow));
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/programs/{program.Id}", ApiMap.Workspace(program, project, release));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/api/workspaces", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet();
            var programs = await db.Programs.AsNoTracking().Where(x=>allowed==null||allowed.Contains(x.Id)).ToListAsync(ct);
            var projects = await db.Projects.AsNoTracking().ToListAsync(ct);
            var releases = await db.Releases.AsNoTracking().ToListAsync(ct);
            return Results.Ok(programs.Select(program => new
            {
                program = new { program.Id, program.Name, program.Code },
                projects = projects.Where(x => x.ProgramId == program.Id).Select(project => new
                {
                    project = new { project.Id, project.Name, project.SoftwareProduct },
                    releases = releases.Where(x => x.ProjectId == project.Id).OrderBy(x => x.Version)
                        .Select(x => new { x.Id, x.Version, x.IsReleased, x.PredecessorReleaseId })
                })
            }));
        });

        app.MapGet("/api/context", async (HttpContext http, AeroLinkDbContext db, CancellationToken ct) => { var actor=http.UserAccount(); var allowed=actor.IsAdministrator?null:actor.Programs.Select(x=>x.ProgramId).ToHashSet(); var programs=await db.Programs.AsNoTracking().Where(x=>allowed==null||allowed.Contains(x.Id)).ToListAsync(ct); var programIds=programs.Select(x=>x.Id).ToList(); var projects=await db.Projects.AsNoTracking().Where(x=>programIds.Contains(x.ProgramId)).ToListAsync(ct); return Results.Ok(new
        {
            programs, projects,
            releases = await db.Releases.AsNoTracking().Where(x=>projects.Select(p=>p.Id).Contains(x.ProjectId)).OrderBy(x => x.Version).ToListAsync(ct)
        }); });

        app.MapGet("/api/build-context", async (Guid projectId, Guid releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId && x.ProjectId == projectId, ct);
            if (release is null) return Results.NotFound(new { error = "The selected build does not exist in this project." });
            var effectiveBaselineId = await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId, ct);
            var effectiveBaseline = effectiveBaselineId is null
                ? null
                : await (from baseline in db.CandidateBaselines.AsNoTracking()
                         join origin in db.Releases.AsNoTracking() on baseline.ReleaseId equals origin.Id
                         where baseline.Id == effectiveBaselineId
                         select new { baseline.Id, baseline.BaseNumber, baseline.Revision, baseline.Name, baseline.RequirementsMaterializedAt, ReleaseId = origin.Id, ReleaseVersion = origin.Version }).SingleAsync(ct);
            return Results.Ok(new
            {
                projectId,
                releaseId = release.Id,
                release.Version,
                release.IsReleased,
                release.PredecessorReleaseId,
                effectiveBaselineId,
                effectiveBaseline,
                inheritedBaseline = effectiveBaseline is not null && effectiveBaseline.ReleaseId != release.Id
            });
        });

        app.MapGet("/api/release-planning", async (Guid projectId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
            var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.PredecessorBaselineId, x.DisplayNumber, x.Name, state = x.State.ToString(), x.RequirementsMaterializedAt, selectionCount = x.Selections.Count }).ToListAsync(ct);
            var campaigns = await db.ReleaseCampaigns.AsNoTracking().Where(x => x.ProjectId == projectId)
                .Select(x => new { x.Id, x.ReleaseId, x.BaselineId, state = x.State.ToString(), x.Name }).ToListAsync(ct);
            var changes = await db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId)
                .GroupBy(x => new { x.TargetReleaseId, x.State }).Select(x => new { releaseId = x.Key.TargetReleaseId, state = x.Key.State.ToString(), count = x.Count() }).ToListAsync(ct);
            return Results.Ok(new { releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased, x.ReleasedAt, x.PredecessorReleaseId }), baselines, campaigns, changes });
        });

        app.MapPost("/api/releases", async (CreateReleaseRequest request, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            if (!await http.HasProjectRoleAsync(db, identity, request.ProjectId, ct, ProgramRole.ConfigurationManager, ProgramRole.ProgramManager)) return Results.Forbid();
            var version = request.Version.Trim();
            if (string.IsNullOrWhiteSpace(version)) return Results.BadRequest(new { error = "A release version is required." });
            var current = await db.Releases.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == request.ProjectId && !x.IsReleased, ct);
            if (current is not null) return Results.Conflict(new { error = $"Release {current.Version} is still in work. Release or formally close it before planning its successor." });
            if (await db.Releases.AnyAsync(x => x.ProjectId == request.ProjectId && x.Version.ToLower() == version.ToLower(), ct)) return Results.Conflict(new { error = $"Release {version} already exists in this project." });
            if (request.PredecessorReleaseId is not null)
            {
                var predecessor = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PredecessorReleaseId && x.ProjectId == request.ProjectId, ct);
                if (predecessor is null) return Results.BadRequest(new { error = "The predecessor release does not belong to this project." });
                if (!predecessor.IsReleased) return Results.BadRequest(new { error = "A successor release can only branch from a released product version." });
            }
            var release = new SoftwareRelease(request.ProjectId, version, false, request.PredecessorReleaseId); db.Releases.Add(release);
            var actor = http.UserAccount(); db.SecurityAuditEvents.Add(new SecurityAuditEvent("ReleaseCreated", actor.UserName, $"Release:{release.Id}", "Success", $"Created in-work release {version} from predecessor {request.PredecessorReleaseId?.ToString() ?? "none"}.", http.Connection.RemoteIpAddress?.ToString() ?? "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct); return Results.Created($"/api/releases/{release.Id}", new { release.Id, release.Version, release.IsReleased, request.PredecessorReleaseId });
        });

        app.MapGet("/api/showcase/overview", async (Guid projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            if (!await http.HasProjectAccessAsync(db, projectId, ct)) return Results.Forbid();
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.Version).ToListAsync(ct);
            var selectedReleaseIds = releaseId is null ? releases.Select(x => x.Id).ToArray() : [releaseId.Value];
            var requests = db.SystemChangeRequests.AsNoTracking().Where(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.TargetReleaseId));
            var effectiveBaselineId = releaseId is null ? null : await BuildScope.EffectiveBaselineAsync(db, projectId, releaseId.Value, ct);
            var revisionIds = effectiveBaselineId is null
                ? []
                : await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == effectiveBaselineId).Select(x => x.RevisionId).ToListAsync(ct);
            var artifactIds = effectiveBaselineId is null
                ? await db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.Id).ToListAsync(ct)
                : await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == effectiveBaselineId).Select(x => x.ArtifactId).ToListAsync(ct);
            var requirements = db.Requirements.AsNoTracking().Where(x => artifactIds.Contains(x.Id));
            var procedureEffectivity = releaseId is null
                ? null
                : await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct);
            var procedureIds = procedureEffectivity is not null
                ? procedureEffectivity.ProcedureIds.ToList()
                : await (from coverage in db.TestCoverage.AsNoTracking().Where(x => revisionIds.Contains(x.RequirementRevisionId))
                         join procedureRevision in db.TestProcedureRevisions.AsNoTracking() on coverage.ProcedureRevisionId equals procedureRevision.Id
                         select procedureRevision.ProcedureId).Distinct().ToListAsync(ct);
            var executionBuildIds = await db.SoftwareBuilds.AsNoTracking().Where(x => selectedReleaseIds.Contains(x.ReleaseId)).Select(x => x.Id).ToListAsync(ct);
            return Results.Ok(new {
                releases = releases.Select(x => new { x.Id, x.Version, x.IsReleased }),
                systemRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.System, ct),
                highLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.HighLevel, ct),
                lowLevelRequirements = await requirements.CountAsync(x => x.Level == RequirementLevel.LowLevel, ct),
                historicalScrs = await requests.CountAsync(x => x.Type == ChangeRequestType.System, ct),
                historicalSwcrs = await requests.CountAsync(x => x.Type == ChangeRequestType.Software, ct),
                activeRequests = await requests.CountAsync(x => x.State != ChangeRequestState.Deferred, ct),
                traceLinks = await db.RequirementTraces.CountAsync(x => revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId), ct),
                testProcedures = await db.TestProcedures.CountAsync(x => procedureIds.Contains(x.Id), ct),
                testExecutions = await db.TestExecutions.CountAsync(x => x.SoftwareBuildId != null && executionBuildIds.Contains(x.SoftwareBuildId.Value), ct),
                controlledDocuments = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.ReleaseId), ct),
                softwareBuilds = await db.SoftwareBuilds.CountAsync(x => x.ProjectId == projectId && selectedReleaseIds.Contains(x.ReleaseId), ct)
            });
        });

        app.MapGet("/api/dashboard", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount();
            if (projectId is not null && !await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Forbid();
            var allowedProjects = actor.IsAdministrator ? null : await db.Projects.AsNoTracking().Where(x => actor.Programs.Select(p => p.ProgramId).Contains(x.ProgramId)).Select(x => x.Id).ToListAsync(ct);
            var source = db.SystemChangeRequests.AsNoTracking().Where(x => (allowedProjects == null || allowedProjects.Contains(x.ProjectId)) && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId));
            var requests = await source.Select(x => new { x.Id, x.Type, x.State }).ToListAsync(ct);
            var requestIds = requests.Select(x => x.Id).ToList();
            var impacts = await db.VerificationImpactItems.AsNoTracking()
                .Where(x => requestIds.Contains(x.ChangeRequestId))
                .Select(x => new { x.ChangeRequestId, x.RequirementChangeId, x.ProcedureId, x.State })
                .ToListAsync(ct);
            var requirementChangeIds = impacts.Where(x => x.RequirementChangeId is not null).Select(x => x.RequirementChangeId!.Value).ToList();
            var requirementLevels = await db.RequirementChanges.AsNoTracking().Where(x => requirementChangeIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
            var procedureIds = impacts.Where(x => x.ProcedureId is not null).Select(x => x.ProcedureId!.Value).ToList();
            var procedureLevels = await db.TestProcedures.AsNoTracking().Where(x => procedureIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Level }).ToDictionaryAsync(x => x.Id, x => x.Level, ct);
            ChangeDashboardSummary ChangeSummary(ChangeRequestType type)
            {
                var rows = requests.Where(x => x.Type == type).ToList();
                return new(rows.Count, rows.Count(x => x.State == ChangeRequestState.Draft), rows.Count(x => x.State == ChangeRequestState.InReview),
                    rows.Count(x => x.State is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline), rows.Count(x => x.State == ChangeRequestState.Deferred));
            }
            VerificationDashboardSummary VerificationSummary(string area)
            {
                var areaRequestIds = requests.Where(x => area == "System" ? x.Type == ChangeRequestType.System : x.Type == ChangeRequestType.Software).Select(x => x.Id).ToHashSet();
                var rows = impacts.Where(x =>
                {
                    if (!areaRequestIds.Contains(x.ChangeRequestId)) return false;
                    if (area == "System") return true;
                    if (x.RequirementChangeId is Guid requirementChangeId && requirementLevels.TryGetValue(requirementChangeId, out var requirementLevel))
                        return area == "HLR" ? requirementLevel == RequirementLevel.HighLevel : requirementLevel == RequirementLevel.LowLevel;
                    if (x.ProcedureId is Guid procedureId && procedureLevels.TryGetValue(procedureId, out var procedureLevel))
                        return area == "HLR" ? procedureLevel == TestProcedureLevel.HighLevel : procedureLevel == TestProcedureLevel.LowLevel;
                    return false;
                }).ToList();
                var current = rows.Where(x => x.State != VerificationImpactState.Superseded).ToList();
                var currentGrouped = current.GroupBy(x => x.ChangeRequestId).ToList();
                return new(currentGrouped.Count, currentGrouped.Count(group => group.All(x => x.State == VerificationImpactState.Resolved)),
                    current.Count(x => x.State != VerificationImpactState.Resolved), current.Count(x => x.State == VerificationImpactState.Resolved));
            }
            return Results.Ok(new {
                system = ChangeSummary(ChangeRequestType.System),
                software = ChangeSummary(ChangeRequestType.Software),
                verification = new {
                    system = VerificationSummary("System"),
                    hlr = VerificationSummary("HLR"),
                    llr = VerificationSummary("LLR")
                }
            });
        });

        app.MapGet("/api/directory", async (Guid? programId, Guid? projectId, string? search, int? limit, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var selectedProgram = programId ?? (projectId is null ? null : await db.Projects.Where(x=>x.Id==projectId).Select(x=>(Guid?)x.ProgramId).SingleOrDefaultAsync(ct));
            if(selectedProgram is null)return Results.BadRequest(new{error="Choose a Program or Project directory context."});
            var actor=http.UserAccount();if(!actor.IsAdministrator&&!actor.Programs.Any(x=>x.ProgramId==selectedProgram.Value))return Results.Forbid();
            var members = await (from membership in db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == selectedProgram)
                                 join user in db.UserAccounts.AsNoTracking().Where(x => x.State == AccountState.Active) on membership.UserId equals user.Id
                                 select new { user.Id, user.UserName, user.DisplayName, user.Email, role = membership.Role.ToString() }).ToListAsync(ct);
            var people=members.GroupBy(x => new { x.Id, x.UserName, x.DisplayName, x.Email }).Select(x => {var roles=x.Select(r=>r.role).Order().ToList();return new{x.Key.Id,x.Key.UserName,x.Key.DisplayName,x.Key.Email,title=DirectoryTitles.For(x.Key.UserName,roles),roles};});
            var q=search?.Trim()??"";
            if(q.Length>0)people=people.Where(x=>x.DisplayName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.UserName.Contains(q,StringComparison.OrdinalIgnoreCase)||x.Email.Contains(q,StringComparison.OrdinalIgnoreCase)||x.title.Contains(q,StringComparison.OrdinalIgnoreCase)||x.roles.Any(r=>r.Contains(q,StringComparison.OrdinalIgnoreCase)));
            // Exact account/display-name matches lead the suggestions. Handles remain hidden in the picker,
            // but typing a known person must not let ten same-titled generated accounts crowd them out.
            return Results.Ok(people.OrderBy(x=>q.Length>0&&!string.Equals(x.UserName,q,StringComparison.OrdinalIgnoreCase)&&!string.Equals(x.DisplayName,q,StringComparison.OrdinalIgnoreCase))
                .ThenBy(x=>q.Length>0&&!x.DisplayName.StartsWith(q,StringComparison.OrdinalIgnoreCase))
                .ThenBy(x=>x.DisplayName).Take(Math.Clamp(limit??50,1,200)));
        });

        app.MapGet("/api/my-work", async (Guid? projectId, Guid? releaseId, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var actor = http.UserAccount(); var now = DateTimeOffset.UtcNow;
            var activeScrSteps = await (from step in db.ApprovalSteps.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ApprovalStepState.Active)
                                        join cycle in db.ReviewCycles.AsNoTracking() on step.ReviewCycleId equals cycle.Id
                                        join scr in db.SystemChangeRequests.AsNoTracking() on cycle.ChangeRequestId equals scr.Id
                                        where (projectId == null || scr.ProjectId == projectId) && (releaseId == null || scr.TargetReleaseId == releaseId)
                                        select new { id = scr.Id, type = "Change request approval", artifact = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision, title = scr.Title, priority = "High", dueAt = cycle.StartedAt.AddDays(5), ageDays = (int)(now - cycle.StartedAt).TotalDays, route = "scr", discipline = scr.Type == ChangeRequestType.Software ? "software" : "system" }).ToListAsync(ct);
            activeScrSteps = activeScrSteps.OrderBy(x => x.dueAt).ToList();
            var releaseSteps = await (from step in db.ReleaseApprovals.AsNoTracking().Where(x => x.ApproverId == actor.UserName && x.State == ReleaseApprovalState.Active)
                                      join campaign in db.ReleaseCampaigns.AsNoTracking() on step.CampaignId equals campaign.Id
                                      where (projectId == null || campaign.ProjectId == projectId) && (releaseId == null || campaign.ReleaseId == releaseId)
                                      select new { id = campaign.Id, type = "Release approval", artifact = campaign.Name, title = "Authorize the controlled release package", priority = "Critical", dueAt = campaign.CreatedAt.AddDays(10), ageDays = (int)(now - campaign.CreatedAt).TotalDays, route = "release" }).ToListAsync(ct);
            releaseSteps = releaseSteps.OrderBy(x => x.dueAt).ToList();
            // Ordered after materialisation: SQLite cannot ORDER BY a DateTimeOffset, and this set is bounded by
            // the drafts one person authored, so sorting in memory costs nothing and works on every provider.
            var authoredDrafts = (await db.SystemChangeRequests.AsNoTracking().Where(x => x.AuthorId == actor.UserName && x.State == ChangeRequestState.Draft && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.TargetReleaseId == releaseId))
                .Select(x => new { id = x.Id, type = "Draft to complete", artifact = x.BaseNumber + "." + (x.Revision < 10 ? "0" : "") + x.Revision, title = x.Title, priority = "Normal", dueAt = x.UpdatedAt.AddDays(10), ageDays = (int)(now - x.UpdatedAt).TotalDays, route = "scr", discipline = x.Type == ChangeRequestType.Software ? "software" : "system" }).ToListAsync(ct))
                .OrderBy(x => x.dueAt).ToList();
            var assignedTestWork = (await db.TestChangeReviews.AsNoTracking().Where(x =>
                    x.AssignedEngineerId == actor.UserName && x.State == TestChangeReviewState.Open
                    && (projectId == null || x.ProjectId == projectId) && (releaseId == null || x.ReleaseId == releaseId))
                .ToListAsync(ct)).OrderBy(x => x.UpdatedAt).Select(x => new
                {
                    id = x.Id,
                    type = "Test change request",
                    artifact = x.DisplayNumber,
                    title = "Resolve verification impact decisions",
                    priority = "High",
                    dueAt = x.UpdatedAt.AddDays(5),
                    ageDays = (int)(now - x.UpdatedAt).TotalDays,
                    route = "testingCoverage",
                    discipline = x.Discipline.ToString()
                }).ToList();
            var tasks = activeScrSteps.Cast<object>().Concat(releaseSteps).Concat(authoredDrafts).Concat(assignedTestWork).ToList();
            return Results.Ok(new { generatedAt = now, summary = new { total = tasks.Count, approvals = activeScrSteps.Count + releaseSteps.Count, overdue = activeScrSteps.Count(x => x.dueAt < now) + releaseSteps.Count(x => x.dueAt < now) + authoredDrafts.Count(x => x.dueAt < now), drafts = authoredDrafts.Count }, tasks });
        });

        // Notifications and Jira emitted paths such as /systems/change-requests/{id}. The client router
        // accepts application routes only beneath /programs/{p}/projects/{pr}/releases/{r}/, so a recipient
        // received a valid-looking link to a controlled record and landed on Not Found. One resolver owns
        // that mapping now, rather than every emitter holding a copy of the URL shape.
        //
        // Deliberately not under /api: this is opened from a mail client, and the session gate answers an
        // unauthenticated /api request with a JSON 401. Missing, unauthorized and unauthenticated all end at
        // the workspace root, so probing cannot distinguish an artifact that exists from one that does not.
        app.MapGet("/open/{kind}/{id:guid}", async (string kind, Guid id, HttpContext http, AeroLinkDbContext db, IdentityService identity, CancellationToken ct) =>
        {
            var user = await identity.ResolveAsync(http.Request.Cookies[IdentityService.CookieName], DateTimeOffset.UtcNow, ct);
            if (user is null) return Results.Redirect("/");
            http.Items["AeroLink.User"] = user;

            var normalized = kind.Trim().ToLowerInvariant();
            Guid? projectId = null, releaseId = null; var tail = "";
            switch (normalized)
            {
                case "scr" or "swcr" or "change-request":
                {
                    var record = await db.SystemChangeRequests.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.TargetReleaseId, x.Type }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId; releaseId = record.TargetReleaseId;
                        tail = $"/{(record.Type == ChangeRequestType.Software ? "software" : "systems")}/change-requests/{id}";
                    }
                    break;
                }
                case "requirement":
                {
                    var record = await db.Requirements.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId;
                        tail = $"/requirements/{id}?discipline={(record.Level == RequirementLevel.System ? "system" : "software")}";
                    }
                    break;
                }
                case "procedure":
                {
                    var record = await db.TestProcedures.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.Level }).SingleOrDefaultAsync(ct);
                    if (record is not null)
                    {
                        projectId = record.ProjectId;
                        tail = record.Level == TestProcedureLevel.System ? "/system-verification" : "/software-verification";
                    }
                    break;
                }
                case "baseline":
                {
                    var record = await db.CandidateBaselines.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; releaseId = record.ReleaseId; tail = "/baselines"; }
                    break;
                }
                case "document":
                {
                    var record = await db.ControlledDocuments.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId, x.ReleaseId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; releaseId = record.ReleaseId; tail = "/traceability"; }
                    break;
                }
                case "problem-report":
                {
                    var record = await db.ProblemReports.AsNoTracking().Where(x => x.Id == id)
                        .Select(x => new { x.ProjectId }).SingleOrDefaultAsync(ct);
                    if (record is not null) { projectId = record.ProjectId; tail = "/problem-reports"; }
                    break;
                }
            }

            if (projectId is null) return Results.Redirect("/");
            if (!await http.HasProjectAccessAsync(db, projectId.Value, ct)) return Results.Redirect("/");
            var programId = await db.Projects.AsNoTracking().Where(x => x.Id == projectId).Select(x => (Guid?)x.ProgramId).SingleOrDefaultAsync(ct);
            if (programId is null) return Results.Redirect("/");
            // A record that does not carry a release of its own opens in the one being worked, which is where
            // the reader would have gone looking for it anyway.
            releaseId ??= await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
                .OrderBy(x => x.IsReleased).ThenByDescending(x => x.Version)
                .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
            if (releaseId is null) return Results.Redirect("/");
            return Results.Redirect($"/programs/{programId}/projects/{projectId}/releases/{releaseId}{tail}");
        });

        // Bounded, Program-scoped universal search. Results are identifiers plus stable IDs;
        // the client owns the durable URL so every result can be opened in a new tab.
        app.MapGet("/api/search",async(Guid projectId,Guid? releaseId,string query,int? limit,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            var q=(query??string.Empty).Trim().ToLowerInvariant();if(q.Length<2)return Results.Ok(new{query,items=Array.Empty<SearchResultDto>()});var identifierQ=q.Length>3&&q[^3]=='.'&&char.IsDigit(q[^2])&&char.IsDigit(q[^1])?q[..^3]:q;var take=Math.Clamp(limit??30,1,50);var items=new List<SearchResultDto>();
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,projectId,releaseId.Value,ct);
            items.AddRange(await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.TargetReleaseId==releaseId)&&(x.BaseNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"change-request",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),x.Type==ChangeRequestType.Software?"software":"system",x.UpdatedAt)).ToListAsync(ct));
            items.AddRange(await db.ProblemReports.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||db.ProblemReportLinks.Any(link=>link.ProblemReportId==x.Id&&link.ArtifactType=="Release"&&link.ArtifactId==releaseId))&&(x.ReportNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q)||x.Problem.ToLower().Contains(q)||x.RootCause.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"problem-report",x.ReportNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,x.State.ToString(),"assurance",x.UpdatedAt)).ToListAsync(ct));
            var requirementRows=effectiveBaselineId is not null
                ? await(from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId) on artifact.Id equals member.ArtifactId join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id where artifact.BaseNumber.ToLower().Contains(identifierQ)||revision.Statement.ToLower().Contains(q)||revision.Rationale.ToLower().Contains(q) select new{artifact.Id,artifact.BaseNumber,artifact.Level,revision.Revision,revision.Statement,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct)
                : await(from artifact in db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)&&(artifact.BaseNumber.ToLower().Contains(identifierQ)||revision.Statement.ToLower().Contains(q)||revision.Rationale.ToLower().Contains(q)) select new{artifact.Id,artifact.BaseNumber,artifact.Level,revision.Revision,revision.Statement,revision.State,revision.CreatedAt}).Take(take).ToListAsync(ct);
            items.AddRange(requirementRows.Select(x=>new SearchResultDto(x.Id,"requirement",$"{x.BaseNumber}.{x.Revision:D2}",x.Statement,x.State.ToString(),x.Level==RequirementLevel.System?"system":"software",x.CreatedAt)));
            items.AddRange(await db.CandidateBaselines.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.BaseNumber.ToLower().Contains(q)||x.Name.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"baseline",x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.BuildNumber.ToLower().Contains(q)||x.Description.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"build",x.BuildNumber,x.Description,x.State.ToString(),"software",x.RecordedAt)).ToListAsync(ct));
            items.AddRange(await db.TestProcedures.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.BaseNumber.ToLower().Contains(q)||x.Title.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"test-procedure",x.BaseNumber,x.Title,"Controlled",x.Level==TestProcedureLevel.System?"system":"software",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.ControlledDocuments.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&(x.DocumentNumber.ToLower().Contains(identifierQ)||x.Title.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"document",x.DocumentNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Title,"Generated",x.Type==ControlledDocumentType.Sysrd?"system":"software",x.GeneratedAt)).ToListAsync(ct));
            items.AddRange(await db.ReleaseCampaigns.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.ReleaseId==releaseId)&&x.Name.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release-campaign",x.Name,x.Name,x.State.ToString(),"configuration",x.CreatedAt)).ToListAsync(ct));
            items.AddRange(await db.Releases.AsNoTracking().Where(x=>x.ProjectId==projectId&&(releaseId==null||x.Id==releaseId)&&x.Version.ToLower().Contains(q)).Take(take).Select(x=>new SearchResultDto(x.Id,"release",x.Version,"Software release "+x.Version,x.IsReleased?"Released":"InWork","configuration",x.ReleasedAt)).ToListAsync(ct));
            var executionRows=await(from execution in db.TestExecutions.AsNoTracking().Where(x=>x.ProjectId==projectId) join revision in db.TestProcedureRevisions.AsNoTracking() on execution.ProcedureRevisionId equals revision.Id join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id where procedure.BaseNumber.ToLower().Contains(q)||procedure.Title.ToLower().Contains(q)||execution.Determination.ToLower().Contains(q)||execution.EvidenceReference.ToLower().Contains(q) select new{execution.Id,identifier=procedure.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,procedure.Title,execution.Outcome,execution.RecordedAt,procedure.Level}).Take(take).ToListAsync(ct);
            items.AddRange(executionRows.Select(x=>new SearchResultDto(x.Id,"test-execution",x.identifier,$"{x.Title} result",x.Outcome.ToString(),x.Level==TestProcedureLevel.System?"system":"software",x.RecordedAt)));
            items.AddRange(await db.EvidenceRecords.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OriginalFileName.ToLower().Contains(q)||x.Sha256.ToLower().Contains(q))).Take(take).Select(x=>new SearchResultDto(x.Id,"evidence",x.OriginalFileName,x.Sha256,"Immutable","verification",x.UploadedAt)).ToListAsync(ct));
            var ordered=items.OrderByDescending(x=>x.Identifier.ToLowerInvariant().Contains(q)).ThenByDescending(x=>x.UpdatedAt).ThenBy(x=>x.Identifier).Take(take).ToList();return Results.Ok(new{query,items=ordered});
        });

        app.MapGet("/api/artifacts/{kind}/{id:guid}",async(string kind,Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            static Dictionary<string,object?> Details(params (string Key,object? Value)[] values)=>values.ToDictionary(x=>x.Key,x=>x.Value);
            var normalized=kind.Trim().ToLowerInvariant();
            if(normalized=="baseline")
            {var item=await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var members=await db.BaselineRequirements.CountAsync(x=>x.BaselineId==id,ct);var changes=await db.BaselineSelections.CountAsync(x=>x.BaselineId==id,ct);var related=(await db.SoftwareBuilds.AsNoTracking().Where(x=>x.BaselineId==id).Select(x=>new RelatedArtifactDto("build",x.Id,x.BuildNumber,x.Description)).ToListAsync(ct));return Results.Ok(new{kind=normalized,item.Id,identifier=item.DisplayNumber,title=item.Name,state=item.State.ToString(),subtitle="Exact candidate baseline manifest",updatedAt=item.FrozenAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("requirementRevisions",members),("selectedChangeRequests",changes),("contentHash",item.ContentHash),("requirementsHash",item.RequirementsHash),("createdAt",item.CreatedAt)),related});}
            if(normalized=="build")
            {var item=await db.SoftwareBuilds.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.BuildNumber,title=item.Description,state=item.State.ToString(),subtitle="Immutable software build provenance",updatedAt=item.ReleasedAt??item.RecordedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("recordedBy",item.RecordedBy),("recordedAt",item.RecordedAt),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="document")
            {var item=await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{item.DocumentNumber}.{item.Revision:D2}",title=item.Title,state="Generated",subtitle=$"{ApiMap.ControlledDocumentTypeLabel(item.Type)} controlled output",updatedAt=item.GeneratedAt,details=Details(("baseline",baseline.DisplayNumber),("artifactCount",item.ArtifactCount),("contentHash",item.ContentHash),("generatedAt",item.GeneratedAt)),related});}
            if(normalized=="test-procedure")
            {var item=await db.TestProcedures.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var revisions=await db.TestProcedureRevisions.AsNoTracking().Where(x=>x.ProcedureId==id).OrderByDescending(x=>x.Revision).ToListAsync(ct);var latest=revisions.FirstOrDefault();var coverage=latest is null?0:await db.TestCoverage.CountAsync(x=>x.ProcedureRevisionId==latest.Id,ct);return Results.Ok(new{kind=normalized,item.Id,identifier=latest is null?item.BaseNumber:$"{item.BaseNumber}.{latest.Revision:D2}",title=item.Title,state=latest?.State.ToString()??"Draft",subtitle=$"{item.Level} verification procedure",updatedAt=latest?.CreatedAt??item.CreatedAt,details=Details(("owner",item.OwnerId),("revisionCount",revisions.Count),("coveredRequirements",coverage),("objective",latest?.Objective),("expectedResult",latest?.ExpectedResult)),related=Array.Empty<RelatedArtifactDto>()});}
            if(normalized=="release-campaign")
            {var item=await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var baseline=await db.CandidateBaselines.AsNoTracking().SingleAsync(x=>x.Id==item.BaselineId,ct);var related=new[]{new RelatedArtifactDto("baseline",baseline.Id,baseline.DisplayNumber,baseline.Name)};return Results.Ok(new{kind=normalized,item.Id,identifier=item.Name,title=item.Name,state=item.State.ToString(),subtitle="Governed release readiness and approval campaign",updatedAt=item.ReleasedAt??item.CreatedAt,details=Details(("releaseId",item.ReleaseId),("baseline",baseline.DisplayNumber),("verificationBuildId",item.SoftwareBuildId),("releaseHash",item.ReleaseHash)),related});}
            if(normalized=="release")
            {var item=await db.Releases.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var related=await db.CandidateBaselines.AsNoTracking().Where(x=>x.ReleaseId==id).Select(x=>new RelatedArtifactDto("baseline",x.Id,x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,x.Name)).ToListAsync(ct);return Results.Ok(new{kind=normalized,item.Id,identifier=item.Version,title="Software release "+item.Version,state=item.IsReleased?"Released":"InWork",subtitle="Explicitly governed product-version record",updatedAt=item.ReleasedAt,details=Details(("predecessorReleaseId",item.PredecessorReleaseId),("releasedAt",item.ReleasedAt)),related});}
            if(normalized=="test-execution")
            {var item=await db.TestExecutions.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var revision=await db.TestProcedureRevisions.AsNoTracking().SingleAsync(x=>x.Id==item.ProcedureRevisionId,ct);var procedure=await db.TestProcedures.AsNoTracking().SingleAsync(x=>x.Id==revision.ProcedureId,ct);var related=new[]{new RelatedArtifactDto("test-procedure",procedure.Id,$"{procedure.BaseNumber}.{revision.Revision:D2}",procedure.Title)};return Results.Ok(new{kind=normalized,item.Id,identifier=$"{procedure.BaseNumber}.{revision.Revision:D2}",title=procedure.Title+" result",state=item.Outcome.ToString(),subtitle="Immutable attributable verification determination",updatedAt=item.RecordedAt,details=Details(("executedBy",item.ExecutedBy),("executedAt",item.ExecutedAt),("configuration",item.Configuration),("determination",item.Determination),("evidenceReference",item.EvidenceReference),("retestOfExecutionId",item.RetestOfExecutionId)),related});}
            if(normalized=="evidence")
            {var item=await db.EvidenceRecords.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var executionIds=await db.TestExecutionEvidence.AsNoTracking().Where(x=>x.EvidenceId==id).Select(x=>x.TestExecutionId).ToListAsync(ct);var related=await db.TestExecutions.AsNoTracking().Where(x=>executionIds.Contains(x.Id)).Select(x=>new RelatedArtifactDto("test-execution",x.Id,x.Id.ToString(),x.Determination)).ToListAsync(ct);return Results.Ok(new{kind=normalized,item.Id,identifier=item.OriginalFileName,title=item.OriginalFileName,state="Immutable",subtitle="Content-addressed verification evidence",updatedAt=item.UploadedAt,details=Details(("sha256",item.Sha256),("contentType",item.ContentType),("size",item.Size),("uploadedBy",item.UploadedBy),("uploadedAt",item.UploadedAt)),related});}
            if(normalized is "problem-report" or "problemreport" or "pr")
            {var item=await db.ProblemReports.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();var links=await db.ProblemReportLinks.AsNoTracking().Where(x=>x.ProblemReportId==id).ToListAsync(ct);var related=links.Select(x=>new RelatedArtifactDto(ProblemReportIntegrationMap.ArtifactKind(x.ArtifactType),x.ArtifactId,x.Relationship,ProblemReportIntegrationMap.ArtifactLabel(x.ArtifactType))).ToList();return Results.Ok(new{kind="problem-report",item.Id,identifier=item.DisplayNumber,title=item.Title,state=item.State.ToString(),subtitle="Controlled problem report with immutable lifecycle evidence",updatedAt=item.UpdatedAt,details=Details(("classification",item.Classification),("severity",item.Severity.ToString()),("priority",item.Priority.ToString()),("reportedBy",item.ReportedBy),("origin",item.Origin),("affectedConfiguration",item.AffectedConfiguration),("rootCause",item.RootCause),("correctiveAction",item.CorrectiveAction),("disposition",item.Disposition?.ToString()),("releaseBlocker",item.IsReleaseBlocker),("waiver",item.WaiverRationale),("verificationExecutionId",item.ResolutionVerificationExecutionId)),related});}
            return Results.NotFound();
        });

        // Exclusive controlled editing for SCR/SWCR Drafts. The pre-existing enterprise
        // merge endpoints remain available for artifacts configured for optimistic editing.
    }
}

internal sealed record ChangeDashboardSummary(int Total, int Draft, int InReview, int Approved, int Deferred);
internal sealed record VerificationDashboardSummary(int TotalChangeRequests, int TriagedChangeRequests, int OpenDecisions, int ResolvedDecisions);
