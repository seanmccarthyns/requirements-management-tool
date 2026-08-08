# AeroLink 3.0 - implementation status

**Status date:** 2026-08-08
**Qualified product checkpoint:** `main` at `d06fcee94473a9128a98e58b3699c1f6c0ad3af6` after Stage 4 PR #388

This is the current scorecard for the long-lived
[AeroLink 3.0 completion contract](AEROLINK_3_ENTERPRISE_LIFECYCLE_COMPLETION.md). The contract describes the
full enterprise ambition; this file describes what the repository truthfully delivers now. Detailed restart
context is in [CURRENT_PRODUCT_HANDOFF_2026-08-08.md](CURRENT_PRODUCT_HANDOFF_2026-08-08.md).

## Status vocabulary

- **MVP delivered:** the product-owned acceptance boundary is implemented and qualified.
- **Delivered foundation:** useful production code is implemented, but the complete enterprise contract remains
  broader than the supported product surface.
- **Deferred by decision:** intentionally outside the current MVP until a recorded trigger is met.
- **Deployment-owned:** requires a selected customer topology, provider, credentials, or service objectives and
  cannot be completed truthfully inside this repository alone.
- **Historical/dormant:** retained implementation or evidence is not a supported current route.

## Overall position

The original AeroLink 3.0 parent program (#29) and its planned enterprise workstreams are delivered, deferred by
recorded decision, or deployment-owned. Subsequent focused increments delivered active Problem Reports,
downstream assessments, exact build-scoped verification, the production-served single-origin client, Stage 3B
verification navigation, and Stage 4 first-class manual Test Change Request authoring.

Closing those increments does **not** mean AeroLink claims certification, tool qualification, completed customer
deployment, or that no product defects remain. It means the supported MVP is coherent and qualified at the named
checkpoint. The independent Aug. 7–8 audit raised focused follow-up issues #395–#402 and reopened #214; they are
current GitHub backlog, not contradictions to the completed enterprise-program status.

## Workstream scorecard

| Workstream | Current status | Product evidence and boundary |
| --- | --- | --- |
| 1. Universal controlled editing | **MVP delivered** | change request checkout, renewable leases, autosave snapshots, recovery, check-in/discard, read-only observers, optimistic versions, forced unlock audit, and retained conflict evidence. Test procedures are deliberately excluded from direct universal editing under DEC-103; their controlled changes occur through TCRs. |
| 2. Problem-report lifecycle | **MVP increment active** | Project-scoped Problem Reports are navigable and searchable, carry target-build attribution, drive change request/TCR work, project approved corrective actions and selected evidence, and remain read-only in released-build context. Broader classification and closure policy remains incremental under DEC-085/DEC-089. |
| 3. Product-line configuration and reuse | **Delivered foundation** | Canonical software builds, exact immutable baselines, released 1.5/read-only and active 1.6 workspaces, controlled libraries, propagation decisions, variants, configuration-correct outputs, deterministic publications, and release evidence. Exact procedure effectivity remains under reopened #214. |
| 4. Enterprise identity and account assurance | **MVP delivered; federation deferred** | Local accounts, MFA/recovery codes, Program roles, individual role revocation, distinct global/Program administration, current/other session controls, time-bounded delegation lifecycle, electronic signatures, security audit, provider/mapping foundations, and PostgreSQL migration coverage. OIDC/SAML and SCIM resume only with a real directory contract. TCR signature parity is tracked by #398. |
| 5. Resumable interchange and monitored integrations | **Delivered foundation** | Governed CSV/XLSX onboarding, ReqIF profile round trip, scoped service identities, versioned API, transactional events, HMAC webhooks, retry/dead-letter replay, Jira mapping/link-back, OSLC foundations, and inspectable notification outbox. A real SMTP relay and vendor/provider-specific contracts remain external qualification work. |
| 6. Rich technical content and controlled publications | **MVP delivered** | Structured rich content, approved template revisions, deterministic SYSRD/SWRD/test/change outputs in DOCX/PDF, exact provenance, document control, redlines, publication jobs, manifests, and release evidence packages. Managed Word documents use the desktop connector and retain exact DOCX/PDF candidates. |
| 7. Quality, evidence and portfolio intelligence | **MVP delivered with focused audit backlog** | Build-scoped Command Center; direct System/HLR/LLR Change Requests, Test Procedure Explorer and Test Results surfaces; controlled manual and automatic TCRs; staged review; Build Test Sets; verification decision history/reopening; downstream assessments; exact upward allocations; release readiness; and immutable evidence/retest history. Follow-up integrity/reachability issues are #214 and #395–#402. |
| 8. Production operations and qualification | **Product foundation delivered; deployment-owned remainder** | One-click development/production/shared launchers, API-served production client, readiness, diagnostics, cryptographic attachment checkpoints, manifested backup/verification, isolated restore, retention/hold evidence, upgrade evidence, PostgreSQL migration/bootstrap, production-build browser tests, 50,000-requirement qualification, and 150-client database workload. Protected off-device storage, external alert delivery, TLS/reverse proxy, scheduler provisioning, and approved RPO/RTO/SLOs require a selected deployment. |

## Current control model

- Build 1.5 (`SW-01.50`) is released, immutable, and read-only.
- Build 1.6 (`SW-01.60`) is the active controlled development workspace.
- System, Software HLR, Software LLR, and each verification discipline use explicit build context.
- System approval raises an HLR downstream assessment; HLR approval raises an LLR assessment.
- PRs may drive every change-request type; requirement changes never manufacture a PR.
- Every procedure covering an introduced or modified requirement is mandatory pre-release scope and cannot be
  removed from that build's test set.
- HLR proposals allocate to current System revisions; LLR proposals allocate to current HLR revisions. An
  explicit derived classification with rationale is the only alternative.
- Approved changes raise discipline-specific test assessments. Test Change Requests may also be raised manually
  over one or more approved source changes and carry governed procedure-change proposals.
- Configured review workflows freeze stage identity, order/mode, authority, and version on each review cycle;
  where none is configured, the independent-Approver fallback remains.
- Approved procedure changes materialize only through an approved TCR; there is no direct procedure mutation or
  separate procedure-level approval.
- Immutable results and evidence over the build test set determine readiness.

## Qualification evidence

Stage 4 PR #388 closed at head `6cc22acd36a1f984d54dabf2a11a952325051c2b` with:

- Domain 286 / 286;
- Infrastructure 202 / 202;
- API 293 / 293;
- client lint and type-check (one pre-existing warning only);
- production build;
- focused browser journeys 20 / 20;
- production-build journeys 10 / 10;
- full local browser suite: 147 passed plus one intentional capture-only skip; and
- PostgreSQL migration and secure-bootstrap validation.

The squash merge produced `main` commit `d06fcee94473a9128a98e58b3699c1f6c0ad3af6`. Post-merge Product Quality
Gate run `31269258110` completed successfully on that exact commit. Browser shards skipped by the main-push
classifier had run successfully on the immediately preceding PR merge candidate.

## Current focused backlog from the Aug. 7–8 audit

Priority is determined by reproduced/confirmed product risk, not issue number:

1. #395 — cross-Program verification read/evidence-download authorization.
2. #214 — exact build procedure-manifest effectivity, including TCR Modify/Retire targets.
3. #400 — driving requirement revisions outside the TCR's governed scope.
4. #401 — procedure Modify silently dropping unchanged coverage.
5. #398 — TCR electronic-signature password/meaning/current-authority contract.
6. #396 and #397 — source lifecycle and current-package case completeness.
7. #399 and #402 — procedure trace reachability and bounded searchable authoring pickers.
8. #365 — remaining browser/history presentation for superseded TCR revisions.

Each is implementation-ready in GitHub. Do not combine them into one unbounded hardening branch.

## Boundaries that must remain explicit

- No certification, compliance, or tool-qualification claim.
- No generic identity federation without a provider/customer contract.
- No fake production concurrency or integrity simulations in the product.
- No claim of 150 rendered browser users; the published evidence is 150 simultaneous database clients.
- No claim that repository scripts provision customer backup storage, monitoring, TLS, or recovery objectives.
- No reset of the persistent demonstration database merely to prove an increment.
- No claim that TCR approval is password-confirmed until #398 is completed.
- No claim that Procedure Explorer/current TCR target effectivity is exact until #214 is completed.

## Governance

Current issue/PR state must always be refreshed from GitHub; counts in dated records are historical. New work
starts from a reproduced product need, not from an old roadmap sentence. Use focused branches and pull requests,
wait for the required Product Quality Gate, obtain explicit owner merge authorization, squash merge, pull
`main`, and requalify the exact merge commit. Repository governance settings and the persistent database are not
changed to make delivery convenient.