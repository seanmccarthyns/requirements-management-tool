using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// One exact requirement-revision to procedure-revision applicability link.
///
/// Procedure lifecycle and applicability are deliberately separate. An approved procedure can still be
/// suspect for changed wording; calling that link "approved coverage" would hide the work while changing the
/// procedure's own controlled state would misstate its history.
/// </summary>
public sealed record VerificationCoverageLinkProjection(
    Guid RequirementRevisionId,
    Guid ProcedureId,
    Guid ProcedureRevisionId,
    string DisplayNumber,
    string Title,
    string Level,
    string ProcedureState,
    bool IsSuspect,
    string CoverageState);

/// <summary>
/// The three coverage states a requirement revision can be in. They are mutually exclusive and exhaustive,
/// so one revision always has exactly one of them and a worklist built from them cannot double-count.
/// </summary>
public static class RequirementCoverageState
{
    public const string Covered = "Covered";
    public const string Suspect = "Suspect";
    public const string Uncovered = "Uncovered";

    public static bool TryParse(string? value, out string state)
    {
        state = value?.Trim().ToLowerInvariant() switch
        {
            "covered" => Covered,
            "suspect" => Suspect,
            "uncovered" => Uncovered,
            _ => ""
        };
        return state.Length > 0;
    }
}

public static class VerificationCoverageProjection
{
    /// <summary>
    /// The one definition of settled coverage. The release readiness gate and the requirements workspace
    /// filter both read it from here, because a product that answers "is this requirement covered?" in two
    /// places must not answer it two ways.
    ///
    /// Settled takes three things: the link is not suspect, the exact procedure revision it names is
    /// Approved, and that procedure has no other revision still in draft or review. A procedure being
    /// rewritten cannot settle anything, and a link carried across a requirement change that nobody
    /// reconfirmed would let a requirement pass on wording it was never written against.
    ///
    /// Returned as a composable query rather than a list so callers filter in the database. The workspace
    /// pages fifty thousand requirements and must never materialize coverage to answer this.
    /// </summary>
    public static IQueryable<Guid> SettledCoveredRequirementRevisionIds(AeroLinkDbContext db,
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null, bool buildScoped = false)
    {
        var source = db.TestCoverage.AsNoTracking();
        if (effectiveProcedureRevisionIds is not null)
        {
            var procedureRevisionIds = effectiveProcedureRevisionIds.Distinct().ToList();
            source = source.Where(coverage => procedureRevisionIds.Contains(coverage.ProcedureRevisionId));
        }
        return source
            .Where(coverage => !coverage.IsSuspect
                && db.TestProcedureRevisions.Any(revision => revision.Id == coverage.ProcedureRevisionId
                    && revision.State == TestProcedureState.Approved
                    && (buildScoped || !db.TestProcedureRevisions.Any(sibling => sibling.ProcedureId == revision.ProcedureId
                        && sibling.State != TestProcedureState.Approved))))
            .Select(coverage => coverage.RequirementRevisionId);
    }

    /// <summary>
    /// Every requirement revision some coverage link points at, settled or not. The difference between this
    /// and <see cref="SettledCoveredRequirementRevisionIds"/> is exactly the Suspect state: a revision that
    /// has been linked to a procedure by somebody, where that link does not currently count.
    /// </summary>
    public static IQueryable<Guid> LinkedRequirementRevisionIds(AeroLinkDbContext db,
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null)
    {
        var source = db.TestCoverage.AsNoTracking();
        if (effectiveProcedureRevisionIds is not null)
        {
            var procedureRevisionIds = effectiveProcedureRevisionIds.Distinct().ToList();
            source = source.Where(coverage => procedureRevisionIds.Contains(coverage.ProcedureRevisionId));
        }
        return source.Select(coverage => coverage.RequirementRevisionId);
    }

    /// <summary>
    /// The settled-covered subset of the supplied revisions, for callers that already hold a bounded
    /// population — a baseline's effective members, or one page of workspace rows.
    /// </summary>
    public static async Task<HashSet<Guid>> SettledCoveredAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> requirementRevisionIds,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null,
        bool buildScoped = false)
    {
        if (requirementRevisionIds.Count == 0) return [];
        var ids = requirementRevisionIds.Distinct().ToList();
        return (await SettledCoveredRequirementRevisionIds(db, effectiveProcedureRevisionIds, buildScoped)
            .Where(id => ids.Contains(id)).Distinct().ToListAsync(ct)).ToHashSet();
    }

    /// <summary>
    /// The exact state of each supplied revision, so a caller can label rows without a second definition.
    /// </summary>
    public static async Task<Dictionary<Guid, string>> StatesAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> requirementRevisionIds,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null,
        bool buildScoped = false)
    {
        if (requirementRevisionIds.Count == 0) return [];
        var ids = requirementRevisionIds.Distinct().ToList();
        var settled = await SettledCoveredAsync(db, ids, ct, effectiveProcedureRevisionIds, buildScoped);
        var linked = (await LinkedRequirementRevisionIds(db, effectiveProcedureRevisionIds)
            .Where(id => ids.Contains(id)).Distinct().ToListAsync(ct)).ToHashSet();
        return ids.ToDictionary(id => id, id => settled.Contains(id)
            ? RequirementCoverageState.Covered
            : linked.Contains(id) ? RequirementCoverageState.Suspect : RequirementCoverageState.Uncovered);
    }

    public static async Task<List<VerificationCoverageLinkProjection>> ForRequirementRevisionsAsync(
        AeroLinkDbContext db,
        IReadOnlyCollection<Guid> requirementRevisionIds,
        CancellationToken ct,
        bool buildScoped = false,
        IReadOnlyCollection<Guid>? effectiveProcedureRevisionIds = null)
    {
        if (requirementRevisionIds.Count == 0) return [];
        var ids = requirementRevisionIds.Distinct().ToList();
        var coverageSource = db.TestCoverage.AsNoTracking().Where(x => ids.Contains(x.RequirementRevisionId));
        if (effectiveProcedureRevisionIds is not null)
        {
            var procedureRevisionIds = effectiveProcedureRevisionIds.Distinct().ToList();
            coverageSource = coverageSource.Where(x => procedureRevisionIds.Contains(x.ProcedureRevisionId));
        }
        return await (from coverage in coverageSource
                      join revision in db.TestProcedureRevisions.AsNoTracking()
                          on coverage.ProcedureRevisionId equals revision.Id
                      join procedure in db.TestProcedures.AsNoTracking()
                          on revision.ProcedureId equals procedure.Id
                      orderby procedure.BaseNumber, revision.Revision
                      select new VerificationCoverageLinkProjection(
                          coverage.RequirementRevisionId,
                          procedure.Id,
                          revision.Id,
                          procedure.BaseNumber + "." + revision.Revision.ToString("D2"),
                          procedure.Title,
                          procedure.Level.ToString(),
                          revision.State.ToString(),
                          coverage.IsSuspect,
                          // IsSuspect is the stored flag; CoverageState is the judgement, and it uses the
                          // same settled rule as the release gate and the workspace filter. A link to a
                          // procedure that is being rewritten reported Confirmed here while the gate refused
                          // to count it — so a requirement could show "1 confirmed test" beside a workspace
                          // row labelled Suspect, and both were describing the same link.
                          !coverage.IsSuspect
                          && revision.State == TestProcedureState.Approved
                          && (buildScoped || !db.TestProcedureRevisions.Any(sibling => sibling.ProcedureId == procedure.Id
                              && sibling.State != TestProcedureState.Approved))
                              ? "Confirmed" : "Suspect"))
            .ToListAsync(ct);
    }
}
