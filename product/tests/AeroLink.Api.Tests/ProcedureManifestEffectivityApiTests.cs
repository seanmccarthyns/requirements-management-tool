using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// One controlled procedure identity carried at different exact revisions by two builds.
///
/// Coverage is deliberately ambiguous: every revision covers the same retained requirement. Any reader that
/// infers build membership from coverage will therefore select the project-latest revision and will lose the
/// carried procedure with zero coverage. Only the baseline procedure manifest can answer correctly.
/// </summary>
public sealed class ProcedureManifestEffectivityApiTests
{
    private sealed record Fixture(
        Guid ProjectId,
        Guid Release15Id,
        Guid Release16Id,
        Guid Baseline15Id,
        Guid Baseline16Id,
        Guid RequirementArtifactId,
        Guid ProcedureId,
        Guid Revision00Id,
        Guid Revision01Id,
        Guid ZeroCoverageProcedureId,
        Guid ProcedureDocumentId,
        Guid TcrId,
        string FutureOnlyBaseNumber);

    private static async Task<Fixture> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        var program = new ProgramRecord("Procedure Manifest Program", "PMP");
        var project = new ProjectRecord(program.Id, "FMS", "Manifest FMS");
        var release15 = new SoftwareRelease(project.Id, "1.5", true);
        var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        var release17 = new SoftwareRelease(project.Id, "1.7", false, release16.Id);
        db.AddRange(program, project, release15, release16, release17);

