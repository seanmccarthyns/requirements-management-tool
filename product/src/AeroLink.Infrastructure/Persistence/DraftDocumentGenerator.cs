using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The document a release is heading towards, before it is frozen.
///
/// A controlled document is generated from a frozen baseline, which is correct and is also why one cannot
/// exist until the release is nearly over: the content is fixed at the moment of freezing. A team working
/// through a release wants to see the document taking shape — the released predecessor with the approved
/// changes already folded in — and has had no way to.
///
/// So this generates from *released baseline + approved change requests*, and nothing is persisted. There is
/// no document number allocated, no content hash retained, no record created. That is deliberate: the input
/// set is still moving, and a controlled record of something that changes hourly is a record of nothing. The
/// approved document still comes later, through the existing path, from a frozen baseline.
///
/// What makes it safe to hand round is the watermark, and the fact that the revision it carries is the one the
/// released document will carry. A reader comparing a draft to the eventual release sees the same number and
/// the difference is the stamp, rather than two documents whose relationship they have to work out.
/// </summary>
public sealed class DraftDocumentGenerator(AeroLinkDbContext db, RichContentPublisher richContent)
{
    public async Task<GeneratedOutput?> GenerateAsync(Guid releaseId, ControlledDocumentType type, string format,
        string preparedBy, CancellationToken ct)
    {
        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == releaseId, ct);
        if (release is null) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == release.ProjectId, ct);
        var program = await db.Programs.AsNoTracking().SingleAsync(x => x.Id == project.ProgramId, ct);

        if (type is ControlledDocumentType.SystemTestProcedures
            or ControlledDocumentType.HighLevelTestProcedures
            or ControlledDocumentType.LowLevelTestProcedures)
            return await GenerateProcedureDraftAsync(release, project, program, type, format, preparedBy, ct);

        var level = type switch
        {
            ControlledDocumentType.Sysrd => RequirementLevel.System,
            ControlledDocumentType.SwrdHighLevel => RequirementLevel.HighLevel,
            ControlledDocumentType.SwrdLowLevel => RequirementLevel.LowLevel,
            // Test procedure documents are not built from a baseline of requirements, so a draft of one would
            // be the approved generator with a stamp on it. Refused rather than half-answered.
            _ => (RequirementLevel?)null
        };
        if (level is null) return null;

        var predecessor = await ReleasedPredecessorBaselineAsync(release.ProjectId, release.PredecessorReleaseId, ct);
        var effective = await EffectiveRequirementsAsync(predecessor?.Id, release.ProjectId, releaseId, level.Value, ct);
        var generatedAt = DateTimeOffset.UtcNow;

        var revisionIds = effective.Where(x => x.RevisionId is not null).Select(x => x.RevisionId!.Value).ToList();
        var authored = await db.RequirementRevisionProfiles.AsNoTracking()
            .Where(x => revisionIds.Contains(x.RevisionId)).ToDictionaryAsync(x => x.RevisionId, x => x.RichText, ct);
        var proposed = effective.Where(x => x.RevisionId is null && !string.IsNullOrWhiteSpace(x.RichText))
            .Select(x => x.RichText).ToList();
        var images = await richContent.ResolveImagesAsync(authored.Values.Concat(proposed), ct);

        var records = effective.Select(x => new PublicationRecord(
            $"{x.BaseNumber}.{x.Revision:D2}", level.Value.ToString(), x.Origin, x.Statement,
            new[] { ("Rationale", x.Rationale), ("Verification method", x.VerificationMethod), ("Source change request", x.Source) },
            Supplementary(x))).ToList();

        var documentNumber = DocumentNumber(type, release.Version);
        var revision = await NextRevisionAsync(release.ProjectId, type, ct);
        var pending = effective.Count(x => x.Origin.Length > 0);

        var publication = new ProfessionalPublication(
            project.SoftwareProduct, $"{program.Name} ({program.Code})", project.Name, DocumentTypeName(type),
            $"{project.SoftwareProduct} {DocumentTypeName(type)}",
            $"Draft for release {release.Version}. Released content plus every approved change not yet baselined.",
            documentNumber, revision.ToString("D2"), "DRAFT - NOT APPROVED", release.Version,
            predecessor?.DisplayNumber ?? "No released predecessor", preparedBy, generatedAt,
            // No manifest hash: a hash asserts that this content is fixed and reproducible, and this content is
            // neither. Printing one would be the most misleading thing on the page.
            "not applicable to a draft",
            new[]
            {
                ("Requirements", effective.Count.ToString("N0")),
                ("Changed by approved change requests", pending.ToString("N0")),
                ("Released predecessor", predecessor?.DisplayNumber ?? "none"),
                ("Status", "Draft - content may still change"),
            },
            [],
            new[] { (revision.ToString("D2"), "Draft", generatedAt.UtcDateTime.ToString("yyyy-MM-dd"), preparedBy) },
            new[]
            {
                new PublicationSection("Effective Requirements",
                    $"The released baseline for this product with every approved change to release {release.Version} applied. Rows marked as changed are not yet part of any frozen baseline.",
                    records),
            })
        {
            Watermark = "DRAFT",
        };

        return ProfessionalPublicationRenderer.Render(publication, format, $"DRAFT_{documentNumber}.{revision:D2}_{release.Version}");

        string Supplementary(EffectiveRequirement item)
        {
            var content = item.RevisionId is not null && authored.TryGetValue(item.RevisionId.Value, out var stored)
                ? stored
                : item.RichText;
            if (string.IsNullOrWhiteSpace(content)) return "";
            var adds = AeroLink.Domain.Content.RichContent.HasStructure(content)
                || AeroLink.Domain.Content.RichContent.ToPlainText(content) != item.Statement;
            return adds ? RichContentPublisher.ForPublication(content, images) : "";
        }
    }

    private async Task<GeneratedOutput> GenerateProcedureDraftAsync(SoftwareRelease release, ProjectRecord project,
        ProgramRecord program, ControlledDocumentType type, string format, string preparedBy, CancellationToken ct)
    {
        var level = type switch
        {
            ControlledDocumentType.SystemTestProcedures => TestProcedureLevel.System,
            ControlledDocumentType.HighLevelTestProcedures => TestProcedureLevel.HighLevel,
            _ => TestProcedureLevel.LowLevel
        };
        var effectivity = await TestProcedureEffectivity.ForReleaseAsync(db, project.Id, release.Id, ct);
        var revisionIds = effectivity?.RevisionIds ?? [];
        var latest = await (from revision in db.TestProcedureRevisions.AsNoTracking()
                                .Where(x => revisionIds.Contains(x.Id))
                            join procedure in db.TestProcedures.AsNoTracking()
                                .Where(x => x.ProjectId == project.Id && x.Level == level)
                                on revision.ProcedureId equals procedure.Id
                            orderby procedure.BaseNumber
                            select new { Procedure = procedure, Revision = revision }).ToListAsync(ct);
        var records = latest.Select(x => new PublicationRecord(
            $"{x.Procedure.BaseNumber}.{x.Revision.Revision:D2}",
            $"{level} · {x.Revision.State}",
            x.Procedure.Title,
            x.Revision.Steps,
            new[]
            {
                ("Objective", x.Revision.Objective),
                ("Preconditions", x.Revision.Preconditions),
                ("Expected result", x.Revision.ExpectedResult),
                ("Owner", x.Procedure.OwnerId)
            })).ToList();
        var generatedAt = DateTimeOffset.UtcNow;
        var documentNumber = DocumentNumber(type, release.Version);
        var revisionNumber = await NextRevisionAsync(project.Id, type, ct);
        var publication = new ProfessionalPublication(
            project.SoftwareProduct, $"{program.Name} ({program.Code})", project.Name, DocumentTypeName(type),
            $"{project.SoftwareProduct} {DocumentTypeName(type)}",
            $"Living draft for software build {SoftwareBuildIdentifier.FromVersion(release.Version)}.",
            documentNumber, revisionNumber.ToString("D2"), "DRAFT - NOT APPROVED", release.Version,
            SoftwareBuildIdentifier.FromVersion(release.Version), preparedBy, generatedAt,
            "not applicable to a draft",
            new[]
            {
                ("Test procedures", records.Count.ToString("N0")),
                ("Approved revisions", latest.Count(x => x.Revision.State == TestProcedureState.Approved).ToString("N0")),
                ("In review or draft", latest.Count(x => x.Revision.State != TestProcedureState.Approved).ToString("N0")),
                ("Status", "Draft - content may still change")
            },
            [],
            new[] { (revisionNumber.ToString("D2"), "Draft", generatedAt.UtcDateTime.ToString("yyyy-MM-dd"), preparedBy) },
            new[]
            {
                new PublicationSection("Effective Test Procedures",
                    effectivity?.IsExactManifest == true
                        ? "Exact controlled procedure revisions carried by the effective build manifest."
                        : "Approved compatibility projection for a predecessor created before procedure manifests existed.",
                    records)
            })
        {
            Watermark = "DRAFT"
        };
        return ProfessionalPublicationRenderer.Render(publication, format,
            $"DRAFT_{documentNumber}.{revisionNumber:D2}_{release.Version}");
    }

    /// <summary>The materialized baseline of the released predecessor, or null for a first release.</summary>
    private async Task<CandidateBaseline?> ReleasedPredecessorBaselineAsync(Guid projectId, Guid? predecessorReleaseId, CancellationToken ct)
    {
        var candidates = db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null);
        // The named predecessor when there is one. Otherwise the newest materialized baseline belonging to a
        // release that has actually shipped — never one from the release being drafted, which is the whole
        // point: a draft is what the *next* document will say, not a copy of what this one already says.
        if (predecessorReleaseId is not null)
            candidates = candidates.Where(x => x.ReleaseId == predecessorReleaseId);
        else
            candidates = candidates.Where(x => db.Releases.Any(r => r.Id == x.ReleaseId && r.IsReleased));
        // Ordered after the rows arrive, not in the query: SQLite cannot ORDER BY a DateTimeOffset and throws
        // at execution. PostgreSQL can, so this worked when generated against a real deployment and failed on
        // every test — the opposite of the usual way round, and the reason the tests run on SQLite at all. The
        // set is the materialized baselines of one project, so ordering them here costs nothing.
        var materialized = await candidates.ToListAsync(ct);
        return materialized.OrderByDescending(x => x.RequirementsMaterializedAt).FirstOrDefault();
    }

    private sealed record EffectiveRequirement(string BaseNumber, int Revision, string Statement, string Rationale,
        string VerificationMethod, string Source, string Origin, Guid? RevisionId, string RichText);

    /// <summary>
    /// The released baseline with approved changes applied — introduce, modify, retire — keyed by identity.
    ///
    /// The proposed content is read off the change request rather than from a requirement revision, because
    /// the revisions a change describes do not exist until materialization creates them. That is the ordering
    /// this whole feature works around: the document is wanted before the thing it would normally be built
    /// from has been made.
    /// </summary>
    private async Task<List<EffectiveRequirement>> EffectiveRequirementsAsync(Guid? baselineId, Guid projectId,
        Guid releaseId, RequirementLevel level, CancellationToken ct)
    {
        var effective = new Dictionary<string, EffectiveRequirement>(StringComparer.OrdinalIgnoreCase);
        if (baselineId is not null)
        {
            var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                              join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == level) on member.ArtifactId equals artifact.Id
                              join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                              select new { artifact.BaseNumber, revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, RevisionId = revision.Id })
                .ToListAsync(ct);
            foreach (var row in rows)
                effective[row.BaseNumber] = new(row.BaseNumber, row.Revision, row.Statement, row.Rationale,
                    row.VerificationMethod, "Released baseline", "", row.RevisionId, "");
        }

        // Approved and allocated both count: the engineering is signed for in each, and whether it has been
        // picked into a candidate yet says nothing about whether it belongs in the document being drafted.
        // Joined through the RequirementChanges set rather than navigated through `scr.RequirementChanges`.
        // The domain exposes that collection as `AsReadOnly()`, which protects the invariant and cannot be
        // composed into a query — EF gives up and throws at execution rather than at compile time, so the
        // mistake surfaces as a 500 from a working-looking endpoint.
        var changes = await (from scr in db.SystemChangeRequests.AsNoTracking()
                             where scr.ProjectId == projectId && scr.TargetReleaseId == releaseId
                                && (scr.State == ChangeRequestState.Approved || scr.State == ChangeRequestState.SelectedForBaseline)
                             join change in db.RequirementChanges.AsNoTracking() on scr.Id equals change.ChangeRequestId
                             where change.Level == level
                             select new { scr.BaseNumber, scr.Revision, scr.UpdatedAt, change.Kind, ChangeBase = change.BaseNumber, ChangeRevision = change.Revision, change.Statement, change.Rationale, change.VerificationMethod, change.RichText })
            .ToListAsync(ct);
        // Ordered here rather than in the query, for the same reason as above — SQLite will not sort a
        // DateTimeOffset. Order matters: two approved changes touching one requirement must be applied oldest
        // first so the newest wins.
        changes = [.. changes.OrderBy(x => x.UpdatedAt)];

        foreach (var change in changes)
        {
            var source = $"{change.BaseNumber}.{change.Revision:D2}";
            if (change.Kind == RequirementChangeKind.Retire) { effective.Remove(change.ChangeBase); continue; }
            var origin = change.Kind == RequirementChangeKind.Introduce ? "New in this release" : "Changed in this release";
            effective[change.ChangeBase] = new(change.ChangeBase, change.ChangeRevision, change.Statement,
                change.Rationale, change.VerificationMethod, source, origin, null, change.RichText ?? "");
        }

        return effective.Values.OrderBy(x => x.BaseNumber, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The revision the released document will carry, so the draft and the release agree.
    ///
    /// One past the highest revision of this document type already generated in the project. A draft that
    /// invented its own numbering would make the eventual released document look like a different document.
    /// </summary>
    private async Task<int> NextRevisionAsync(Guid projectId, ControlledDocumentType type, CancellationToken ct)
    {
        var highest = await db.ControlledDocuments.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.Type == type)
            .Select(x => (int?)x.Revision).MaxAsync(ct);
        return (highest ?? 0) + 1;
    }

    private static string DocumentNumber(ControlledDocumentType type, string version)
    {
        var digits = string.Concat(version.Where(char.IsDigit));
        var suffix = int.TryParse(digits, out var number) ? number.ToString("D6") : digits;
        var prefix = type switch
        {
            ControlledDocumentType.Sysrd => "SYSRD",
            ControlledDocumentType.SwrdHighLevel => "HLRD",
            ControlledDocumentType.SwrdLowLevel => "LLRD",
            ControlledDocumentType.SystemTestProcedures => "SYSTD",
            ControlledDocumentType.HighLevelTestProcedures => "HLRTD",
            _ => "LLRTD",
        };
        return $"{prefix}-{suffix}";
    }

    private static string DocumentTypeName(ControlledDocumentType type) => type switch
    {
        ControlledDocumentType.Sysrd => "System Requirements Document (SYSRD)",
        ControlledDocumentType.SwrdHighLevel => "High-Level Software Requirements Document (HLRD)",
        ControlledDocumentType.SwrdLowLevel => "Low-Level Software Requirements Document (LLRD)",
        ControlledDocumentType.SystemTestProcedures => "System Test Procedure Document (SYSTD)",
        ControlledDocumentType.HighLevelTestProcedures => "HLR Test Procedure Document (HLRTD)",
        _ => "LLR Test Procedure Document (LLRTD)",
    };
}
