using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The exact procedure revisions a build carries.
///
/// Coverage answers which requirements a revision verifies; it cannot answer whether a build carries that
/// revision. BaselineTestProcedureSelection is the configuration-controlled source for that second question.
/// A deterministic compatibility projection remains only for baselines created before procedure manifests
/// existed, where no exact selection was ever recorded.
/// </summary>
public sealed record TestProcedureEffectivityResult(
    Guid BaselineId,
    bool IsExactManifest,
    IReadOnlyDictionary<Guid, Guid> RevisionByProcedure)
{
    public IReadOnlyList<Guid> ProcedureIds => RevisionByProcedure.Keys.ToList();
    public IReadOnlyList<Guid> RevisionIds => RevisionByProcedure.Values.ToList();
}

public static class TestProcedureEffectivity
{
    public static async Task<TestProcedureEffectivityResult?> ForBaselineAsync(
        AeroLinkDbContext db, Guid baselineId, CancellationToken ct)
    {
        var baseline = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.Id == baselineId && x.RequirementsMaterializedAt != null)
            .Select(x => new
            {
                x.Id, x.TestProceduresMaterializedAt, x.RequirementsMaterializedAt, x.FrozenAt, x.CreatedAt
            })
            .SingleOrDefaultAsync(ct);
        if (baseline is null) return null;

        if (baseline.TestProceduresMaterializedAt is not null)
        {
            var selections = await db.BaselineTestProcedures.AsNoTracking()
                .Where(x => x.BaselineId == baseline.Id)
                .Select(x => new { x.ProcedureId, x.RevisionId })
                .ToListAsync(ct);
            return new(baseline.Id, true, selections.ToDictionary(x => x.ProcedureId, x => x.RevisionId));
        }

        // Compatibility for a genuinely pre-manifest baseline. It is intentionally not presented as exact:
        // a zero-coverage carried procedure cannot be recovered from records that never captured membership.
        // Only approved revisions are eligible, so a later draft cannot rewrite released historical content.
        var requirementRevisionIds = await db.BaselineRequirements.AsNoTracking()
            .Where(x => x.BaselineId == baseline.Id).Select(x => x.RevisionId).ToListAsync(ct);
        var rows = await (from coverage in db.TestCoverage.AsNoTracking()
                          where requirementRevisionIds.Contains(coverage.RequirementRevisionId)
                          join revision in db.TestProcedureRevisions.AsNoTracking()
                              .Where(x => x.State == TestProcedureState.Approved)
                              on coverage.ProcedureRevisionId equals revision.Id
                          select new
                          {
                              revision.ProcedureId, RevisionId = revision.Id, revision.Revision, revision.CreatedAt
                          })
            .Distinct().ToListAsync(ct);
        // Materialize before comparing DateTimeOffset values because SQLite cannot reliably translate their
        // ordering/comparison. A revision created after the baseline closed is successor evidence, not a
        // defensible compatibility candidate for that historical build.
        var effectiveAt = baseline.FrozenAt ?? baseline.RequirementsMaterializedAt ?? baseline.CreatedAt;
        var legacy = rows.Where(x => x.CreatedAt <= effectiveAt).GroupBy(x => x.ProcedureId).ToDictionary(
            group => group.Key,
            group => group.OrderByDescending(x => x.Revision).First().RevisionId);
        return new(baseline.Id, false, legacy);
    }

    /// <summary>
    /// The newest exact procedure manifest in the selected release or its predecessor chain.
    ///
    /// Requirement materialization and procedure materialization close at different times. An in-work release
    /// may already have a requirement baseline while its procedures still inherit the predecessor's fixed
    /// manifest, so this traversal cannot reuse the requirement-only effective-baseline rule.
    /// </summary>
    public static async Task<TestProcedureEffectivityResult?> ForReleaseAsync(
        AeroLinkDbContext db, Guid projectId, Guid releaseId, CancellationToken ct)
    {
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId)
            .Select(x => new { x.Id, x.PredecessorReleaseId }).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.RequirementsMaterializedAt != null)
            .Select(x => new
            {
                x.Id, x.ReleaseId, x.FrozenAt, x.CreatedAt, x.TestProceduresMaterializedAt
            }).ToListAsync(ct);

        Guid? legacyBaselineId = null;
        var current = releases.SingleOrDefault(x => x.Id == releaseId);
        var visited = new HashSet<Guid>();
        while (current is not null && visited.Add(current.Id))
        {
            var releaseBaselines = baselines.Where(x => x.ReleaseId == current.Id)
                .OrderByDescending(x => x.FrozenAt ?? x.CreatedAt).ToList();
            legacyBaselineId ??= releaseBaselines.FirstOrDefault()?.Id;
            var exact = releaseBaselines.FirstOrDefault(x => x.TestProceduresMaterializedAt is not null);
            if (exact is not null) return await ForBaselineAsync(db, exact.Id, ct);
            current = current.PredecessorReleaseId is null
                ? null
                : releases.SingleOrDefault(x => x.Id == current.PredecessorReleaseId.Value);
        }

        return legacyBaselineId is null ? null : await ForBaselineAsync(db, legacyBaselineId.Value, ct);
    }
}