        SystemChangeRequest Approved(string number, string requirementNumber, Guid releaseId)
        {
            var request = new SystemChangeRequest(number, 0, project.Id, releaseId,
                "Manifest fixture", "Problem", "Analysis", "Solution", "author", now);
            request.AddRequirementChange("author", requirementNumber, 0, RequirementLevel.System,
                RequirementChangeKind.Introduce, "The FMS shall retain exact build effectivity.",
                "Configuration identity must be deterministic.", "Test", now);
            request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], now);
            request.ApproveActiveStage("reviewer", now);
            return request;
        }

        CandidateBaseline Baseline(string number, SoftwareRelease release, SystemChangeRequest request,
            Guid? predecessor)
        {
            var baseline = new CandidateBaseline(number, 0, project.Id, release.Id, predecessor,
                $"Build {release.Version}", "cm", now);
            baseline.Select(request, "cm", now);
            baseline.Freeze("cm", now);
            baseline.MarkRequirementsMaterialized("cm", new string('a', 64), 1, now);
            return baseline;
        }

        var scr15 = Approved("SRCR-02140", "SYSR-002140", release15.Id);
        var scr16 = Approved("SRCR-02141", "SYSR-002141", release16.Id);
        var scr17 = Approved("SRCR-02142", "SYSR-002142", release17.Id);
        var baseline15 = Baseline("SW-01.50", release15, scr15, null);
        var baseline16 = Baseline("SW-01.60", release16, scr16, baseline15.Id);
        var baseline17 = Baseline("SW-01.70", release17, scr17, baseline16.Id);

        var requirement = new RequirementArtifact(project.Id, "SYSR-002140", RequirementLevel.System, now);
        var requirementRevision = new RequirementRevision(requirement.Id, 0,
            "The FMS shall retain exact build effectivity.", "Configuration identity must be deterministic.",
            "Test", RequirementRevisionState.Active, scr15.Id, baseline15.Id, now);
        db.AddRange(scr15, scr16, scr17, baseline15, baseline16, baseline17, requirement, requirementRevision);
        db.BaselineRequirements.AddRange(
            new BaselineRequirementSelection(baseline15.Id, requirement.Id, requirementRevision.Id),
            new BaselineRequirementSelection(baseline16.Id, requirement.Id, requirementRevision.Id),
            new BaselineRequirementSelection(baseline17.Id, requirement.Id, requirementRevision.Id));

        var procedure = new TestProcedure(project.Id, "SYSTP-002140", "Exact manifest procedure",
            "test.author", now, TestProcedureLevel.System);
        var revision00 = Revision(procedure.Id, 0, "Released 1.5 procedure", baseline15.Id);
        var revision01 = Revision(procedure.Id, 1, "Build 1.6 procedure", baseline16.Id);
        var revision02 = Revision(procedure.Id, 2, "Future 1.7 procedure", baseline17.Id);
        var zeroCoverage = new TestProcedure(project.Id, "SYSTP-002141", "Retained zero-coverage procedure",
            "test.author", now, TestProcedureLevel.System);
        var zeroRevision = Revision(zeroCoverage.Id, 0, "Carried without current coverage", baseline15.Id);
        var futureOnly = new TestProcedure(project.Id, "SYSTP-002142", "Future-only procedure",
            "test.author", now, TestProcedureLevel.System);
        var futureOnlyRevision = Revision(futureOnly.Id, 0, "Only Build 1.7 carries this", baseline17.Id);
        db.AddRange(procedure, revision00, revision01, revision02, zeroCoverage, zeroRevision,
            futureOnly, futureOnlyRevision);

        // All three revisions cover the same retained requirement. Coverage-derived effectivity therefore
        // cannot distinguish which revision either build actually carries.
        db.TestCoverage.AddRange(
            new TestRequirementCoverage(revision00.Id, requirementRevision.Id),
            new TestRequirementCoverage(revision01.Id, requirementRevision.Id),
            new TestRequirementCoverage(revision02.Id, requirementRevision.Id));
        db.BaselineTestProcedures.AddRange(
            new BaselineTestProcedureSelection(baseline15.Id, procedure.Id, revision00.Id),
            new BaselineTestProcedureSelection(baseline15.Id, zeroCoverage.Id, zeroRevision.Id),
            new BaselineTestProcedureSelection(baseline16.Id, procedure.Id, revision01.Id),
            new BaselineTestProcedureSelection(baseline16.Id, zeroCoverage.Id, zeroRevision.Id),
            new BaselineTestProcedureSelection(baseline17.Id, procedure.Id, revision02.Id),
            new BaselineTestProcedureSelection(baseline17.Id, futureOnly.Id, futureOnlyRevision.Id));
        baseline15.MarkTestProceduresMaterialized("cm", new string('b', 64), 2, now);
        baseline16.MarkTestProceduresMaterialized("cm", new string('c', 64), 2, now);
        baseline17.MarkTestProceduresMaterialized("cm", new string('d', 64), 2, now);
        var procedureDocument = new ControlledDocument(project.Id, release15.Id, baseline15.Id,
            ControlledDocumentType.SystemTestProcedures, "SYSTD-002140", "Build 1.5 System Test Procedures",
            0, new string('e', 64), 2, now);
        db.Add(procedureDocument);

        var authoringRequest = Approved("SRCR-02143", "SYSR-002143", release16.Id);
        var review = new TestChangeReview(project.Id, release16.Id, authoringRequest.Id,
            TestChangeReviewDiscipline.System, authoringRequest.DisplayNumber, now);
        review.RecordTestChangeRequired("manifest.engineer", now);
        review.AssignControlledNumber("SYSTCR-002140", now);
        db.AddRange(authoringRequest, review);

        var account = new UserAccount("manifest.engineer", "Manifest Engineer", "manifest@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.AddRange(account, new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        return new(project.Id, release15.Id, release16.Id, baseline15.Id, baseline16.Id,
            requirement.Id, procedure.Id, revision00.Id, revision01.Id, zeroCoverage.Id, procedureDocument.Id,
            review.Id, futureOnly.BaseNumber);

        TestProcedureRevision Revision(Guid procedureId, int revision, string objective, Guid baselineId) =>
            new(procedureId, revision, objective, "Configured test environment", "Execute the controlled steps.",
                "The expected behavior is observed.", TestProcedureState.Approved, "test.author", now,
                effectiveBaselineId: baselineId);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "manifest.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    [Fact]
    public async Task Explorer_search_and_history_use_each_builds_exact_manifest_revision()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var build15 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}&scope=System&page=1&pageSize=25");
        var build15Items = build15.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, build15Items.Count);
        Assert.Contains(build15Items, x => x.GetProperty("displayNumber").GetString() == "SYSTP-002140.00");
        Assert.Contains(build15Items, x => x.GetProperty("id").GetGuid() == fixture.ZeroCoverageProcedureId
            && x.GetProperty("requirementCount").GetInt32() == 0);

        var build16 = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release16Id}&scope=System&page=1&pageSize=25");
        Assert.Contains(build16.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("displayNumber").GetString() == "SYSTP-002140.01");

        var wrongRevisionSearch = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-procedures?projectId={fixture.ProjectId}&releaseId={fixture.Release15Id}&search=SYSTP-002140.01");
        Assert.Equal(0, wrongRevisionSearch.GetProperty("totalCount").GetInt32());

        using var exact = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/history?releaseId={fixture.Release15Id}&revisionId={fixture.Revision00Id}");
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        using var crossBuild = await client.GetAsync(
            $"/api/test-procedures/{fixture.ProcedureId}/history?releaseId={fixture.Release15Id}&revisionId={fixture.Revision01Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossBuild.StatusCode);
    }

    [Fact]
    public async Task Coverage_and_traceability_only_name_manifest_procedure_revisions()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var coverage = await client.GetFromJsonAsync<JsonElement>(
            $"/api/verification-coverage?projectId={fixture.ProjectId}&baselineId={fixture.Baseline15Id}");
        var coveredBy = Assert.Single(coverage.GetProperty("items").EnumerateArray())
            .GetProperty("coveredBy").EnumerateArray().ToList();
        var exact = Assert.Single(coveredBy);
        Assert.Equal("SYSTP-002140.00", exact.GetProperty("displayNumber").GetString());

        var traceability = await client.GetFromJsonAsync<JsonElement>(
            $"/api/traceability?projectId={fixture.ProjectId}&baselineId={fixture.Baseline15Id}&page=1&pageSize=25");
        var tests = Assert.Single(traceability.GetProperty("items").EnumerateArray())
            .GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal("SYSTP-002140.00", Assert.Single(tests).GetProperty("displayNumber").GetString());

        var requirementImpact = await client.GetFromJsonAsync<JsonElement>(
            $"/api/enterprise-requirements/{fixture.RequirementArtifactId}/impact?releaseId={fixture.Release15Id}");
        var inspectorTests = requirementImpact.GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal("SYSTP-002140.00", Assert.Single(inspectorTests).GetProperty("displayNumber").GetString());
    }

    [Fact]
    public async Task Tcr_targets_and_mutations_are_bound_to_the_target_build_manifest()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        var workspace = await client.GetFromJsonAsync<JsonElement>(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes");
        var targets = workspace.GetProperty("procedureTargets").EnumerateArray().ToList();
        Assert.Contains(targets, x => x.GetProperty("baseNumber").GetString() == "SYSTP-002140"
            && x.GetProperty("currentRevision").GetInt32() == 1);
        Assert.DoesNotContain(targets, x => x.GetProperty("baseNumber").GetString() == fixture.FutureOnlyBaseNumber);

        using var futureOnly = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            Proposal(fixture.FutureOnlyBaseNumber, 1));
        Assert.Equal(HttpStatusCode.BadRequest, futureOnly.StatusCode);
        var futureBody = JsonSerializer.Deserialize<JsonElement>(await futureOnly.Content.ReadAsStringAsync());
        Assert.Equal("procedure_not_carried_by_build", futureBody.GetProperty("code").GetString());

        using var skippedRevision = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            Proposal("SYSTP-002140", 3));
        Assert.Equal(HttpStatusCode.BadRequest, skippedRevision.StatusCode);
        var skippedBody = JsonSerializer.Deserialize<JsonElement>(await skippedRevision.Content.ReadAsStringAsync());
        Assert.Equal("procedure_revision_not_next_for_build", skippedBody.GetProperty("code").GetString());

        using var accepted = await client.PostAsJsonAsync(
            $"/api/test-change-reviews/{fixture.TcrId}/procedure-changes",
            Proposal("SYSTP-002140", 2));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        static object Proposal(string baseNumber, int revision) => new
        {
            kind = "Modify", baseNumber, revision, title = "Manifest-bound revision",
            objective = "Verify exact effectivity.", steps = "Execute exact steps.",
            expectedResult = "The configured behavior is observed.", rationale = "Controlled build change."
        };
    }

    [Fact]
    public async Task Controlled_procedure_and_traceability_exports_use_the_same_exact_manifest()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var fixture = await SeedAsync(factory);
        await LoginAsync(client);

        using var procedures = await client.GetAsync(
            $"/api/documents/{fixture.ProcedureDocumentId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, procedures.StatusCode);
        var procedurePdf = System.Text.Encoding.Latin1.GetString(await procedures.Content.ReadAsByteArrayAsync());
        Assert.Contains("SYSTP-002140.00", procedurePdf);
        Assert.Contains("SYSTP-002141.00", procedurePdf);
        Assert.DoesNotContain("SYSTP-002140.01", procedurePdf);
        Assert.DoesNotContain("SYSTP-002140.02", procedurePdf);

        using var traceability = await client.GetAsync(
            $"/api/traceability/{fixture.Baseline15Id}/download?format=pdf");
        Assert.Equal(HttpStatusCode.OK, traceability.StatusCode);
        var tracePdf = System.Text.Encoding.Latin1.GetString(await traceability.Content.ReadAsByteArrayAsync());
        Assert.Contains("SYSTP-002140.00", tracePdf);
        Assert.DoesNotContain("SYSTP-002140.01", tracePdf);
        Assert.DoesNotContain("SYSTP-002140.02", tracePdf);
    }
}
