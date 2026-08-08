# AeroLink Aug. 7–8 independent audit and Codex remediation handoff

**Status date:** 2026-08-08  
**Purpose:** give Codex one complete, current and execution-ready handoff for the independent review of the work delivered primarily by Claude and DeepSeek on Aug. 7–8, 2026.

This document records the audit result, the exact repository state reviewed, every confirmed issue, the documentation reconciliation, the safe implementation order, and the instructions Codex must follow. It is deliberately more detailed than a chat prompt. The short prompt given to Codex should point here rather than attempting to restate this document.

---

## 1. Authoritative repository state

- Repository: `seanmccarthyns/requirements-management-tool`
- Sole source of truth: GitHub
- Authoritative branch: `main`
- Exact audited and qualified `main` SHA: `d06fcee94473a9128a98e58b3699c1f6c0ad3af6`
- That commit is the squash merge of PR [#388](https://github.com/seanmccarthyns/requirements-management-tool/pull/388), **Stage 4: first-class manual Test Change Request authoring**.
- Post-merge Product Quality Gate run `31269258110` completed successfully on that exact `main` commit.
- The former Stage 4 branch was deleted remotely after merge.
- Documentation/audit PR [#403](https://github.com/seanmccarthyns/requirements-management-tool/pull/403) is open as a **draft**, documentation-only PR on branch `codex/aug-7-8-audit-docs`.
- PR #403 must remain unmerged unless the owner explicitly authorizes its merge.
- Obsolete continuation PR [#377](https://github.com/seanmccarthyns/requirements-management-tool/pull/377) was closed without merge after being superseded by the merged Stage 4 state and this audit.

The earlier Stage 4 continuation instructions that named `a2beb2909f3e31741240339ae838e832bbde9b1d` as `main` and instructed agents not to merge PR #388 are historical. PR #388 has since been owner-authorized, merged and qualified. Do not restart from that old state.

Before starting any implementation, fetch GitHub and verify the current `main` head. If GitHub has advanced beyond the SHA above, treat current GitHub as authoritative and reconcile this handoff against the intervening commits. Never force a local checkout to become the source of truth.

---

## 2. Standing safety and governance rules

These rules override convenience.

1. **Never commit directly to `main`.** Use one focused branch per issue, a pull request, required quality gates, explicit owner merge authorization, and a squash merge.
2. **Never merge or enable auto-merge without explicit owner authorization.** A green check is not authorization.
3. **Never alter GitHub governance or control-plane settings** to make work easier. Do not change branch protection, rulesets, required checks, strict/up-to-date requirements, enforce-admin settings, repository settings, merge policy, or GitHub Actions protection.
4. If GitHub protection blocks an otherwise-ready action, stop and report the exact blocker. Do not weaken or bypass protection.
5. There is one persistent engineering PostgreSQL database, normally on port `54329`. Do not reset, replace, seed, restore, directly edit, or use it for automated validation.
6. Automated tests and reproductions must use disposable SQLite/PostgreSQL infrastructure created by the test harness.
7. Do not mutate persistent engineering data merely to demonstrate a fix.
8. Build 1.5 (`SW-01.50`) is released, immutable and read-only. Build 1.6 (`SW-01.60`) is the active in-work workspace.
9. Do not make certification, compliance, or tool-qualification claims.
10. Do not ship an AI product capability under this workstream.
11. Do not rewrite historical signatures, review hashes, approved records, or audit evidence to make a new implementation agree with the past.
12. On Windows, stop a running AeroLink instance before builds when it is holding product DLLs open. Do not treat a locked output assembly as a source-code failure.

---

## 3. Scope of the independent review

The review covered merged work through PR #388, with emphasis on changes delivered Aug. 7–8:

- exact test-procedure manifests and build-scoped procedure effectivity;
- the Stage 3B direct Verification surfaces: Change Requests, Test Procedure Explorer and Test Results;
- production-served React client and production-build browser qualification;
- Test Change Request procedure Introduce / Modify / Retire authoring and materialization;
- first-class manually raised System/HLR/LLR TCRs;
- multi-source manual TCR creation, fold/unfold and source claims;
- assignment and controlled TCR revisioning;
- configured TCR review workflows, fallback approval, signatures and notifications;
- canonical review snapshots and optimistic concurrency;
- build scoping and released-build restrictions;
- Program/project authorization boundaries;
- procedure-to-requirement traceability reachability;
- current CI and post-merge `main` qualification;
- current project, security, status and restart documentation.

The Stage 3B and Stage 4 work is substantial and generally coherent. The audit did not conclude that the entire delivery was defective. It found several focused but important gaps in security, configuration integrity, review evidence and ordinary engineering usability.

---

## 4. Confirmed GitHub findings

### 4.1 Critical — #395: verification read and evidence-download endpoints bypass Program isolation

Issue: [#395](https://github.com/seanmccarthyns/requirements-management-tool/issues/395)

Several verification read/download endpoints accept a project, baseline, execution or evidence identifier and return controlled information without first proving that the authenticated user can access the owning Program.

Confirmed examples in `product/src/AeroLink.Api/VerificationEndpoints.cs` include:

- `GET /api/evidence/{id}`
- `GET /api/traceability?projectId=...&baselineId=...`
- `GET /api/test-executions?projectId=...`
- `GET /api/verification-coverage?projectId=...`

Adjacent routes in the same module do call `HasProjectAccessAsync`, so there is no shared middleware that makes these omissions harmless.

**Impact:** an authenticated member of Program B who learns or guesses Program A identifiers may be able to read Program A traceability, coverage, execution data, evidence metadata or evidence bytes. Responses may also disclose that hidden records exist.

**Required correction:** resolve the owning Project/Program server-side where possible; enforce project access before returning a row, count, metadata object or file stream; validate that supplied project/baseline/build parameters belong together; avoid unnecessary existence disclosure; audit all remaining read-only verification routes so the correction is complete.

**Minimum acceptance:** two-Program API tests, browser-session and service-identity coverage, deterministic cross-project refusal, no filename/hash/count leakage, and unchanged same-Program behavior.

This is the first implementation priority and should be handled in its own security PR.

### 4.2 High — reopened #214: procedure effectivity is inferred from coverage rather than the exact build manifest

Issue: [#214](https://github.com/seanmccarthyns/requirements-management-tool/issues/214)

The Stage 3B Procedure Explorer changed the UI but did not fully correct the original build-boundary defect.

`GET /api/test-procedures?releaseId=...` and the exact-history membership check still derive procedure membership from requirement coverage and then select the highest procedure revision found. They do not use the exact `BaselineTestProcedureSelection` manifest produced by `TestProcedureBaselineMaterializer`.

That is not configuration-equivalent:

- a procedure carried by the build with zero current coverage can disappear;
- a newer procedure revision from another build can replace the selected build's exact revision;
- historical membership is inferred from trace relations instead of read from the controlled configuration;
- TCR Modify/Retire authoring can offer project-global latest procedure revisions rather than the exact procedure revision active in the target build.

**Required correction:** use the effective baseline's exact procedure manifest as the common source of truth for the Explorer list, exact history/deep links, Modify/Retire candidates, exports and build-scoped search. Coverage is a trace relationship; it must not define which procedure revision a build carries.

### 4.3 High — #400: TCR procedure proposals can govern requirement revisions outside the package scope

Issue: [#400](https://github.com/seanmccarthyns/requirements-management-tool/issues/400)

The UI offers driving requirements reached through the TCR's own `VerificationImpactItems`. The mutation endpoint, however, accepts any requirement revision that exists in the same Project and matches the procedure level.

A direct API caller can therefore attach a procedure Introduce/Modify proposal to an unrelated requirement revision or one from another build. Materialization can turn those identifiers into real `TestCoverage` links.

**Impact:** false coverage, cross-build traceability, and approval of engineering scope the TCR was never raised to govern.

**Required correction:** define one server-side permitted set of requirement revisions for the TCR, based on its own source changes/impact work and valid target build; use it in the picker, mutation and materializer; fail closed without partial writes.

### 4.4 High — #401: modifying a procedure can silently drop unchanged predecessor coverage

Issue: [#401](https://github.com/seanmccarthyns/requirements-management-tool/issues/401)

When a TCR creates the next revision of a procedure, the materializer links only the requirement revision IDs explicitly named in the proposal. It does not carry forward the predecessor revision's existing coverage set.

A procedure that verifies A and B can be modified because A changed and emerge linked only to A. B becomes uncovered without any explicit removal proposal, rationale or reviewer-visible delta.

**Required correction:** a Modify starts with the predecessor's exact coverage set; unchanged links carry forward; additions and removals are explicit, attributable, rationalized, snapshot-protected deltas; the approved final coverage set is applied atomically. Do not require authors to manually reselect every unchanged link.

### 4.5 High — #398: TCR approval does not satisfy the electronic-signature contract

Issue: [#398](https://github.com/seanmccarthyns/requirements-management-tool/issues/398)

The current TCR approval request captures rationale only. It records an `ElectronicSignature` without re-confirming the user's password and uses the engineering rationale as the signature meaning. For the no-configured-workflow fallback, the approval route also does not defensively recheck current Approver authority when the signature is applied.

**Impact:** an unattended or misused authenticated session can apply a signature without current credential knowledge; engineering rationale and explicit signatory intent are conflated; revoked fallback authority may still complete approval.

**Required correction:** password reconfirmation immediately before signing; distinct approval rationale and signature meaning; frozen configured-stage authority for configured workflows; current Approver authority for the no-workflow fallback; exact snapshot hash/stage attribution; no partial transition on failure. Existing signatures remain unchanged historical evidence.

### 4.6 High — #396: generic TCR fold accepts source Change Requests that are not approved

Issue: [#396](https://github.com/seanmccarthyns/requirements-management-tool/issues/396)

Manual TCR creation restricts sources to `Approved` or `SelectedForBaseline`, but `POST /api/test-change-reviews/{id}/change-requests` does not check the source Change Request state. Draft, In Review, Deferred or otherwise ineligible changes can be claimed by an Open TCR.

**Required correction:** one shared source-eligibility predicate for picker, manual create and fold; authoritative state check inside the mutation; no claim, supersession, item movement, version or audit side effect on refusal; test the complete lifecycle-state matrix and state-change races.

### 4.7 High — #397: newly raised automatic TCRs can be approved without an engineering case

Issue: [#397](https://github.com/seanmccarthyns/requirements-management-tool/issues/397)

Stage 4 established Title, Problem, Analysis and Solution as the controlled engineering case the approver judges. Submission currently requires those fields only when `Title` is already nonblank. A newly generated automatic TCR can keep all four fields blank and proceed through review and approval.

Historical records created before case authoring must remain readable without fabricated content, but new automatic work is not historical compatibility.

**Required correction:** an explicit legacy/schema distinction; complete case required for every newly created/current manual or automatic TCR; field-specific UI guidance; no rewriting of historical hashes or signatures.

### 4.8 Major — #399: Procedure Explorer Trace & impact hides the exact requirements

Issue: [#399](https://github.com/seanmccarthyns/requirements-management-tool/issues/399)

The Trace & impact tab currently reports only that a procedure verifies `N` requirements. A count is not a trace. It does not identify the exact requirement revisions, statements, levels, coverage/suspect state or navigation targets.

The history API already exposes some exact `covers` and `drivenBy` information, but the client does not render a complete, build-correct inverse trace.

**Required correction:** an exact revision-scoped procedure trace projection; display and navigate every requirement revision; distinguish confirmed/suspect links; show producing TCR/source provenance; preserve selected-build effectivity and refresh/deep-link context.

### 4.9 Major — #402: TCR authoring pickers silently truncate candidates

Issue: [#402](https://github.com/seanmccarthyns/requirements-management-tool/issues/402)

Several controls load one fixed page and present it as the complete candidate universe:

- quick procedure authoring: at most 200 requirement revisions;
- coverage-confirmation selector: at most 200 approved procedures;
- TCR Modify/Retire projection: at most 500 procedures.

The FMS demonstration data already exceeds these limits. A valid artifact may exist but be impossible to select, with no count, warning, paging, search or exact-ID hydration.

**Required correction:** bounded server-side search by controlled number/title/statement, stable paging and total metadata, exact selected-item hydration by immutable ID, server-enforced Project/build/discipline eligibility, and browser tests above the former limits.

### 4.10 Existing #365 was reconciled rather than duplicated

Issue: [#365](https://github.com/seanmccarthyns/requirements-management-tool/issues/365)

PR #388 already implemented atomic TCR successor creation and predecessor supersession, exact successor reference, retained review/signature history, concurrency protection and released-build restrictions.

The issue remains open only for the browser/history work that still remains:

- show only the successor as active engineering/review work;
- show the predecessor as `Superseded` through History/deep links;
- provide an explicit route to the successor;
- prove the predecessor is not offered or accepted for new baseline selection;
- preserve the distinction after refresh.

Do not reimplement the domain/API supersession correction.

---

## 5. Finding deliberately not raised as an issue

The generic requirement discussion-resolution route was rechecked because the Procedure Explorer reuses the collaboration model. It resolves the owning comment and calls `HasProjectAccessAsync` against the comment's Project before changing it.

No duplicate or speculative authorization issue was created for that route. Do not reopen this concern without a new reproduction or materially different path.

---

## 6. Documentation and tracker reconciliation completed

Draft PR #403 updates the current product truth rather than rewriting long-lived historical records.

Documents reconciled:

- `CURRENT_PRODUCT_HANDOFF_2026-08-08.md` — new authoritative restart point, exact product SHA, Stage 3B/Stage 4 behavior, audit findings and safe sequence;
- `README.md` — points readers to the Aug. 8 handoff and treats Aug. 6 as historical;
- `PROJECT_STATE.md` — current product surface, Stage 4 control model, qualification and known limitations;
- `AEROLINK_3_IMPLEMENTATION_STATUS.md` — current checkpoint, evidence and focused audit backlog;
- `SECURITY_AND_IDENTITY_MODEL.md` — configurable-stage authority, intended signature contract, and honest #395/#398 qualification boundaries;
- this file — execution handoff for Codex.

Long-lived roadmap/catalog text and older dated handoffs remain historical decision records. They are not current backlog authority and must not be blindly converted into work.

The audit itself changed no product code, migrations, workflows, repository governance, branch protection or persistent engineering data.

---

## 7. Qualification evidence at the reviewed checkpoint

Stage 4 PR #388 closed at head `6cc22acd36a1f984d54dabf2a11a952325051c2b` with:

- Domain: 286 / 286;
- Infrastructure: 202 / 202;
- API: 293 / 293;
- client lint/type-check: passed, with one pre-existing warning only;
- production build: passed;
- focused browser journeys: 20 / 20;
- production-build journeys: 10 / 10;
- full local browser suite: 147 passed plus one intentional capture-only skip;
- PostgreSQL migration and secure-bootstrap validation: passed.

The squash merge produced `main` commit `d06fcee94473a9128a98e58b3699c1f6c0ad3af6`. Post-merge Product Quality Gate run `31269258110` passed on that exact commit.

PR #403 is documentation-only. Its quality gate classification may correctly skip backend, browser, production-build and PostgreSQL jobs. A green documentation PR is not new product qualification; the product evidence remains the PR #388 and exact-merge evidence above.

Test counts are checkpoint evidence, not constants. Remediation PRs will legitimately add tests and increase them.

---

## 8. Required remediation order

Do not combine the findings into one unbounded hardening branch.

1. **#395 alone** — critical Program confidentiality/security boundary.
2. **#214** — establish the exact baseline procedure manifest as the build-effectivity source of truth.
3. **#400** — constrain driving requirements to the TCR's governed package/build scope.
4. **#401** — preserve predecessor procedure coverage and govern explicit add/remove deltas.
5. **#398** — bring TCR signing to the password-confirmed electronic-signature contract.
6. **#396 and #397** — source lifecycle eligibility and current engineering-case completeness. These may be separate PRs unless one narrowly shared predicate/contract makes a small combined correction clearly safer.
7. **#399 and #402** — trace reachability and scalable server-backed pickers; normally separate PRs.
8. **#365** — remaining browser/history presentation for superseded TCR revisions.

Do not start the next issue on a branch based on unmerged work unless the owner explicitly authorizes a stacked-PR strategy. The default is: complete one focused PR, stop before merge, let the owner authorize/merge, update local `main`, then start the next branch.

---

## 9. Codex first assignment: issue #395 only

Codex must begin with #395 and carry it to a review-ready, CI-green draft PR. Do not implement the remaining issues in the same branch.

### 9.1 Orient safely

1. Read this file in full.
2. Read `CURRENT_PRODUCT_HANDOFF_2026-08-08.md`, `PROJECT_STATE.md`, `SECURITY_AND_IDENTITY_MODEL.md`, and the complete body of issue #395.
3. Fetch `origin` and verify current GitHub `main`.
4. Confirm the worktree is clean.
5. Create a focused branch from current `origin/main`, suggested name:

   `codex/issue-395-verification-program-isolation`

6. Do not use `codex/aug-7-8-audit-docs` for product code. It is the documentation branch for PR #403.

### 9.2 Investigate and reproduce before changing code

Use disposable infrastructure and a two-Program scenario:

- Program A has a baseline, requirements, coverage, execution and evidence;
- the test identity belongs only to Program B;
- call every confirmed route with Program A identifiers;
- record the exact current response, including whether it discloses counts, identifiers, metadata or bytes;
- inspect all read/download routes in `VerificationEndpoints.cs`, not only the four confirmed examples;
- compare with adjacent routes that correctly establish access;
- inspect browser-session and service-identity authorization paths.

Do not use the persistent `54329/aerolink` database for reproduction.

### 9.3 Correct the boundary

The correction must:

- resolve ownership server-side from the requested resource wherever possible;
- verify Project/Program access before returning data or streaming files;
- verify that supplied Project, baseline, release/build, execution and evidence identifiers belong to one coherent scope;
- prevent unnecessary existence disclosure;
- preserve valid same-Program behavior;
- avoid a route-by-route patchwork if a small, existing/shared authorization helper can make the invariant clear;
- avoid broad unrelated refactors.

Do not rely on React to enforce authorization. The API remains authoritative.

### 9.4 Test strongly

At minimum add focused API tests proving:

- Program A member can read Program A data and evidence;
- Program B-only member cannot read Program A traceability;
- Program B-only member cannot read Program A coverage;
- Program B-only member cannot read Program A executions;
- Program B-only member cannot read Program A evidence metadata or bytes;
- cross-project baseline/build parameter combinations are refused;
- unauthorized responses do not disclose controlled filenames, hashes, counts or identifiers unnecessarily;
- service identities obey the same project boundary;
- existing same-Program browser journeys/downloads remain green.

If the route contract intentionally returns `404` rather than `403` to avoid existence disclosure, use one consistent established product policy and test it. Do not mix responses arbitrarily.

### 9.5 Validate the complete product impact

Run the smallest focused tests during development, then all repository-required gates selected by the change, including:

- focused API/security tests;
- full Domain suite;
- full Infrastructure suite;
- full API suite;
- client type-check and lint;
- production build;
- focused browser journeys affected by verification/download behavior;
- production-build journeys if selected;
- full browser shards if selected by repository policy;
- PostgreSQL migration/secure-bootstrap gate if selected by the classifier;
- the required enforcing reporter.

Investigate every failure. Do not relabel a real failure as flaky without evidence.

### 9.6 Publish safely

1. Commit coherent changes on the focused branch.
2. Push the branch.
3. Open a **draft** PR against `main`.
4. Link issue #395 and use `Fixes #395` only if the PR fully satisfies its acceptance criteria when merged.
5. Update the PR body with root cause, routes audited, authorization model, tests and exact evidence.
6. Let CI complete and investigate failures.
7. Do not make the PR ready, merge it, enable auto-merge or change governance unless the owner explicitly authorizes those actions.
8. Stop after the PR is review-ready and CI-green. Do not begin #214 from an unmerged #395 branch by default.

---

## 10. Required final report from Codex for #395

The completion report must state:

- old exact `main` SHA used as the base;
- branch name and new exact head SHA;
- PR URL;
- issue URL;
- reproduced root cause;
- every route audited and whether it was affected;
- authorization/error contract after correction;
- files changed;
- focused and full test counts;
- exact GitHub Actions run ID and result;
- any skipped jobs and why;
- remaining risks or routes not covered;
- confirmation that persistent PostgreSQL and GitHub governance were untouched;
- recommended squash title;
- explicit statement that the PR was **not merged**.

---

## 11. Instructions for later issues

After the owner authorizes and merges #395, refresh `main` and continue in the sequence in section 8. For every issue:

- reread the live GitHub issue because its body is the implementation contract;
- reproduce against disposable infrastructure before coding;
- search current GitHub state for intervening fixes/duplicates;
- use a focused branch and draft PR;
- preserve historical controlled evidence;
- add domain/API/infrastructure/browser coverage appropriate to the risk;
- run required gates;
- stop before merge.

When correcting #214/#400/#401, treat the exact baseline procedure manifest, TCR governed scope, and approved final coverage set as three related but distinct invariants. Do not solve one by weakening another.

When correcting #398, do not impose a generic Approver role on configured TestLead/ConfigurationManager stages. Preserve the frozen configured-stage authority while requiring password-confirmed signing and distinct signature meaning. The no-workflow fallback additionally requires current Approver authority.

When correcting #397, do not fabricate case content for historical automatic TCRs and do not recompute their recorded review hashes/signatures. Introduce an explicit compatibility/version distinction.

When correcting #402, do not fix truncation by loading the entire Project into the browser. Use bounded server-side search/paging plus exact-ID hydration.

---

## 12. Definition of a successful handoff execution

This handoff is successfully executed when Codex:

- starts from current GitHub truth rather than stale local assumptions;
- completes issue #395 on a focused branch;
- proves the defect and the correction with two-Program tests;
- audits the surrounding verification read/download surface;
- leaves the persistent database and repository governance untouched;
- opens a complete, CI-green draft PR;
- provides the required evidence report;
- stops before merge and before starting the next remediation issue.
