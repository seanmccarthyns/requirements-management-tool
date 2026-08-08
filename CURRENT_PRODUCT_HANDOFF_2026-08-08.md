# AeroLink current product handoff — 8 August 2026

This is the current restart point after the Aug. 7–8 delivery window and the independent review that followed it.
It supersedes the 6 August handoff as the document to read before starting new work. Older dated handoffs remain
historical records and must not be used as current backlog authority.

## Authoritative repository state

- Repository: `seanmccarthyns/requirements-management-tool`
- Authoritative branch: `main`
- Exact qualified main SHA: `d06fcee94473a9128a98e58b3699c1f6c0ad3af6`
- Squash merge: PR #388, **Stage 4: first-class manual test change request authoring**
- Post-merge Product Quality Gate: run `31269258110`, successful
- Former Stage 4 branch: deleted remotely after merge
- Persistent PostgreSQL remains the sole engineering-data store; no reset or replacement was part of the merge

Start every task by fetching `origin`, confirming `main` at the exact current GitHub head, and creating a focused
branch. GitHub is the source of truth over every local checkout.

## Standing safety and governance rules

- Never commit directly to `main`.
- Branch per task, pull request, quality gate, explicit owner authorization, squash merge.
- Never weaken branch protection, rulesets, required checks, strict/up-to-date requirements, enforce-admin
  settings, repository settings, merge policy, or GitHub Actions protection to make a merge possible.
- There is one persistent PostgreSQL database, normally on port `54329`. Automated tests use disposable
  databases only. Do not reset, restore, seed, migrate, or directly edit persistent engineering data without
  explicit owner authorization.
- Released Build 1.5 remains immutable and read-only. Build 1.6 is the in-work development workspace.
- No certification, compliance, or tool-qualification claim.
- No AI capability ships in the product under the current program boundary.

## What Aug. 7–8 delivered

### Production-shaped client hosting and qualification

AeroLink can now build the React client and serve it from the ASP.NET Core process on one origin. The production
launcher and production-build browser journeys exercise the artifact that would actually be deployed rather
than only the Vite development server. API paths retain API status/JSON behavior; client deep links fall back to
the built entry document. Content security policy and cache behavior are explicit.

### Verification Stage 3B surface

Verification is presented as three direct work surfaces for each System, HLR, and LLR discipline:

1. **Change Requests** — downstream test assessments, controlled Test Change Requests, verification-impact
   decisions, procedure-change authoring, review, and Problem Report links.
2. **Test Procedure Explorer** — procedure inventory, exact history, discussion, coverage attention, and
   procedure reading.
3. **Test Results** — build test set, recorded executions, evidence, retest history, and corrective-action work.

The old generic Testing Coverage presentation is no longer the current navigation contract.

### Stage 4 manual Test Change Requests

A Test Change Request may be raised deliberately over one or more approved source Change Requests instead of
existing only as one automatic assessment per source. A manually raised package carries:

- a controlled discipline-specific number and revision;
- Title, Problem, Analysis, and Solution, including canonical rich content;
- one originating source and optional folded-in source Change Requests;
- linked Problem Reports;
- verification-impact decisions;
- Introduce / Modify / Retire procedure-change proposals;
- assignment and supervisory authority;
- review cycles, approval steps, notifications, and electronic-signature evidence.

Multi-source consolidation moves the source assessments' verification-impact items into the surviving package
without recreating their identity or deleting decision history. Unfold restores a fresh actionable assessment,
including the zero-item case, and permits later refolding while retaining superseded history.

### Configurable TCR review workflows

A configured TCR workflow now executes as a real staged review:

- one selected authorized person per configured stage;
- sequential or parallel activation as defined by the recorded procedure;
- frozen workflow identity/version/name and frozen authority on each approval step;
- active-stage capability derived from the live review cycle rather than the legacy first approver field;
- final approval only after every required active stage approves;
- request changes closes the cycle and returns the package to Open;
- resubmission creates a new cycle and snapshot while retaining prior evidence.

Where no workflow is configured, the backwards-compatible single independent Approver fallback remains. For
ordinary System Change Requests, the closure patch restored the same no-workflow Approver rule while preserving
configured Test Lead, Configuration Manager, and other stage authorities.

### Canonical TCR review evidence

New TCR review cycles use the explicit `aerolink.tcr-review-snapshot` version 1 canonical JSON contract. The
snapshot deterministically covers the package identity/outcome, complete case, exact covered source identities,
procedure-change content, Problem Report identities, and verification-impact decisions. Ordering is stable;
rich content is canonical; malformed controlled JSON fails closed. Existing historical hashes and signatures
are never recomputed.

