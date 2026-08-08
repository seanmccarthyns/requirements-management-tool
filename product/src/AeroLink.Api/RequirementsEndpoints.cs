using System.Text.Json.Nodes;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Contracts;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AeroLink.Api;

/// <summary>
/// The requirements workspace: schemas, specifications, saved views, comments, imports, and the
/// bulk operations a team of engineers spends its day in.
///
/// Requirements are read-only here by design. Every change to one arrives through a controlled change
/// request, so these endpoints author the structure and the discussion around requirements rather than the
/// requirements themselves.
/// </summary>
public static class RequirementsEndpoints
{
    public static void MapRequirementsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/requirements", async (Guid projectId, string? search, Guid? releaseId, Guid? baselineId, string? scope, bool? includeRetired, int? page, int? pageSize, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var resolvedPage=Math.Max(1,page??1);var resolvedPageSize=Math.Clamp(pageSize??50,1,200);
            var source = from artifact in db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId)
                         join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                         join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id
                         select new { artifact, revision, scr };
            if(string.Equals(scope,"System",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.System);
            else if(string.Equals(scope,"Software",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.HighLevel||x.artifact.Level==RequirementLevel.LowLevel);
            else if(string.Equals(scope,"HighLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.HighLevel);
            else if(string.Equals(scope,"LowLevelSoftware",StringComparison.OrdinalIgnoreCase))source=source.Where(x=>x.artifact.Level==RequirementLevel.LowLevel);
            if (baselineId is not null) source = source.Where(x => db.BaselineRequirements.Any(m => m.BaselineId == baselineId && m.RevisionId == x.revision.Id));
            else if (includeRetired != true) source = source.Where(x => x.revision.State == AeroLink.Domain.Requirements.RequirementRevisionState.Active);
            if (releaseId is not null) source = source.Where(x => db.CandidateBaselines.Any(b => b.Id == x.revision.EffectiveBaselineId && b.ReleaseId == releaseId));
            if (!string.IsNullOrWhiteSpace(search)) { var q = search.Trim().ToLower(); source = source.Where(x => x.artifact.BaseNumber.ToLower().Contains(q) || x.revision.Statement.ToLower().Contains(q) || x.revision.Rationale.ToLower().Contains(q)); }
            var total = await source.CountAsync(ct);
            var items = await source.OrderBy(x => x.artifact.BaseNumber).ThenByDescending(x => x.revision.Revision).Skip((resolvedPage - 1) * resolvedPageSize).Take(resolvedPageSize)
                .Select(x => new { x.artifact.Id, x.artifact.BaseNumber, level = x.artifact.Level.ToString(), revisionId = x.revision.Id, x.revision.Revision,
                    displayNumber = x.artifact.BaseNumber + "." + (x.revision.Revision < 10 ? "0" : "") + x.revision.Revision, x.revision.Statement, x.revision.Rationale,
                    x.revision.VerificationMethod, state = x.revision.State.ToString(), x.revision.EffectiveBaselineId, sourceChangeRequestId = x.scr.Id,
                    sourceScr = x.scr.BaseNumber + "." + (x.scr.Revision < 10 ? "0" : "") + x.scr.Revision, x.revision.CreatedAt }).ToListAsync(ct);
            return Results.Ok(new { page=resolvedPage, pageSize=resolvedPageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)resolvedPageSize), items });
        });

        app.MapGet("/api/requirements/{id:guid}/history", async (Guid id, HttpContext http, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var artifact = await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (artifact is null) return Results.NotFound();
            if (!await http.HasProjectAccessAsync(db, artifact.ProjectId, ct)) return Results.Forbid();
            var revisions = await (from revision in db.RequirementRevisions.AsNoTracking().Where(x => x.ArtifactId == id)
                                   join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id
                                   join baseline in db.CandidateBaselines.AsNoTracking() on revision.EffectiveBaselineId equals baseline.Id
                                   orderby revision.Revision descending select new { revision.Id, revision.Revision, displayNumber = artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision,
                                       revision.Statement, revision.Rationale, revision.VerificationMethod, state = revision.State.ToString(), revision.CreatedAt,
                                       sourceChangeRequestId = scr.Id, sourceScr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision,
                                       baselineId = baseline.Id, baseline = baseline.BaseNumber + "." + (baseline.Revision < 10 ? "0" : "") + baseline.Revision }).ToListAsync(ct);
            return Results.Ok(new { artifact.Id, artifact.BaseNumber, level = artifact.Level.ToString(), revisions });
        });

        // Enterprise Requirements Workspace: configurable schemas, structured specifications,
        // collaboration, saved views, governed bulk operations, redlines, and onboarding.
        app.MapGet("/api/enterprise-requirements/workspace", async (Guid projectId, Guid? releaseId, Guid? specificationId, Guid? sectionId, string? search, string? level, string? verification, string? tag,string? state,string? owner,string? sourceScr,Guid? baselineId,bool? openComments,string? coverageState,string? sort,int page, int pageSize,
            HttpContext http, AeroLinkDbContext db, EnterpriseRequirementsService enterprise, CancellationToken ct) =>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            // Direct filters are validated against the same contract a saved view is stored under, so a
            // worklist means the same thing whether it arrives as a query string or as a saved record — and
            // a sort or coverage state this workspace cannot apply is refused rather than silently ignored.
            var submitted=new JsonObject();
            void Submit(string key,string? value){if(RequirementFilterValue.HasValue(value))submitted[key]=value;}
            Submit("search",search);Submit("level",level);Submit("verification",verification);Submit("tag",tag);
            Submit("state",state);Submit("owner",owner);Submit("sourceScr",sourceScr);Submit("coverageState",coverageState);Submit("sort",sort);
            var contract=SavedViewContract.Normalize(submitted.ToJsonString(),"[]");
            if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="requirement_filter_invalid"});

            var timer=Stopwatch.StartNew();
            await enterprise.SynchronizeProjectAsync(projectId,http.UserAccount().UserName,ct);page=Math.Max(1,page==0?1:page);pageSize=Math.Clamp(pageSize==0?100:pageSize,1,250);
            var artifacts=db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId);
            if(string.Equals(level,"Software",StringComparison.OrdinalIgnoreCase))artifacts=artifacts.Where(x=>x.Level==RequirementLevel.HighLevel||x.Level==RequirementLevel.LowLevel);
            else if(!string.IsNullOrWhiteSpace(level)&&Enum.TryParse<RequirementLevel>(level,true,out var parsedLevel))artifacts=artifacts.Where(x=>x.Level==parsedLevel);
            if(specificationId is not null)artifacts=artifacts.Where(x=>db.SpecificationNodes.Any(n=>n.SpecificationId==specificationId&&n.RequirementArtifactId==x.Id));
            // A section is a node inside a specification, and a requirement sits under it as a child node. The
            // headings were rendered as labels with counts beside them and could not be acted on, so a reader
            // could see that a section held forty requirements and had no way to see which forty.
            if(sectionId is not null)artifacts=artifacts.Where(x=>db.SpecificationNodes.Any(n=>n.ParentId==sectionId&&n.RequirementArtifactId==x.Id));
            var effectiveBaselineId=baselineId??(releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,projectId,releaseId.Value,ct));
            var procedureEffectivity = releaseId is not null
                ? await TestProcedureEffectivity.ForReleaseAsync(db, projectId, releaseId.Value, ct)
                : effectiveBaselineId is not null
                    ? await TestProcedureEffectivity.ForBaselineAsync(db, effectiveBaselineId.Value, ct)
                    : null;
            var effectiveProcedureRevisionIds = procedureEffectivity?.RevisionIds;
            var isExactProcedureSnapshot = procedureEffectivity is not null && (releaseId is null ||
                await db.CandidateBaselines.AsNoTracking().AnyAsync(x =>
                    x.Id == procedureEffectivity.BaselineId && x.ReleaseId == releaseId.Value, ct));
            var current=effectiveBaselineId is not null
                ? from artifact in artifacts
                  join member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId) on artifact.Id equals member.ArtifactId
                  join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                  select new{artifact,revision}
                : from artifact in artifacts
                  join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId
                  where revision.Revision==db.RequirementRevisions.Where(r=>r.ArtifactId==artifact.Id).Max(r=>r.Revision)
                  select new{artifact,revision};
            if(!string.IsNullOrWhiteSpace(search)){var q=search.Trim().ToLower();current=current.Where(x=>x.artifact.BaseNumber.ToLower().Contains(q)||x.revision.Statement.ToLower().Contains(q)||x.revision.Rationale.ToLower().Contains(q));}
            if(!string.IsNullOrWhiteSpace(verification)){var v=verification.Trim().ToLower();current=current.Where(x=>x.revision.VerificationMethod.ToLower()==v);}
            // Exact tag membership against the normalized index, not a substring of the serialized array —
            // the tag "safe" matched every requirement tagged "failsafe", and a leading-wildcard scan over
            // raw JSON can use no index at all.
            if(RequirementFilterValue.HasValue(tag)){var t=RequirementFilterValue.Normalize(tag);current=current.Where(x=>db.RequirementRevisionTags.Any(p=>p.RevisionId==x.revision.Id&&p.Tag==t));}
            if(!string.IsNullOrWhiteSpace(state)&&Enum.TryParse<RequirementRevisionState>(state,true,out var parsedState))current=current.Where(x=>x.revision.State==parsedState);
            // The declared owner field, exactly, rather than any attribute value that happens to contain it.
            if(RequirementFilterValue.HasValue(owner)){var o=RequirementFilterValue.Normalize(owner);current=current.Where(x=>db.RequirementRevisionProfiles.Any(p=>p.RevisionId==x.revision.Id&&p.Owner==o));}
            if(!string.IsNullOrWhiteSpace(sourceScr)){var s=sourceScr.Trim().ToLower();current=current.Where(x=>db.SystemChangeRequests.Any(scr=>scr.Id==x.revision.SourceChangeRequestId&&(scr.BaseNumber.ToLower().Contains(s)||scr.Title.ToLower().Contains(s))));}
            if(baselineId is not null)current=current.Where(x=>db.BaselineRequirements.Any(b=>b.BaselineId==baselineId&&b.RevisionId==x.revision.Id));
            if(openComments==true)current=current.Where(x=>db.ArtifactComments.Any(c=>c.ArtifactId==x.artifact.Id&&c.ArtifactType=="Requirement"&&c.State==CollaborationState.Open));
            // Which requirements are uncovered, or covered only by something that no longer counts, was a
            // question the workspace could not answer at all — it filtered on the verification *method* an
            // author declared, which says what kind of evidence is intended and nothing about whether any
            // exists. Both subqueries stay composable so this filters in the database alongside every other
            // predicate, before the count and the page.
            if(!string.IsNullOrWhiteSpace(coverageState)&&RequirementCoverageState.TryParse(coverageState,out var parsedCoverage))
            {
                var settled=VerificationCoverageProjection.SettledCoveredRequirementRevisionIds(db,effectiveProcedureRevisionIds,isExactProcedureSnapshot);var linked=VerificationCoverageProjection.LinkedRequirementRevisionIds(db,effectiveProcedureRevisionIds);
                current=parsedCoverage switch{
                    RequirementCoverageState.Covered=>current.Where(x=>settled.Contains(x.revision.Id)),
                    RequirementCoverageState.Suspect=>current.Where(x=>!settled.Contains(x.revision.Id)&&linked.Contains(x.revision.Id)),
                    _=>current.Where(x=>!linked.Contains(x.revision.Id))};
            }
            var ordered=sort?.ToLowerInvariant() switch{"updated" when !db.Database.IsSqlite()=>current.OrderByDescending(x=>x.revision.CreatedAt).ThenBy(x=>x.artifact.BaseNumber),"verification"=>current.OrderBy(x=>x.revision.VerificationMethod).ThenBy(x=>x.artifact.BaseNumber),"state"=>current.OrderBy(x=>x.revision.State).ThenBy(x=>x.artifact.BaseNumber),_=>current.OrderBy(x=>x.artifact.BaseNumber)};
            var total=await current.CountAsync(ct);var rows=await ordered.Skip((page-1)*pageSize).Take(pageSize)
                .Select(x=>new{x.artifact.Id,x.artifact.BaseNumber,level=x.artifact.Level.ToString(),revisionId=x.revision.Id,x.revision.Revision,x.revision.Statement,x.revision.Rationale,x.revision.VerificationMethod,state=x.revision.State.ToString(),x.revision.SourceChangeRequestId,x.revision.CreatedAt}).ToListAsync(ct);
            var revisionIds=rows.Select(x=>x.revisionId).ToList();
            // The controlled number of the change request that authorized each revision. The inspector names
            // its source authority after this rather than after the workspace it is being read in — a fixed
            // A fixed "Open SCR" was wrong every time it appeared on an HLR or LLR, whose authority is an HLRCR or LLRCR.
            var sourceScrIds=rows.Select(x=>x.SourceChangeRequestId).Distinct().ToList();
            var sourceNumbers=await db.SystemChangeRequests.AsNoTracking().Where(x=>sourceScrIds.Contains(x.Id))
                .Select(x=>new{x.Id,x.BaseNumber,x.Revision}).ToDictionaryAsync(x=>x.Id,x=>x.BaseNumber+"."+(x.Revision<10?"0":"")+x.Revision,ct);
            var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToDictionaryAsync(x=>x.RevisionId,ct);
            var coverageStates=await VerificationCoverageProjection.StatesAsync(db,revisionIds,ct,effectiveProcedureRevisionIds,isExactProcedureSnapshot);
            var commentCounts=await db.ArtifactComments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.ArtifactType=="Requirement"&&rows.Select(r=>r.Id).Contains(x.ArtifactId)).GroupBy(x=>x.ArtifactId).Select(x=>new{x.Key,Count=x.Count(),Open=x.Count(c=>c.State==CollaborationState.Open)}).ToDictionaryAsync(x=>x.Key,ct);
            var schemas=await db.ArtifactSchemas.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.IsActive).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Key,x.Name,x.AppliesTo,x.Description,x.Version,fields=x.Fields.OrderBy(f=>f.SortOrder).Select(f=>new{f.Id,f.Key,f.Label,type=f.Type.ToString(),f.IsRequired,f.SortOrder,f.OptionsJson})}).ToListAsync(ct);
            var specificationRows=await db.RequirementSpecifications.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Level).Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,nodeCount=db.SpecificationNodes.Count(n=>n.SpecificationId==x.Id&&n.Type==SpecificationNodeType.Requirement)}).ToListAsync(ct);
            var specificationIds=specificationRows.Select(x=>x.Id).ToList();var sectionRows=await db.SpecificationNodes.AsNoTracking().Where(n=>specificationIds.Contains(n.SpecificationId)&&n.Type==SpecificationNodeType.Section).OrderBy(n=>n.Position).Select(n=>new{n.Id,n.SpecificationId,n.Heading,n.Position,count=db.SpecificationNodes.Count(c=>c.ParentId==n.Id)}).ToListAsync(ct);
            var specifications=specificationRows.Select(x=>new{x.Id,x.DocumentNumber,x.Title,x.Level,x.Description,x.nodeCount,sections=sectionRows.Where(s=>s.SpecificationId==x.Id).Select(s=>new{s.Id,s.Heading,s.Position,s.count})}).ToList();
            var views=await db.SavedRequirementViews.AsNoTracking().Where(x=>x.ProjectId==projectId&&(x.OwnerId==http.UserAccount().Id||x.IsShared)).OrderBy(x=>x.Name).Select(x=>new{x.Id,x.Name,x.QueryJson,x.ColumnsJson,x.IsShared,owned=x.OwnerId==http.UserAccount().Id}).ToListAsync(ct);
            var build=releaseId is null?null:await db.Releases.AsNoTracking().Where(x=>x.Id==releaseId&&x.ProjectId==projectId).Select(x=>new{x.Id,x.Version,x.IsReleased}).SingleOrDefaultAsync(ct);
            timer.Stop();return Results.Ok(new{page,pageSize,totalCount=total,totalPages=(int)Math.Ceiling(total/(double)pageSize),queryElapsedMs=timer.ElapsedMilliseconds,effectiveBaselineId,build,schemas,specifications,views,items=rows.Select(x=>{profiles.TryGetValue(x.revisionId,out var profile);commentCounts.TryGetValue(x.Id,out var comments);return new{x.Id,x.BaseNumber,displayNumber=$"{x.BaseNumber}.{x.Revision:D2}",x.level,x.revisionId,x.Revision,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.SourceChangeRequestId,sourceScr=sourceNumbers.TryGetValue(x.SourceChangeRequestId,out var sourceNumber)?sourceNumber:"",x.CreatedAt,richText=profile?.RichText??x.Statement,attributesJson=profile?.AttributesJson??"{}",tagsJson=profile?.TagsJson??"[]",commentCount=comments?.Count??0,openCommentCount=comments?.Open??0,coverageState=coverageStates.TryGetValue(x.revisionId,out var rowCoverage)?rowCoverage:RequirementCoverageState.Uncovered};})});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}", async (Guid artifactId,Guid? releaseId,HttpContext http,AeroLinkDbContext db,CancellationToken ct) =>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();
            if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,artifact.ProjectId,releaseId.Value,ct);
            if(releaseId is not null&&(effectiveBaselineId is null||!await db.BaselineRequirements.AnyAsync(x=>x.BaselineId==effectiveBaselineId&&x.ArtifactId==artifactId,ct)))return Results.NotFound(new{error="This requirement is not primary content in the active build.",code="cross_build_requirement"});
            var history=await (from r in db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId)
                               join s in db.SystemChangeRequests.AsNoTracking() on r.SourceChangeRequestId equals s.Id
                               join b in db.CandidateBaselines.AsNoTracking() on r.EffectiveBaselineId equals b.Id
                               join release in db.Releases.AsNoTracking() on b.ReleaseId equals release.Id
                               orderby r.Revision descending select new{r.Id,r.Revision,displayNumber=artifact.BaseNumber+"."+(r.Revision<10?"0":"")+r.Revision,r.Statement,r.Rationale,r.VerificationMethod,state=r.State.ToString(),r.SourceChangeRequestId,sourceScr=s.BaseNumber+"."+(s.Revision<10?"0":"")+s.Revision,r.CreatedAt,originBuild=release.Version,isHistorical=releaseId!=null&&release.Id!=releaseId}).ToListAsync(ct);
            var revisionIds=history.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
            var placements=await (from n in db.SpecificationNodes.AsNoTracking().Where(x=>x.RequirementArtifactId==artifactId) join spec in db.RequirementSpecifications.AsNoTracking() on n.SpecificationId equals spec.Id join parent in db.SpecificationNodes.AsNoTracking() on n.ParentId equals parent.Id select new{spec.Id,spec.DocumentNumber,spec.Title,section=parent.Heading,n.Position}).ToListAsync(ct);
            var procedureEffectivity=releaseId is null?null:await TestProcedureEffectivity.ForReleaseAsync(db,artifact.ProjectId,releaseId.Value,ct);var effectiveProcedureRevisionIds=procedureEffectivity?.RevisionIds;
            var traces=await db.RequirementTraces.AsNoTracking().CountAsync(x=>revisionIds.Contains(x.SourceRevisionId)||revisionIds.Contains(x.TargetRevisionId),ct);var testSource=db.TestCoverage.AsNoTracking().Where(x=>revisionIds.Contains(x.RequirementRevisionId));if(effectiveProcedureRevisionIds is not null)testSource=testSource.Where(x=>effectiveProcedureRevisionIds.Contains(x.ProcedureRevisionId));var tests=await testSource.CountAsync(ct);
            return Results.Ok(new{artifact.Id,artifact.BaseNumber,level=artifact.Level.ToString(),activeBuildId=releaseId,effectiveBaselineId,history=history.Select(x=>new{x.Id,x.Revision,x.displayNumber,x.Statement,x.Rationale,x.VerificationMethod,x.state,x.SourceChangeRequestId,x.sourceScr,x.CreatedAt,x.originBuild,x.isHistorical,richText=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.RichText,attributesJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.AttributesJson??"{}",tagsJson=profiles.SingleOrDefault(p=>p.RevisionId==x.Id)?.TagsJson??"[]"}),placements,traceCount=traces,testCoverageCount=tests});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/redline",async(Guid artifactId,Guid fromRevisionId,Guid toRevisionId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var projectId=await db.Requirements.Where(x=>x.Id==artifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.Id==fromRevisionId||x.Id==toRevisionId)).ToListAsync(ct);if(revisions.Count!=2)return Results.BadRequest(new{error="Select two revisions of the same requirement."});var from=revisions.Single(x=>x.Id==fromRevisionId);var to=revisions.Single(x=>x.Id==toRevisionId);
            var profiles=await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId).ToListAsync(ct);var fromProfile=profiles.SingleOrDefault(x=>x.RevisionId==fromRevisionId);var toProfile=profiles.SingleOrDefault(x=>x.RevisionId==toRevisionId);
            var files=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&(x.RevisionId==fromRevisionId||x.RevisionId==toRevisionId)).ToListAsync(ct);var attachmentChanges=files.Select(x=>new{x.Id,x.LogicalId,x.Version,x.Label,x.OriginalFileName,x.Sha256,kind=x.RevisionId==toRevisionId?"added":"removed"}).ToList();
            return Results.Ok(new{from=from.Revision,to=to.Revision,statement=EnterpriseRequirementsService.Diff(from.Statement,to.Statement),rationale=EnterpriseRequirementsService.Diff(from.Rationale,to.Rationale),richText=EnterpriseRequirementsService.Diff(fromProfile?.RichText??from.Statement,toProfile?.RichText??to.Statement),attributesChanged=(fromProfile?.AttributesJson??"{}")!=(toProfile?.AttributesJson??"{}"),fromAttributes=fromProfile?.AttributesJson??"{}",toAttributes=toProfile?.AttributesJson??"{}",verificationChanged=from.VerificationMethod!=to.VerificationMethod,fromVerification=from.VerificationMethod,toVerification=to.VerificationMethod,attachmentChanges});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/impact",async(Guid artifactId,Guid? releaseId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();
            var effectiveBaselineId=releaseId is null?null:await BuildScope.EffectiveBaselineAsync(db,artifact.ProjectId,releaseId.Value,ct);
            var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId).ToListAsync(ct);
            var effectiveRevisionId=effectiveBaselineId is null?null:await db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==effectiveBaselineId&&x.ArtifactId==artifactId).Select(x=>(Guid?)x.RevisionId).SingleOrDefaultAsync(ct);
            var current=effectiveBaselineId is null
                ? revisions.OrderByDescending(x=>x.Revision).First()
                : revisions.SingleOrDefault(x=>x.Id==effectiveRevisionId);
            if(current is null)return Results.NotFound(new{error="This requirement is not primary content in the active build.",code="cross_build_requirement"});
            var parents=await (from link in db.RequirementTraces.AsNoTracking().Where(x=>x.SourceRevisionId==current.Id) join revision in db.RequirementRevisions.AsNoTracking() on link.TargetRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
            var children=await (from link in db.RequirementTraces.AsNoTracking().Where(x=>x.TargetRevisionId==current.Id) join revision in db.RequirementRevisions.AsNoTracking() on link.SourceRevisionId equals revision.Id join related in db.Requirements.AsNoTracking() on revision.ArtifactId equals related.Id select new{related.Id,displayNumber=related.BaseNumber+"."+(revision.Revision<10?"0":"")+revision.Revision,level=related.Level.ToString(),revision.Statement,type=link.Type.ToString(),link.Rationale}).ToListAsync(ct);
            var procedureEffectivity=releaseId is null?null:await TestProcedureEffectivity.ForReleaseAsync(db,artifact.ProjectId,releaseId.Value,ct);
            var isExactProcedureSnapshot=procedureEffectivity is not null&&await db.CandidateBaselines.AsNoTracking().AnyAsync(x=>x.Id==procedureEffectivity.BaselineId&&x.ReleaseId==releaseId,ct);
            var coverageLinks=await VerificationCoverageProjection.ForRequirementRevisionsAsync(db,[current.Id],ct,isExactProcedureSnapshot,procedureEffectivity?.RevisionIds);
            var tests=coverageLinks.Select(x=>new{id=x.ProcedureId,revisionId=x.ProcedureRevisionId,x.DisplayNumber,x.Title,x.Level,state=x.ProcedureState,x.IsSuspect,x.CoverageState}).ToList();
            var baselines=await (from selection in db.BaselineRequirements.AsNoTracking().Where(x=>x.ArtifactId==artifactId) join baseline in db.CandidateBaselines.AsNoTracking() on selection.BaselineId equals baseline.Id join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id select new{baseline.Id,baseline=baseline.BaseNumber+"."+(baseline.Revision<10?"0":"")+baseline.Revision,baseline.Name,state=baseline.State.ToString(),release=release.Version,selection.RevisionId}).ToListAsync(ct);
            var baselineIds=baselines.Select(x=>x.Id).ToList();var builds=await db.SoftwareBuilds.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.BuildNumber,x.Description,state=x.State.ToString()}).ToListAsync(ct);var documents=await db.ControlledDocuments.AsNoTracking().Where(x=>baselineIds.Contains(x.BaselineId)).Select(x=>new{x.Id,x.DocumentNumber,x.Revision,x.Title,type=x.Type.ToString(),x.ContentHash}).ToListAsync(ct);
            var activeChanges=await (from change in db.RequirementChanges.AsNoTracking().Where(x=>x.BaseNumber==artifact.BaseNumber) join scr in db.SystemChangeRequests.AsNoTracking() on change.ChangeRequestId equals scr.Id where scr.State==ChangeRequestState.Draft||scr.State==ChangeRequestState.InReview||scr.State==ChangeRequestState.Approved select new{scr.Id,displayNumber=scr.BaseNumber+"."+(scr.Revision<10?"0":"")+scr.Revision,scr.Title,state=scr.State.ToString(),kind=change.Kind.ToString(),proposedRevision=change.Revision}).ToListAsync(ct);
            var openComments=await db.ArtifactComments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==CollaborationState.Open,ct);var openAssignments=await db.ArtifactAssignments.AsNoTracking().CountAsync(x=>x.ArtifactId==artifactId&&x.State==AssignmentState.Open,ct);
            var confirmedCoverage=coverageLinks.Count(x=>x.CoverageState=="Confirmed");
            var categories=new[]{new{key="trace",label="Trace relationships",count=parents.Count+children.Count,needsAction=parents.Count+children.Count==0},new{key="verification",label="Verification coverage",count=confirmedCoverage,needsAction=confirmedCoverage==0},new{key="baseline",label="Baselines and builds",count=baselines.Count+builds.Count,needsAction=false},new{key="document",label="Controlled documents",count=documents.Count,needsAction=false},new{key="collaboration",label="Open collaboration",count=openComments+openAssignments,needsAction=openComments+openAssignments>0}};
            return Results.Ok(new{artifact.Id,artifact.BaseNumber,currentRevision=current.Revision,requirementRevisionId=current.Id,displayNumber=artifact.BaseNumber+"."+(current.Revision<10?"0":"")+current.Revision,parents,children,tests,baselines,builds,documents,activeChanges,openComments,openAssignments,categories});
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/propose",async(Guid artifactId,ProposeRequirementChangeRequest request,HttpContext http,AeroLinkDbContext db,IChangeRequestRepository repository,IdentityService identity,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(request.Kind is not (RequirementChangeKind.Modify or RequirementChangeKind.Retire))return Results.BadRequest(new{error="An existing requirement can only be modified or retired."});if(!await http.HasProjectRoleAsync(db,identity,artifact.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();if(!await db.Releases.AnyAsync(x=>x.Id==request.TargetReleaseId&&x.ProjectId==artifact.ProjectId&&!x.IsReleased,ct))return Results.BadRequest(new{error="Select an unreleased target release from this Project."});
            var principal=http.UserAccount();var actor=principal.UserName;var current=await db.RequirementRevisions.AsNoTracking().Where(x=>x.ArtifactId==artifactId).OrderByDescending(x=>x.Revision).FirstAsync(ct);var profile=await db.RequirementRevisionProfiles.AsNoTracking().SingleOrDefaultAsync(x=>x.RevisionId==current.Id,ct);SystemChangeRequest scr;
            if(request.ExistingScrId is not null){var existing=await repository.GetAsync(request.ExistingScrId.Value,ct);if(existing is null)return Results.NotFound(new{error="The selected Draft change request was not found."});scr=existing;if(scr.ProjectId!=artifact.ProjectId||scr.TargetReleaseId!=request.TargetReleaseId)return Results.BadRequest(new{error="The selected change request has a different Project or release."});}
            else{var type=artifact.Level==RequirementLevel.System?ChangeRequestType.System:ChangeRequestType.Software;var prefix=type==ChangeRequestType.System?"SCR":"SWCR";var numbers=await db.SystemChangeRequests.AsNoTracking().Where(x=>x.ProjectId==artifact.ProjectId&&x.BaseNumber.StartsWith(prefix+"-")).Select(x=>x.BaseNumber).ToListAsync(ct);var next=numbers.Select(x=>int.TryParse(x[(x.IndexOf('-')+1)..],out var n)?n:0).DefaultIfEmpty().Max()+1;scr=new SystemChangeRequest($"{prefix}-{next:D5}",0,artifact.ProjectId,request.TargetReleaseId,string.IsNullOrWhiteSpace(request.Title)?$"{request.Kind} {artifact.BaseNumber}":request.Title,$"A controlled change is proposed for {artifact.BaseNumber}.",$"Assess parent/child traceability, verification coverage, specifications, software builds, and open collaboration for {artifact.BaseNumber}.",$"Implement the approved {request.Kind.ToString().ToLowerInvariant()} through this exact {prefix} revision.",actor,DateTimeOffset.UtcNow,type);await repository.AddAsync(scr,ct);}
            if(scr.AuthorId!=actor&&!principal.IsAdministrator)return Results.Forbid();if(scr.State!=ChangeRequestState.Draft)return Results.BadRequest(new{error="Requirement proposals can be added only to a Draft change request."});if(scr.RequirementChanges.Any(x=>x.BaseNumber==artifact.BaseNumber))return Results.Conflict(new{error="This Draft already contains a proposal for the selected requirement."});var dispositions=JsonSerializer.Serialize(new{trace="Pending",verification="Pending",documents="Pending",baseline="Pending",collaboration="Pending"});scr.AddRequirementChange(actor,artifact.BaseNumber,current.Revision+1,artifact.Level,request.Kind,request.Kind==RequirementChangeKind.Retire?"":current.Statement,current.Rationale,current.VerificationMethod,DateTimeOffset.UtcNow,profile?.RichText??current.Statement,profile?.AttributesJson??"{}",dispositions,administratorAuthority:principal.IsAdministrator);
            if(!await db.ArtifactWatches.AnyAsync(x=>x.ArtifactId==artifactId&&x.UserName==actor,ct))db.ArtifactWatches.Add(new(artifact.ProjectId,"Requirement",artifactId,actor,actor,DateTimeOffset.UtcNow));try{await repository.SaveAsync(ct);}catch(DbUpdateException){return Results.Conflict(new{error="Another controlled change was created concurrently. Refresh and retry."});}return Results.Created($"/api/change-requests/{scr.Id}",new{scr.Id,scr.DisplayNumber,scr.Title});
        });

        app.MapPost("/api/enterprise-requirements/schemas",async(CreateArtifactSchemaRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Administrator))return Results.Forbid();try{var schema=new ArtifactSchemaDefinition(request.ProjectId,request.Key,request.Name,request.AppliesTo,request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.ArtifactSchemas.Add(schema);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/schemas/{schema.Id}",new{schema.Id});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/schemas/{id:guid}/fields",async(Guid id,CreateSchemaFieldRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var schema=await db.ArtifactSchemas.Include(x=>x.Fields).SingleOrDefaultAsync(x=>x.Id==id,ct);if(schema is null)return Results.NotFound();if(!http.UserAccount().IsAdministrator)return Results.Forbid();try{schema.AddField(request.Key,request.Label,request.Type,request.IsRequired,request.SortOrder,request.OptionsJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/specifications",async(CreateSpecificationRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();try{var spec=new RequirementSpecification(request.ProjectId,request.DocumentNumber,request.Title,request.Level,request.Description,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementSpecifications.Add(spec);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{spec.Id}",new{spec.Id});}catch(Exception ex)when(ex is DomainException or ArgumentException){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/specifications/{id:guid}/sections",async(Guid id,CreateSectionRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            var projectId=await db.RequirementSpecifications.Where(x=>x.Id==id).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,projectId.Value,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();var node=new SpecificationNode(id,request.ParentId,request.Position,SpecificationNodeType.Section,request.Heading,null,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.SpecificationNodes.Add(node);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/specifications/{id}/sections/{node.Id}",new{node.Id});
        });

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/comments",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var projectId=await db.Requirements.Where(x=>x.Id==artifactId).Select(x=>(Guid?)x.ProjectId).SingleOrDefaultAsync(ct);if(projectId is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,projectId.Value,ct))return Results.Forbid();
            var comments=await db.ArtifactComments.AsNoTracking().Where(x=>x.ArtifactId==artifactId&&x.ArtifactType=="Requirement").ToListAsync(ct);
            return Results.Ok(comments.OrderBy(x=>x.CreatedAt).Select(x=>new{x.Id,x.RevisionId,x.ParentCommentId,x.Body,x.MentionsJson,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.ResolvedBy,x.ResolvedAt,x.Disposition}));
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/comments",async(Guid artifactId,CreateCommentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();if(request.RevisionId is not null&&!await db.RequirementRevisions.AnyAsync(x=>x.Id==request.RevisionId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The comment revision is not part of this requirement."});if(request.ParentCommentId is not null&&!await db.ArtifactComments.AnyAsync(x=>x.Id==request.ParentCommentId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The parent comment is not part of this requirement."});try{var actor=http.UserAccount().UserName;var now=DateTimeOffset.UtcNow;var comment=new ArtifactComment(artifact.ProjectId,"Requirement",artifactId,request.RevisionId,request.ParentCommentId,request.Body,JsonSerializer.Serialize(request.Mentions??[]),actor,now);db.ArtifactComments.Add(comment);var requested=(request.Mentions??[]).Select(x=>x.Trim().ToLowerInvariant()).ToHashSet();var watchers=await db.ArtifactWatches.AsNoTracking().Where(x=>x.ArtifactId==artifactId).Select(x=>x.UserName).ToListAsync(ct);requested.UnionWith(watchers);if(request.ParentCommentId is not null){var parentAuthor=await db.ArtifactComments.Where(x=>x.Id==request.ParentCommentId).Select(x=>x.CreatedBy).SingleAsync(ct);requested.Add(parentAuthor.ToLowerInvariant());}var recipients=await db.UserAccounts.AsNoTracking().Where(x=>requested.Contains(x.UserName)&&x.UserName!=actor).Select(x=>x.UserName).ToListAsync(ct);foreach(var recipient in recipients)db.UserNotifications.Add(new(artifact.ProjectId,recipient,"RequirementComment",$"Discussion on {artifact.BaseNumber}",$"{actor}: {request.Body}",$"requirement:{artifactId}",artifactId,now));await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/{artifactId}/comments/{comment.Id}",new{comment.Id,notified=recipients.Count});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/comments/{id:guid}/resolve",async(Guid id,ResolveCommentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {var comment=await db.ArtifactComments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(comment is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,comment.ProjectId,ct))return Results.Forbid();try{comment.Resolve(http.UserAccount().UserName,request.Disposition??"",DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}});

        app.MapGet("/api/enterprise-requirements/{artifactId:guid}/collaboration",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;
            var watchers=await db.ArtifactWatches.AsNoTracking().Where(x=>x.ArtifactId==artifactId).OrderBy(x=>x.UserName).Select(x=>new{x.UserName,x.CreatedAt,isCurrent=x.UserName==actor}).ToListAsync(ct);var assignments=await db.ArtifactAssignments.AsNoTracking().Where(x=>x.ArtifactId==artifactId).ToListAsync(ct);
            return Results.Ok(new{watching=watchers.Any(x=>x.isCurrent),watchers,assignments=assignments.OrderBy(x=>x.State).ThenBy(x=>x.DueAt).Select(x=>new{x.Id,x.CommentId,x.AssignedTo,x.Title,x.Description,x.DueAt,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.UpdatedAt,x.Version,x.CompletedBy})});
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/watch",async(Guid artifactId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;var existing=await db.ArtifactWatches.SingleOrDefaultAsync(x=>x.ArtifactId==artifactId&&x.UserName==actor,ct);if(existing is null)db.ArtifactWatches.Add(new(artifact.ProjectId,"Requirement",artifactId,actor,actor,DateTimeOffset.UtcNow));else db.ArtifactWatches.Remove(existing);await db.SaveChangesAsync(ct);return Results.Ok(new{watching=existing is null});
        });

        app.MapPost("/api/enterprise-requirements/{artifactId:guid}/assignments",async(Guid artifactId,CreateAssignmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var artifact=await db.Requirements.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==artifactId,ct);if(artifact is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,artifact.ProjectId,ct))return Results.Forbid();var assignee=request.AssignedTo.Trim().ToLowerInvariant();if(!await db.UserAccounts.AnyAsync(x=>x.UserName==assignee,ct))return Results.BadRequest(new{error="The assigned AeroLink user does not exist."});if(request.CommentId is not null&&!await db.ArtifactComments.AnyAsync(x=>x.Id==request.CommentId&&x.ArtifactId==artifactId,ct))return Results.BadRequest(new{error="The linked comment is not part of this requirement."});try{var actor=http.UserAccount().UserName;var now=DateTimeOffset.UtcNow;var assignment=new ArtifactAssignment(artifact.ProjectId,"Requirement",artifactId,request.CommentId,assignee,request.Title,request.Description,request.DueAt,actor,now);db.ArtifactAssignments.Add(assignment);db.UserNotifications.Add(new(artifact.ProjectId,assignee,"RequirementAssignment",request.Title,$"{actor} assigned work on {artifact.BaseNumber}.",$"requirement:{artifactId}",artifactId,now));await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/assignments/{assignment.Id}",new{assignment.Id});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        app.MapPost("/api/enterprise-requirements/assignments/{id:guid}/complete",async(Guid id,CompleteAssignmentRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var assignment=await db.ArtifactAssignments.SingleOrDefaultAsync(x=>x.Id==id,ct);if(assignment is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,assignment.ProjectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;if(assignment.AssignedTo!=actor&&!http.UserAccount().IsAdministrator)return Results.Forbid();try{assignment.Complete(actor,request.ExpectedVersion,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();}catch(DomainException ex){return Results.Conflict(new{error=ex.Message});}
        });

        app.MapGet("/api/enterprise-requirements/work-queue",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var actor=http.UserAccount().UserName;var assignments=await db.ArtifactAssignments.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.AssignedTo==actor&&x.State==AssignmentState.Open).ToListAsync(ct);var ids=assignments.Select(x=>x.ArtifactId).ToList();var artifacts=await db.Requirements.AsNoTracking().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,ct);var notifications=await db.UserNotifications.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.Recipient==actor&&x.State==NotificationState.Unread).Take(100).ToListAsync(ct);return Results.Ok(new{assignments=assignments.OrderBy(x=>x.DueAt).Select(x=>new{x.Id,x.ArtifactId,requirement=artifacts.TryGetValue(x.ArtifactId,out var a)?a.BaseNumber:"Requirement",x.Title,x.Description,x.DueAt,x.Version,overdue=x.DueAt<DateTimeOffset.UtcNow}),notifications=notifications.OrderByDescending(x=>x.CreatedAt).Select(x=>new{x.Id,x.Type,x.Title,x.Detail,x.Route,x.ArtifactId,x.CreatedAt})});
        });

        app.MapPost("/api/enterprise-requirements/notifications/{id:guid}/read",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {var notification=await db.UserNotifications.SingleOrDefaultAsync(x=>x.Id==id&&x.Recipient==http.UserAccount().UserName,ct);if(notification is null)return Results.NotFound();notification.MarkRead(DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.NoContent();});

        app.MapPost("/api/enterprise-requirements/views",async(CreateSavedViewRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();
            var name=(request.Name??"").Trim();
            if(name.Length==0)return Results.BadRequest(new{error="A saved view needs a name.",code="saved_view_name_required"});
            // Validated before storage, not on the way out. A view is a worklist somebody else opens, so a
            // field this workspace cannot apply or a column it cannot show must never reach the record.
            var contract=SavedViewContract.Normalize(request.QueryJson,request.ColumnsJson);
            if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="saved_view_contract_invalid"});
            var owner=http.UserAccount().Id;
            // Deliberate rather than incidental: a repeat name is refused and says so, instead of quietly
            // creating the second of two views nobody could tell apart or remove.
            if(await db.SavedRequirementViews.AnyAsync(x=>x.ProjectId==request.ProjectId&&x.OwnerId==owner&&x.Name==name,ct))
                return Results.Conflict(new{error=$"You already have a saved view named '{name}'. Rename it, or update the existing one.",code="saved_view_duplicate_name"});
            var view=new SavedRequirementView(request.ProjectId,owner,name,contract.QueryJson,contract.ColumnsJson,request.IsShared,DateTimeOffset.UtcNow);db.SavedRequirementViews.Add(view);
            try{await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/views/{view.Id}",new{view.Id});}catch(DbUpdateException){return Results.Conflict(new{error="A saved view with that name already exists.",code="saved_view_duplicate_name"});}
        });

        // Owner-only, and answered as Not Found rather than Forbidden for somebody else's view: a shared view
        // is readable, and confirming that a particular id exists but is not yours is more than a reader of a
        // shared list needs to know.
        app.MapPut("/api/enterprise-requirements/views/{id:guid}",async(Guid id,UpdateSavedViewRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var owner=http.UserAccount().Id;
            var view=await db.SavedRequirementViews.SingleOrDefaultAsync(x=>x.Id==id&&x.OwnerId==owner,ct);
            if(view is null)return Results.NotFound();
            var now=DateTimeOffset.UtcNow;
            if(request.Name is not null)
            {
                var name=request.Name.Trim();
                if(name.Length==0)return Results.BadRequest(new{error="A saved view needs a name.",code="saved_view_name_required"});
                if(!string.Equals(name,view.Name,StringComparison.Ordinal)&&await db.SavedRequirementViews.AnyAsync(x=>x.ProjectId==view.ProjectId&&x.OwnerId==owner&&x.Name==name&&x.Id!=id,ct))
                    return Results.Conflict(new{error=$"You already have a saved view named '{name}'.",code="saved_view_duplicate_name"});
                view.Rename(name,now);
            }
            if(request.IsShared is not null)view.SetShared(request.IsShared.Value,now);
            if(request.QueryJson is not null||request.ColumnsJson is not null)
            {
                var contract=SavedViewContract.Normalize(request.QueryJson??view.QueryJson,request.ColumnsJson??view.ColumnsJson);
                if(!contract.Valid)return Results.BadRequest(new{error=contract.Error,code="saved_view_contract_invalid"});
                view.Replace(contract.QueryJson,contract.ColumnsJson,now);
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new{view.Id,view.Name,view.IsShared,view.QueryJson,view.ColumnsJson});
        });

        app.MapDelete("/api/enterprise-requirements/views/{id:guid}",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>{var view=await db.SavedRequirementViews.SingleOrDefaultAsync(x=>x.Id==id&&x.OwnerId==http.UserAccount().Id,ct);if(view is null)return Results.NotFound();db.Remove(view);await db.SaveChangesAsync(ct);return Results.NoContent();});

        app.MapPost("/api/enterprise-requirements/bulk/preview",async(BulkRequirementRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,request.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();
            if(request.SpecificationId is not null&&!await db.RequirementSpecifications.AnyAsync(x=>x.Id==request.SpecificationId&&x.ProjectId==request.ProjectId,ct))return Results.BadRequest(new{error="The target specification is not part of this Project."});
            if(request.SectionId is not null&&!await db.SpecificationNodes.AnyAsync(x=>x.Id==request.SectionId&&x.SpecificationId==request.SpecificationId&&x.Type==SpecificationNodeType.Section,ct))return Results.BadRequest(new{error="The target section is not part of this specification."});
            var valid=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==request.ProjectId&&request.ArtifactIds.Contains(x.Id)).Select(x=>x.Id).ToListAsync(ct);var payload=JsonSerializer.Serialize(new BulkJobPayload(valid,request.Tag,request.SpecificationId,request.SectionId));var job=new EnterpriseOperationJob(request.ProjectId,"RequirementBulkClassify",payload,valid.Count,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.EnterpriseOperationJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,requested=request.ArtifactIds.Count,valid=valid.Count,rejected=request.ArtifactIds.Count-valid.Count,operation=$"Add tag '{request.Tag}'"+(request.SpecificationId is null?"":" and place in specification")});
        });

        app.MapPost("/api/enterprise-requirements/bulk/{id:guid}/commit",async(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            var job=await db.EnterpriseOperationJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer,ProgramRole.ConfigurationManager))return Results.Forbid();if(job.State!=EnterpriseJobState.Preview)return Results.BadRequest(new{error="This bulk job is no longer awaiting commit."});var payload=JsonSerializer.Deserialize<BulkJobPayload>(job.RequestJson)!;var revisions=await db.RequirementRevisions.Where(x=>payload.ArtifactIds.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);var current=revisions.GroupBy(x=>x.ArtifactId).Select(x=>x.First()).ToList();var revisionIds=current.Select(x=>x.Id).ToList();var profiles=await db.RequirementRevisionProfiles.Where(x=>revisionIds.Contains(x.RevisionId)).ToListAsync(ct);foreach(var profile in profiles)profile.AddTag(payload.Tag,http.UserAccount().UserName,DateTimeOffset.UtcNow);
            if(payload.SpecificationId is not null){var parent=payload.SectionId;var existing=(await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.RequirementArtifactId!=null).Select(x=>x.RequirementArtifactId!.Value).ToListAsync(ct)).ToHashSet();var position=await db.SpecificationNodes.Where(x=>x.SpecificationId==payload.SpecificationId&&x.ParentId==parent).Select(x=>(int?)x.Position).MaxAsync(ct)??0;foreach(var artifactId in payload.ArtifactIds.Where(x=>!existing.Contains(x)))db.SpecificationNodes.Add(new(payload.SpecificationId.Value,parent,++position,SpecificationNodeType.Requirement,"",artifactId,http.UserAccount().UserName,DateTimeOffset.UtcNow));}
            job.Complete(profiles.Count,0,JsonSerializer.Serialize(new{tagged=profiles.Count,placed=payload.SpecificationId is not null}),DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,state=job.State.ToString(),job.SucceededCount,job.ResultJson});
        });

        app.MapGet("/api/enterprise-requirements/interchange",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var jobs=await db.RequirementInterchangeJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);var mappings=await db.RequirementImportMappings.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.Name).ToListAsync(ct);return Results.Ok(new{mappings=mappings.Select(x=>new{x.Id,x.Name,x.MappingJson,x.Version,x.UpdatedAt}),jobs=jobs.OrderByDescending(x=>x.CreatedAt).Take(50).Select(x=>new{x.Id,x.FileName,x.Sha256,x.ValidRows,x.InvalidRows,state=x.State.ToString(),x.CreatedBy,x.CreatedAt,x.CreatedChangeRequestId,x.CompletedAt})});
        });

        app.MapPost("/api/enterprise-requirements/import-mappings",async(CreateImportMappingRequest request,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {if(!await http.HasProjectAccessAsync(db,request.ProjectId,ct))return Results.Forbid();try{using var _=JsonDocument.Parse(request.MappingJson);var mapping=new RequirementImportMapping(request.ProjectId,request.Name,request.MappingJson,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementImportMappings.Add(mapping);await db.SaveChangesAsync(ct);return Results.Created($"/api/enterprise-requirements/import-mappings/{mapping.Id}",new{mapping.Id});}catch(Exception ex)when(ex is DomainException or DbUpdateException or JsonException){return Results.BadRequest(new{error=ex.Message});}});

        app.MapGet("/api/enterprise-requirements/import/{id:guid}/errors.csv",async(Guid id,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            var job=await db.RequirementInterchangeJobs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(!await http.HasProjectAccessAsync(db,job.ProjectId,ct))return Results.Forbid();var rows=JsonSerializer.Deserialize<List<InterchangeRequirementRow>>(job.RowsJson)??[];static string Csv(string value){if(value.Length>0&&"=+-@".Contains(value[0]))value="'"+value;return "\""+value.Replace("\"","\"\"")+"\"";}var text="Row,Identifier,Level,Statement,Errors\r\n"+string.Join("\r\n",rows.Where(x=>!x.Valid).Select(x=>$"{x.RowNumber},{Csv(x.Identifier)},{Csv(x.Level)},{Csv(x.Statement)},{Csv(string.Join("; ",x.Errors))}"));return Results.Text(text,"text/csv",Encoding.UTF8,200);
        });

        app.MapGet("/api/enterprise-requirements/performance",async(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)=>
        {
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var total=await db.Requirements.AsNoTracking().CountAsync(x=>x.ProjectId==projectId,ct);var samples=new List<PerformanceSample>();async Task Measure(string name,long target,Func<Task> action){await action();var timings=new List<long>();for(var i=0;i<3;i++){var sw=Stopwatch.StartNew();await action();sw.Stop();timings.Add(sw.ElapsedMilliseconds);}var p95=timings.Max();samples.Add(new(name,target,p95,p95<=target,timings));}await Measure("page_100",500,async()=>{_=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.BaseNumber).Take(100).Select(x=>new{x.Id,x.BaseNumber}).ToListAsync(ct);});await Measure("exact_identifier",300,async()=>{_=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId&&x.BaseNumber=="SYSR-000001").Select(x=>x.Id).FirstOrDefaultAsync(ct);});await Measure("open_collaboration",500,async()=>{_=await db.ArtifactComments.AsNoTracking().CountAsync(x=>x.ProjectId==projectId&&x.State==CollaborationState.Open,ct);});return Results.Ok(new{totalRequirements=total,scaleTarget=50_000,measuredAt=DateTimeOffset.UtcNow,allPassed=samples.All(x=>x.Passed),samples});
        });

        app.MapPost("/api/enterprise-requirements/import/preview",async(Guid projectId,Guid? mappingId,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            if(!await http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.Engineer))return Results.Forbid();if(!http.Request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data with a CSV or XLSX file."});var form=await http.Request.ReadFormAsync(ct);var file=form.Files.GetFile("file");if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty CSV or XLSX file."});if(file.Length>25*1024*1024)return Results.BadRequest(new{error="Import files are limited to 25 MB."});if(!file.FileName.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)&&!file.FileName.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase))return Results.BadRequest(new{error="Only CSV and XLSX files are supported."});
            await using var stream=file.OpenReadStream();using var memory=new MemoryStream();await stream.CopyToAsync(memory,ct);var bytes=memory.ToArray();memory.Position=0;IReadOnlyList<InterchangeRequirementRow> parsed;try{parsed=EnterpriseRequirementsService.ParseImport(memory,file.FileName);}catch(Exception ex){return Results.BadRequest(new{error=$"The workbook could not be read: {ex.Message}"});}
            var existing=(await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).Select(x=>x.BaseNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);var duplicates=parsed.GroupBy(x=>x.Identifier,StringComparer.OrdinalIgnoreCase).Where(x=>x.Count()>1).Select(x=>x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);var rows=parsed.Select(x=>{var errors=x.Errors.ToList();if(existing.Contains(x.Identifier))errors.Add("Identifier already exists in this Project; use a change-request modification workflow.");if(duplicates.Contains(x.Identifier))errors.Add("Identifier is duplicated in this import.");return x with{Valid=errors.Count==0,Errors=errors};}).ToList();var mapping=mappingId is null?"{\"mode\":\"standard-columns\"}":await db.RequirementImportMappings.Where(x=>x.Id==mappingId&&x.ProjectId==projectId).Select(x=>x.MappingJson).SingleOrDefaultAsync(ct)??"{\"mode\":\"standard-columns\"}";var job=new RequirementInterchangeJob(projectId,file.FileName,EnterpriseRequirementsService.Hash(bytes),mapping,JsonSerializer.Serialize(rows),rows.Count(x=>x.Valid),rows.Count(x=>!x.Valid),http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RequirementInterchangeJobs.Add(job);await db.SaveChangesAsync(ct);return Results.Ok(new{job.Id,job.FileName,job.Sha256,total=rows.Count,job.ValidRows,job.InvalidRows,rows=rows.Take(200)});
        }).DisableAntiforgery();

        app.MapPost("/api/enterprise-requirements/import/{id:guid}/commit",async(Guid id,CommitImportRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>
        {
            var job=await db.RequirementInterchangeJobs.SingleOrDefaultAsync(x=>x.Id==id,ct);if(job is null)return Results.NotFound();if(job.InvalidRows>0)return Results.BadRequest(new{error="Resolve every invalid row before committing this import."});if(!await http.HasProjectRoleAsync(db,identity,job.ProjectId,ct,ProgramRole.Engineer))return Results.Forbid();var rows=JsonSerializer.Deserialize<List<InterchangeRequirementRow>>(job.RowsJson)??[];try{var now=DateTimeOffset.UtcNow;var baseNumber=await IdentifierAllocator.NextChangeRequestAsync(db,request.Type,request.SoftwareLevel,ct);var scr=new SystemChangeRequest(baseNumber,0,job.ProjectId,request.TargetReleaseId,request.Title,request.Problem,request.Analysis,request.Solution,http.UserAccount().UserName,now,request.Type,softwareLevel:request.SoftwareLevel);foreach(var row in rows){EnterpriseRequirementsService.TryLevel(row.Level,out var reqLevel);scr.AddRequirementChange(http.UserAccount().UserName,row.Identifier,0,reqLevel,RequirementChangeKind.Introduce,row.Statement,row.Rationale,row.VerificationMethod,now,impactDispositionJson:RequirementAuthoringJson.PendingImpactDispositions);}db.SystemChangeRequests.Add(scr);job.Commit(scr.Id,now);await db.SaveChangesAsync(ct);return Results.Created($"/api/change-requests/{scr.Id}",new{scr.Id,scr.DisplayNumber,imported=rows.Count});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}
        });

        // Enterprise hardening: controlled content, durable operations, merge protection,
        // integrity qualification, and operator-facing health evidence.

        // Inline images are their own surface rather than a use of the attachment vault.
        //
        // An image inside a requirement statement is not a document somebody attached; it is part of what the
        // statement says, and it has to be storable before the record that references it exists, because an author
        // writes the figure into the paragraph as they are drafting it. Uploading here stores and hashes the file
        // against the project, and the authored content then references it by identifier. The file is never
        // duplicated into the record, so one diagram used in five requirements is stored once and stays one thing.
        app.MapPost("/api/content/images",async(HttpRequest request,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            if(!request.HasFormContentType)return Results.BadRequest(new{error="Use multipart form data."});
            var form=await request.ReadFormAsync(ct);var file=form.Files.GetFile("file");
            if(file is null||file.Length==0)return Results.BadRequest(new{error="Select a non-empty image."});
            if(!Guid.TryParse(form["projectId"],out var projectId))return Results.BadRequest(new{error="A project identifier is required."});
            if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();
            // Only formats every renderer here can produce. An image the workspace shows but the generated Word
            // document cannot would make a controlled document disagree with the record it came from.
            var contentType=(file.ContentType??"").ToLowerInvariant();
            if(contentType is not("image/png" or "image/jpeg"))return Results.BadRequest(new{error="Inline images must be PNG or JPEG so every generated document can render them."});
            if(file.Length>12*1024*1024)return Results.BadRequest(new{error="Inline images are limited to 12 MB. Attach larger files as controlled attachments instead."});
            // The declared content type is a claim by whoever uploaded the file. This image is streamed back inline
            // from this deployment's own origin, so the claim has to be checked against the bytes: a file that says
            // PNG and contains markup would otherwise be stored, referenced from a requirement, and served to an
            // approver by us.
            var signature=new byte[8];
            await using(var probe=file.OpenReadStream())
            {
                var read=await probe.ReadAtLeastAsync(signature,signature.Length,throwOnEndOfStream:false,ct);
                if(read<signature.Length||!PngImage.IsDeclaredImage(signature,contentType))
                    return Results.BadRequest(new{error="That file is not the image type it claims to be."});
            }
            var stored=await store.StoreAsync(file.OpenReadStream(),file.FileName,contentType,ct);
            try
            {
                var attachment=new ControlledAttachment(projectId,"InlineImage",projectId,null,Guid.NewGuid(),1,
                    string.IsNullOrWhiteSpace(form["alt"])?stored.OriginalFileName:form["alt"].ToString(),"",
                    stored.OriginalFileName,stored.ContentType,stored.Size,stored.Sha256,stored.StorageKey,null,
                    http.UserAccount().UserName,DateTimeOffset.UtcNow);
                db.ControlledAttachments.Add(attachment);await db.SaveChangesAsync(ct);
                return Results.Created($"/api/content/images/{attachment.Id}",new{attachment.Id,attachment.OriginalFileName,attachment.Size,attachment.Sha256});
            }
            catch{store.Delete(stored.StorageKey);throw;}
        }).DisableAntiforgery();

        app.MapGet("/api/content/images/{id:guid}",async(Guid id,HttpContext http,AeroLinkDbContext db,EvidenceFileStore store,CancellationToken ct)=>
        {
            var item=await db.ControlledAttachments.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.ArtifactType=="InlineImage",ct);
            if(item is null)return Results.NotFound();
            if(!await http.HasProjectAccessAsync(db,item.ProjectId,ct))return Results.Forbid();
            if(!store.Exists(item.StorageKey))return Results.NotFound();
            return Results.File(store.OpenRead(item.StorageKey),item.ContentType,enableRangeProcessing:true);
        });
    }

}