Problem Report links and governed impact decisions advance the owning TCR concurrency token. Submission and
other controlled mutations use `ExpectedVersion`; both pre-check mismatches and actual EF concurrency collisions
return `409 stale_version` without partial state. The test suite contains deterministic true-two-context
collision coverage.

### Assignment and controlled revision integrity

Ordinary Test Engineers act on unassigned or self-held packages. Test Leads and Administrators have documented,
attributable supervisory authority. Server-derived capabilities and mutation authorization use the same policy.
Revising approved TCR work creates one successor, supersedes the predecessor atomically, preserves predecessor
evidence, and refuses competing successor creation.

## Qualification at the Stage 4 closure head

The final PR head `6cc22acd36a1f984d54dabf2a11a952325051c2b` passed:

- Domain: 286 / 286
- Infrastructure: 202 / 202
- API: 293 / 293
- client lint and type-check, with one pre-existing warning only
- production build
- focused browser journeys: 20 / 20
- production-build journeys: 10 / 10
- full local three-shard browser suite: 147 passed plus one intentional capture-only skip
- PostgreSQL migration and secure-bootstrap CI

The post-merge `main` quality gate then completed successfully at `d06fcee...`.

## Fresh independent audit findings after merge

The Aug. 7–8 review did not modify product code. It raised implementation-ready issues against the sole source
of truth and reopened one prior effectivity issue where the new implementation still exhibits the same root
problem.

### Highest priority

- **#395 — Verification read and evidence-download endpoints bypass Program project isolation.** Several
  verification reads/downloads do not establish access to the owning Program. Treat as a critical security
  boundary and address before broader use.
- **#214 — Released workspace leaks project-latest test-procedure revisions across build boundaries.** Reopened:
  Procedure Explorer effectivity and TCR Modify/Retire targets are still derived from coverage/project-global
  latest revisions instead of the exact baseline procedure manifest.
- **#400 — TCR procedure proposals can name requirement revisions outside the package's governed scope.** The
  UI offers scoped choices, but the mutation accepts arbitrary same-Project/same-level revisions.
- **#401 — Modifying a test procedure can silently drop coverage for unchanged requirements.** Modify
  materialization applies only the proposal's named links instead of carrying forward the predecessor coverage
  and recording explicit deltas.

### Other confirmed issues

- **#396 — TCR fold endpoint accepts source Change Requests that are not approved.**
- **#397 — New automatic TCRs can be submitted without an engineering case.**
- **#398 — TCR approval does not satisfy the documented electronic-signature authentication contract.**
- **#399 — Procedure Explorer Trace & impact hides the exact requirements a procedure verifies.**
- **#402 — TCR authoring pickers silently truncate requirements and procedures at fixed page limits.**

### Existing issue reconciled

- **#365 remains open only for browser/history presentation.** Atomic predecessor supersession, exact successor
  identity, retained evidence, concurrency protection, and released-build refusal are already implemented. Its
  body now identifies only the remaining browser/deep-link/baseline journey.

## Recommended next sequence

1. Fix #395 on a dedicated security branch, with two-Program negative API coverage across every affected route.
2. Fix #214, #400, and #401 as one carefully bounded configuration-effectivity increment only if their shared
   data model can be kept coherent; otherwise use separate PRs with an explicit dependency order.
3. Fix #398 before treating TCR electronic signatures as equivalent to the password-confirmed SRCR contract.
4. Address #397 and #396 to close current TCR lifecycle/completeness gaps.
5. Address #399 and #402 as focused reachability/scalability work.
6. Complete #365's remaining browser/history journey.

Do not combine all findings into one giant branch. Each PR must state the exact issue(s), test matrix, database
isolation, and merge authorization boundary.

## Documentation state

This handoff is the current restart point. The documentation-audit PR also updates:

- `README.md` current-handoff pointer;
- `PROJECT_STATE.md` date, current verification surface, Stage 4 checkpoint, and known limitations;
- `AEROLINK_3_IMPLEMENTATION_STATUS.md` current checkpoint and qualification evidence;
- `SECURITY_AND_IDENTITY_MODEL.md` configurable-stage wording and the open TCR signature gap.

The older `CURRENT_PRODUCT_HANDOFF_2026-08-06.md` and earlier dated files remain historical. PR #377, which
contains a pre-Stage-3B temporary DeepSeek continuation prompt, is obsolete once this handoff PR exists and
should remain closed rather than be rebased or merged.

## Restart checklist

Before beginning any new implementation:

1. `git fetch origin --prune`
2. `git switch main`
3. `git pull --ff-only origin main`
4. confirm the exact current `origin/main` SHA from GitHub;
5. confirm `git status --short` is empty;
6. create a new focused branch;
7. read the target issue and its relationships;
8. use disposable infrastructure for all automated tests;
9. update this handoff and `PROJECT_STATE.md` when the product truth changes;
10. never merge without explicit owner authorization.
