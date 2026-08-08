# Project State — Start Here

**Last updated: 2026-08-08.**

This is the orientation record for anyone — human or model — picking up AeroLink. It answers *what
exists, what is true today, what is deliberately not being built, and where to start*. Every other
document in this repository is either a durable definition or a historical record; this one describes
the present.

When work changes the state of the project, update this file in the same change.

## What AeroLink is

An on-premises aerospace development assurance platform: the authoritative record for controlled
requirements and the evidence chain around them. It manages system requirements, software HLRs and
LLRs, change requests, review and approval workflows, immutable baselines, generated controlled documents,
Word-authored managed documents, test procedures, externally produced results and evidence, typed traceability,
release campaigns, and a complete audit trail.

It exists to answer questions that are normally scattered across documents, spreadsheets and people:
*what exact requirement revision was approved for this release, which change authorized it, what
verifies it, what failed, who approved it, and can this document be reproduced years later?*

Read [PROJECT_VISION.md](PROJECT_VISION.md) for the full statement and
[PRODUCT_PRINCIPLES.md](PRODUCT_PRINCIPLES.md) for the fifteen behavioral rules that constrain every
design decision.

## What AeroLink is not

These are settled boundaries, not gaps awaiting work. They come from the original product brief and
are recorded in [SCOPE_AND_BOUNDARIES.md](SCOPE_AND_BOUNDARIES.md).

- **No certification, compliance, or tool-qualification claim.** The product is *informed by* ARP4754
  and DO-178 concepts and terminology. It does not claim to satisfy their objectives, and it is not a
  qualified tool. Never add language that implies otherwise.
- **No AI.** This is a hard delivery rule of the current program, not an oversight. No suggestion,
  scoring, generative, or assistant capability ships. It may be reconsidered as an explicitly
  governed, human-controlled future capability; it is not in scope now.
- **No structured plans or standards content management.** AeroLink may control externally authored Word plans
  as files, revisions, approvals, and released renditions; it does not replace Word or interpret their prose.
- **No architecture, design, or source-code management, and not a Git host.**
- **No automated test execution.** Tests run in external environments; AeroLink controls the
  procedures and captures or imports the results and evidence.
- **No deferral for test procedures.** Change requests can be put away for another day; procedures
  cannot. A requirement that is new or modified in the build being worked on is assumed to need
  coverage, so the procedures verifying it cannot be shelved while it ships — deferring one would
  remove coverage from a requirement still in the build and record it as ordinary planning. The
  deferral that matters happens one level up, on the change request, and verification work already
  follows its change request. See [DEC-058](DECISIONS_AND_OPEN_QUESTIONS.md).
- **Not a document editor.** Requirements and verification publications remain generated outputs of controlled
  data. For a Managed Document, the checked-in Word file is the controlled source and AeroLink is its storage,
  revision, review, and release authority; Microsoft Word remains the editor and AeroLink does not interpret the
  document prose as structured lifecycle data.

## Repository layout

| Path | What it is |
| --- | --- |
| `product/` | **The application.** This is the only software in the repository. |
| `product/src/AeroLink.Domain` | Lifecycle rules and invariants. Domain logic lives here, not in controllers. |
| `product/src/AeroLink.Infrastructure` | EF Core persistence, provider selection, migrations. |
| `product/src/AeroLink.Api` | HTTP boundary. |
| `product/client` | React + TypeScript user interface. |
| `product/tests`, `product/client/tests` | Backend test projects and Playwright browser journeys. |
| `design/mockups`, `docs/mockups` | North-star visual concepts. Reference material, not specifications. |
| Root `*.md` | Authoritative product definition. See the index in [README.md](README.md). |
| Root `*.docx` | The original supplied briefs, retained unmodified for provenance. |
| Root `*.bat` | Windows operator entry points for start, stop, backup, daily backup scheduling, restore, diagnostics. |

A `showcase/` directory previously held a Phase 0.5 static-data prototype. It was retired on
2026-07-24 — see DEC-046. The product application is now the single demonstrable artifact.

## Technology

React and TypeScript client; ASP.NET Core on .NET 10 with Entity Framework Core; PostgreSQL for real
use; SQLite for isolated tests and disposable local runs. A modular monolith with explicit domain,
infrastructure and API boundaries. See [product/docs/ARCHITECTURE.md](product/docs/ARCHITECTURE.md).

## What is built and working

The full controlled chain runs end to end:

change request authoring with server-leased exclusive checkout and autosave recovery → sequential *and*
parallel author-selected/configured approval sequences with frozen snapshot hashes → stage-authorized
electronic signatures over immutable snapshots → candidate baseline assembly with SHA-256 freeze →
deterministic baseline materialization → generated SYSRD/SWRD and test-procedure documents in DOCX and PDF
with approval provenance and document control → versioned test procedures → external execution import with
evidence and immutable retest chains → typed, version-aware traceability with suspect links and impact
analysis → a verification-impact queue that raises test work when an approved change alters what must be
verified → a governed release campaign with computed readiness gates and ordered release approval.

Password re-confirmation is implemented on the qualified SRCR and other established approval paths. Test Change
Request approval currently lacks password re-confirmation and a distinct signature-meaning input; #398 tracks
that gap and this document does not claim TCR signature parity until it is closed.

Around that core: enterprise requirements workspace, configurable artifact schemas, saved views and
structured queries, governed bulk operations, visual redlines, CSV/XLSX onboarding that lands in a
Draft SRCR rather than bypassing approval, ReqIF 1.2 round trip, a versioned REST API with scoped
service identities, webhooks with HMAC signing and dead-letter replay, OSLC RM, product-line libraries
and variants, backup with integrity manifests, a current-user automatic daily backup schedule, and isolated
restore drills.

Delivered since, and not to be omitted when describing the product: **email notification of required
approvals** through an outbox over the existing in-app notification record; **rich authored content** —
tables, figures and symbols stored as structure rather than markup, so nothing ever becomes HTML — in
requirement statements and change-request narrative, reproduced in the generated DOCX and PDF; **configurable
review workflows**, where a project records who signs a change request, in what authority and in what order,
versioned and never edited in place; a **Jira connector** with field mapping and link-back; and **approved
document templates that decide what a generated document contains**, rather than being numbered and approved
while a generator ignored them.

The Aug. 7–8 increment added the API-served production client, production-build browser qualification, direct
verification surfaces named **Change Requests**, **Test Procedure Explorer**, and **Test Results**, and
first-class manual Test Change Requests. A manual TCR may cover several approved source Change Requests, carries
its own rich engineering case and Problem Reports, preserves moved verification-impact history, and executes a
configured sequential or parallel review workflow. New review cycles use a canonical versioned JSON snapshot of
the governed package; source/link/impact mutations and submission share optimistic concurrency and deterministic
true-collision tests.

Change control reads as two facts rather than one. **Allocation** says which build a change request is going
into, or that it is **deferred** — put away for another day with the state it had reached remembered, so a
signed-off change on the shelf is distinguishable from an unwritten one, and reinstating returns it exactly
there. **State** says how far it has got: Draft, In review, Approved, Incorporated once the build ships, and
Superseded once a later revision exists. The last two are derived from the release and from the revision set
rather than stored, so neither can disagree with reality. Listings show each change request's newest revision
with its superseded history one click away (DEC-056). A released build takes no new change requests and no
revisions of old ones (DEC-055, DEC-054).

Authoring says where a requirement goes and what the traces already know. An author chooses the specification
section a proposed requirement belongs in, applied at materialization — introduced requirements land there and
modified ones move (DEC-057). The proposal may show a read-only live trace of the requirements derived from this
one and the procedures that verify it. The author does not disposition downstream trace, verification,
document, baseline/build, collaboration or lifecycle consequences; the engineers who consume and triage the
change make those decisions in their governed workspaces (DEC-071, which supersedes DEC-059 and the
impact-disposition portion of DEC-062).

Software Drafts retain their HLR or LLR workspace even before the first requirement proposal exists. Problem
Reports and test change requests are Build-scoped controlled records with explicit identifiers, truthful totals,
and preserved superseded history. Downstream assessment evidence is read before the engineer records a
conclusion, and Release Readiness exposes every candidate change through a searchable selector rather than
choosing one implicitly. Controlled dialogs are viewport-bound at any scroll depth.

Documents are offered where the requirements are read, not only on the Digital Thread. The build decides which:
the approved controlled document for a released build, or a draft at the revision the released document will
carry, generated from the released baseline plus every approved change and stamped DRAFT on every page — never
stored, because a controlled record of content that is still moving is a record of nothing.

Identity: local accounts, Program-scoped roles, sessions, MFA with recovery codes, mandatory
temporary-password rotation, scoped service accounts, and security audit. Administrators can see and revoke
individual current role grants, distinguish global system administration from Program Administrator authority,
identify the current session, revoke other sessions, and govern time-bounded delegations without deleting
expired or revoked history.

Software change control now governs both directions of allocation. Approval raises a downstream assessment
owned by the consuming discipline: System to HLR, then HLR to LLR. Before approval, an HLR proposal selects
current System revisions from the target build and an LLR selects current HLR revisions, or the author records
an explicit derived exception with rationale. Exact selected revision IDs are reviewed, retained through
checkout and change-request revisioning, and materialized as immutable `AllocatedFrom` traces.

Draft authoring separates two deliberate actions. **Save Draft** persists incomplete work without issuing
empty records or pretending it is review-ready; **Save and check in** applies the controlled working copy and
closes its edit session. HLR and LLR histories, requirement pickers, and procedure inventories stay isolated,
while exact controlled references remain searchable and navigable. A modified software requirement hydrates
its existing exact upstream revisions, including historical revisions no longer active in the current baseline,
without widening the current-build candidate search.

Downstream assessments now read as engineering decisions rather than storage states: pending, in progress, in
review, complete with no impact, complete with controlled impact, change required with a Draft change request pending, or
superseded, always labelled HLR or LLR. A deep-linked assessment drawer shows the source SRCR and its complete
change case, changed requirements, and the current downward trace. An engineer may record no impact, link a
level-compatible Draft change request, or create the correct HLR/LLR Draft directly; the new Draft is linked automatically,
and a failed link remains visible and retryable without losing the saved Draft.

Each queue row carries one control, "Open assessment", whatever state the assessment is in; the drawer offers
only the actions that state permits. Both conclusions appear in exactly one state — claimed and undecided.
Wherever a conclusion exists it is stated with its author, its rationale and, once approved, its approver.
Correcting a wrong conclusion is its own act: **Reopen assessment** takes a stated reason, returns the
assessment to undecided, detaches any linked Draft change request without changing the change requests themselves, and keeps the withdrawn
conclusion — outcome, author, rationale, approver and detached numbers — in the drawer's withdrawn-conclusions
record. An unapproved conclusion is the assigned engineer's to withdraw; an approved one takes Approver
authority; an assessment in review is returned rather than withdrawn behind its approver (DEC-090).

Verification impact: approving a change request raises an item for every requirement it introduces or
modifies, and for any procedure a retirement leaves covering nothing. A Test Lead distributes items;
a Test Engineer resolves each one by naming an approved procedure, by recording that no test is required — a
requirement the author declared verifiable by analysis still needs that confirmation — or by recording that a
procedure must be written and does not exist yet. That third answer is an answer and never verification: it
settles the `verification_impact` gate, because somebody has looked and decided, and deliberately does not
settle coverage, so the release keeps waiting until the procedure is approved. The procedure is authored from
the decision, pre-linked to the exact requirement revision, and approving it advances the decision without the
engineer returning to re-answer.
Undecided items hold the `verification_impact` release-readiness gate, so they block release approval; they
deliberately do **not** block the baseline freeze, because freezing and materializing is what creates the
requirement revisions a procedure is written against. "Decided" means the procedures are authored and
approved — it says nothing about whether they have been executed.

A test change request opens as a workbench: the record itself is the disclosure, and it shows its source
change requests, who holds it, its linked Problem Reports, and one decision per requirement carrying the
coverage that requirement already has — covered with its procedures named, suspect with the procedure that
must be reconfirmed or replaced named, none, or not yet knowable because the build has not materialised its
requirements. Suspect is stated before "no procedure", because a reader who sees "covered" stops looking and
that is the case most likely to be answered wrongly.

A manually raised TCR adds a controlled Title / Problem / Analysis / Solution case and may claim several
approved source changes. Folded source assessments' impact items move to the surviving package with identity,
assignment, decision, attribution, and history retained; unfold restores a fresh actionable assessment. Review
cycles expose active/pending/completed stages, freeze the applicable workflow and authority, and retain prior
cycles after return and resubmission. Current automatically raised TCRs still have a completeness gap: #397
tracks the ability to submit a newly generated package without ever writing its engineering case.

Coverage is a question the requirements workspace can now answer. A **coverage-state filter** builds a worklist
of what is Covered, Suspect or Uncovered, and every row carries its state; the suspect and uncovered ones are
buttons that open the verification trace rather than labels that only read. Covered means one thing everywhere —
the link is not suspect, the procedure revision it names is approved, and that procedure has no revision still in
flight — because the release readiness gate, the workspace filter and the trace panel now read one predicate
(DEC-067). They did not: the gate applied all three conditions while the trace panel counted "confirmed tests"
from the suspect flag alone, so a requirement could show a confirmed test beside a row that called it suspect.

The showcase covered all 1,250 of its requirements and so could never demonstrate the product finding a gap. One
FMS 1.6 work item now puts an approved System procedure back into revision, making the two requirements it covers
suspect while released FMS 1.5 is left exactly as it was. **No uncovered requirement is seeded** — reaching one
would need either a released baseline that failed its own coverage gate or a materialized FMS 1.6, and both are
worse than the missing state. Uncovered appears the moment somebody materializes 1.6 (DEC-068).

Materialization is where the loop closes, because it is the first moment requirement revisions exist. Each
item binds to the exact revision its change produced; coverage on a modified requirement carries forward
onto the new revision marked **suspect**; a decision that named a procedure becomes the real coverage link,
clearing the suspect flag rather than duplicating it; and a procedure a retirement left covering nothing
raises its own item. **Suspect coverage is not coverage**: the `coverage` readiness gate counts only
confirmed links, so a requirement cannot reach release on the strength of a procedure written against its
previous wording.

The independent Aug. 8 review found two unclosed materialization/trace invariants. #400 tracks procedure
proposals that can name same-level requirement revisions outside the TCR's governed source/build scope. #401
tracks Modify materialization that can silently drop unchanged predecessor coverage because only the proposal's
explicit driving IDs are linked to the new procedure revision.

**Bringing in a program that already exists elsewhere** is a separate act from proposing a change (DEC-093).
An import creates an **externally sourced baseline** directly, released on arrival, carrying the provenance
that lets it be told apart from a baseline this product built: source system and version, source baseline
name and date, extract file name, size and SHA-256, who took the extract and when. Five gates run in order
and none can be skipped — Source, Analyse, Map, Reconcile, Accept — and a named person accepts. The page
states what that signature asserts beside what it never asserts: that these requirements were reviewed or
approved here. Every source identifier survives as a searchable record joined to its controlled requirement
by a provenance link, and an object the source retired before the imported baseline is recorded so a
reference to it can be answered while joining nothing. Reading the file itself is not built: the gates are
driven by structured input, which is what makes them exercisable before a parser exists.

Presentation: one design system across every surface — a 12px readability floor, four radii, one type
scale, one focus treatment — with **comfortable and compact information density** expressed as spacing
tokens applied through the workspace shell, and **WCAG 2.2 AA as a commitment**: 4.5:1 body contrast,
3:1 large text, and 24x24 minimum target sizes, all measured on rendered pixels by
`product/client/tests/accessibility-contrast.spec.ts` and `design-system.spec.ts` in both densities.

## Current product flow and visible surface

Authentication no longer drops a user into an implicit FMS workspace. The supported path is
**Projects → FMS Product Development → Software Builds → build-scoped workspace** (DEC-070).
`SW-01.50` (informally Build 1.5) is released and read-only; `SW-01.60` (informally Build 1.6) is in work and
editable. Baseline and software build are one product concept. Changing build requires leaving the
workspace through **Back to Software Builds**. There is no in-workspace build switcher, and a released build
does not show a completion percentage.

Inside a build, System and Software remain separate engineering areas. The Command Center summarizes System,
Software and Verification work. System change creation is direct; Software creation first asks HLR or LLR.
Change history, requirements, search and verification are scoped to the active build, and historical evidence
is labelled with its originating build without changing workspace context.

Problem Reports are active and Project scoped: one Problem Report database per Project, identical whichever
build the reader is standing in, with the target build an attribute of the record and an explicit filter a
user may choose rather than one the workspace applies (DEC-089, superseding the build-scoping half of
DEC-085 and DEC-087). They carry the agreed Draft-to-Closed
lifecycle, progressive rich fields, immutable raised-by/date, auditable owner/target-build changes, structured
impact decisions, AND filters, and an internal History tab. A report is corrected under the same exclusive
server lease as every other controlled record — **Check out & edit**, autosave, check in, discard, and a
named holder while somebody else has it — in every state except Closed and the terminal dispositions, where
reopening is the route back. Each check-in lands in the report's own History as `Details Checked In` with its
actor and time (DEC-091). Their center supports durable detail links and
links forward to SRCRs, HLRCRs, LLRCRs, every TCR discipline, requirements, procedures, executions/evidence, documents,
and releases where those records exist. Every change-request type can select one or more driving PRs; approved
engineering changes are projected back as corrective actions, and only results selected to support closure
are projected as test evidence. Product Versions, Candidate
Baselines, and the old Change Request Software Builds view remain dormant under DEC-072. Lifecycle Decision
Room remains visible.

Procedures covering requirements introduced or modified by the active build are automatically included as
mandatory changed-requirement tests. They cannot be removed from the build test set, and the exact-revision
result/evidence gates prevent release until they pass with evidence.

Verification is three pages per discipline rather than one overloaded workspace. **Change Requests** answers
"what approved change affects this discipline's tests, what package carries the answer, and what procedure work
is being proposed/reviewed" — downstream test assessments, verification-impact decisions, automatic/manual Test
Change Requests, source folding, Problem Reports, procedure-change proposals, assignment, and staged review.
**Test Procedure Explorer** answers "what controlled procedures does this build carry, what is their exact
history, and what do they verify". **Test Results** answers "what does this build have to run, and what happened
when it was run" — the build test set, recorded determinations with evidence, run history, retests, and the
corrective action a Problem Report sends somebody here to perform. System has one trio; software has separate
HLR and LLR trios because the work is planned, done, and approved separately.

The Explorer and authoring effectivity contract is not yet fully qualified. Reopened #214 tracks the fact that
procedure lists/history and TCR Modify/Retire targets derive from coverage/project-global latest revisions rather
than the exact baseline procedure manifest. #399 tracks the Explorer Trace tab showing only a count instead of
the exact requirements. #402 tracks fixed-page candidate truncation at current dataset scale.

Primary navigation mirrors that work: **Requirements** owns change requests, requirements, requirements
documents, and Digital Thread; **Verification** owns the direct Change Requests, Test Procedure Explorer, Test
Results, and verification-document destinations; **Code**, **Documentation Center**, and **Problem Reports** are
standalone destinations in that order. Documentation Center controls Word-authored lifecycle documents without
replacing Word. Code records exact LLR-revision-to-GitLab-merge pointers without hosting source, and released
Build 1.5 is read-only. Legacy verification chooser/deep-link URLs remain supported where explicitly retained,
but no redundant generic Verification workspace defines the current surface. Legitimate assurance role names
and production-assurance terminology are unchanged.

A build's test work is scoped by a **Build Test Set** — one per build per discipline, a working list rather
than a controlled artefact, recording who put each procedure in it and why (changed requirement, coverage
area, corrective action, or simply chosen). The release gates measure that set. The older
"evidence required before release" flag on an individual decision no longer has a control that sets it; it
survives server-side only as one of the inputs that seeds a new set (DEC-073 superseded by DEC-076).

Approved changes still create System, Software HLR and/or Software LLR test assessments. A controlled TCR is
allocated when the assessment concludes that procedure work is required; it may also be raised manually when
several approved source changes are best tested as one package. TCRs have their own numbers/revisions, rich case,
Problem Reports, exact source identities, governed impact decisions, staged review cycles, and successor history.

The restart-ready description, exact qualified `main`, audit findings and safe next sequence are in
[CURRENT_PRODUCT_HANDOFF_2026-08-08.md](CURRENT_PRODUCT_HANDOFF_2026-08-08.md).

## The demonstration dataset

`FMSLIVE` is a deterministic, production-shaped program built through the same domain and persistence
rules as any user-created program — not a mock data layer. Enabled by `DemoData:Enabled`, disabled by
default in production configuration.

Released **FMS 1.5** baseline: 150 system requirements, 400 HLRs, 700 LLRs, 1,250 effective revisions,
30 SRCRs, 44 HLRCRs and 55 LLRCRs, 1,100 typed traces, 515 procedures, 520 executions including retained retests, 6
controlled documents, 1 released build. **FMS 1.6** is derived from it and deliberately in work, with
persistent controlled work spread across approved, in-review, draft and deferred states. Its counts evolve as
realistic engineering qualification adds records; they are not a fixed seed-data contract.

The tool never auto-creates or auto-approves a successor release. Details in
[FMS_LIVE_SHOWCASE_DATASET.md](FMS_LIVE_SHOWCASE_DATASET.md).

## Where delivery stands

The original AeroLink 3.0 implementation program and its review follow-ups have been reconciled. Subsequent
focused increments through PR #388 delivered active Problem Reports, the August engineering-observation work,
managed Word documents, direct verification surfaces, production-shaped client hosting, and Stage 4 manual TCR
authoring with hardened consolidation, review snapshots, staged workflows, assignment and concurrency.

`main` at `d06fcee94473a9128a98e58b3699c1f6c0ad3af6` passed the post-merge Product Quality Gate. That does not mean
there is no backlog. The independent Aug. 7–8 audit raised #395–#402, reopened #214, and reconciled #365 to its
remaining browser/history scope. Residual identity federation and deployment operations remain closed with
explicit resume conditions because they require a real provider or hosting contract, not generic product
simulation.

Per-workstream status is in [AEROLINK_3_IMPLEMENTATION_STATUS.md](AEROLINK_3_IMPLEMENTATION_STATUS.md). Its
vocabulary distinguishes **MVP delivered**, **deferred by decision**, **deployment-owned**, and **focused audit
backlog** from an unqualified claim that every enterprise deployment capability is complete.

**[PRODUCT_REVIEW_2026_07_26.md](PRODUCT_REVIEW_2026_07_26.md)** holds the findings from the first evening of
using the product as an engineer would. **Every item in it is now closed** — the six defects, and all nine that
needed a product decision first. Its impact-disposition outcome was later superseded: computed trace remains
useful context, but downstream decisions now belong to consuming engineers rather than the change author
(DEC-071). The file is retained as the record of what was found and decided, not as current product direction.

A second evening of review followed on 27 July, and its eleven observations are also closed. Four of them were
not missing features but unreachable ones: a Revise action gated on a state no change request in the programme
rested in, a deferral shelf the domain supported and nothing exposed, a change-type field that was read-only on
the one proposal that arrived pre-seeded, and section filtering that worked on the read side while no authoring
path could set a section. **The recurring failure was reachability, not absence** — code that existed, was
correct, and had no route to it.

## Known limitations — state these accurately

Understating these is a product-integrity failure, not a marketing choice.

- **Multi-Program verification confidentiality is not qualified.** The Aug. 8 audit found verification read and
  evidence-download routes that do not consistently establish access to the owning Program. #395 is critical
  and must be closed before broader multi-Program use.
- **Build-scoped procedure effectivity is not yet sourced consistently from the exact procedure manifest.**
  Reopened #214 covers Procedure Explorer/history and TCR Modify/Retire targets. Do not claim every procedure
  surface is configuration-correct until it is closed.
- **TCR trace/materialization has two open integrity defects.** #400 covers out-of-scope driving requirement
  revisions; #401 covers Modify silently dropping unchanged predecessor coverage.
- **TCR lifecycle/electronic-approval parity is incomplete.** #396 covers folding unapproved sources; #397
  covers current automatic packages bypassing the case; #398 covers password/meaning/current-fallback authority.
- **Procedure trace and authoring reachability are incomplete.** #399 covers the count-only Trace tab; #402
  covers silent candidate truncation at fixed limits.
- **No build carries a procedure manifest yet.** The mechanism is complete and reachable, but every existing
  build predates it, so `baseline_test_procedures` is empty in the demonstration data. `MarkReleased`
  deliberately does not require one; gating on it would make already-released builds retrospectively invalid.

- **The scale claim is 150 simultaneous *database clients* and 50,000 requirements on one workstation,**
  with zero failures. This is **not** 150 rendered browser sessions on production topology, and must never
  be described as such. The HTTP path has since been measured too — the `session-load` harness signs in 150
  real authenticated sessions — and that measurement is what found the sign-in limiter refusing 121 of 150
  users. But per-page latency was measured from 10 to 50 concurrent sessions, and one query still caps the
  requirements workspace, so the *claim* does not change. Say "database clients", and say the HTTP path is
  measured but the user number is not yet supported. See
  [product/docs/SCALE_FOUNDATION.md](product/docs/SCALE_FOUNDATION.md) and the path to 150 users in
  [CAPABILITY_ROADMAP.md](CAPABILITY_ROADMAP.md), which is costed and deliberately not started.
- **Email delivery exists but no mail server is configured.** An outbox writes a delivery row in the same
  transaction as the domain change and a background dispatcher sends it over the organization's SMTP relay.
  With no relay configured, deliveries stay Pending and inspectable rather than being dropped. Nothing has
  been proved against a real relay, so treat "notifications reach people by email" as built and unexercised.
  This removed the hard dependency that self-service account recovery was blocked on.
- **Production deployment is not complete.** TLS, certificate and secret management, reverse-proxy
  topology, scheduled off-device backups, monitoring, retention enforcement and an independent
  security review remain organization-specific work. See
  [SECURITY_AND_IDENTITY_MODEL.md](SECURITY_AND_IDENTITY_MODEL.md).
- **Demonstration credentials are non-production** and must be replaced before any operational use.
- **An imported baseline cannot yet be read from a file.** The five import gates are exercisable and
  enforced, but nothing parses a DOORS or ReqIF extract: the objects, their attributes and any source history
  are supplied as structured input, and the extract's hash and size are recorded from whoever took it rather
  than computed from an upload. Say the workflow is built and the reader is not. It is deliberately waiting
  on a representative extract rather than on a design.
The client has **no external runtime dependency**: it makes no network request outside its own origin,
and has been verified to start with all external requests blocked. Keep it that way — a CDN reference
in the client contradicts the on-premises posture and, as the resolved case below showed, can block
first paint for seconds on a restricted network. See DEC-047.

## How to run it

PostgreSQL must be installed once on a new machine — `product\scripts\Setup-Postgres.ps1`, which downloads
roughly 360 MB from `enterprisedb.com`. Neither launcher does this, and on a restricted network it is the
step most likely to fail.

**To demonstrate AeroLink, or to see what a deployment serves:** `START_AEROLINK_PRODUCTION.bat`. It builds
the client and serves it from the API on one origin at `http://127.0.0.1:5080` — one process, one port, no
CORS. This is the on-premises shape and the only path that runs the built client (DEC-052).

**To work on AeroLink:** `START_AEROLINK.bat`, which runs the Vite **dev** server on `http://127.0.0.1:5173`
against the API on 5080. `STOP_AEROLINK.bat`, `AEROLINK_DIAGNOSTICS.bat`, `BACKUP_AEROLINK.bat`,
`VERIFY_AEROLINK_BACKUP.bat` and `RESTORE_AEROLINK.bat` cover the rest. Full procedures in
[product/docs/OPERATIONS.md](product/docs/OPERATIONS.md); developer path and test commands in
[product/README.md](product/README.md).

Both launchers wait on `/health/ready`, which opens a database connection. They previously waited on
`/health`, which answers "is the process listening" and is true with no database at all.

The browser journeys run on Linux, macOS and Windows: `cd product/client && npx playwright test`, after
`npx playwright install chromium` once. They were Windows-only until the Playwright configuration stopped
launching its servers through a PowerShell prologue. Set `AEROLINK_E2E_SKIP_BUILD=true` to reuse an
already-built API and cut about a minute per run.

`npm run test:production` runs a separate set of journeys against the **built** client served by the API.
Everything else serves the client with `vite dev`, which is a different artifact — unbundled modules with
stylesheets injected as they evaluate, rather than chunked code and one extracted, hashed stylesheet. Expect
it to catch things the dev journeys structurally cannot. It now performs protected writes immediately after
deep-linked sign-in, verifies the resulting System SRCR and immutable verification result through the API, and
fault-injects network and conflict responses to prove that failed writes preserve input and create no record
(DEC-061).

CI runs the dev journeys and the production journeys according to changed-area classification, plus scheduled
coverage. The required reporter states exactly which jobs ran and refuses a pass that validated nothing. The
Windows gate runs backend, client lint/type-check/build, and product journeys; PostgreSQL migration/bootstrap
runs against disposable infrastructure when selected.

Local demonstration identities (`admin`, `systems.author`, `software.author`, `systems.reviewer`,
`release.manager`) share a local-only password documented in `product/README.md`. Production
deployment uses the one-time protected administrator bootstrap instead.

Requirement proposal metadata is now one durable server contract (DEC-062). Initial change request creation preserves
schema-allowed `owner`, `criticality`, and future configured attributes while recomputing the server-owned
`derived` flag. Exact section placement survives create, detail, checkout/check-in, review and baseline
materialization; stale section identifiers are rejected with a repair instruction. Administrators can identify
legacy authored-attribute gaps through `/api/authoring/attribute-gaps`. DEC-071 supersedes the former
five-area author-impact requirement: review and downstream lifecycle operations no longer block on those
fields, and integrity checkpoints do not treat their absence as a violation.

Change-request context and review attribution are also controlled facts (DEC-063). Detail links now encode
System or Software and self-correct old generic or mismatched links from the authorized record. Search, My Work,
notifications and Jira preserve the same discipline. Each approval step retains the canonical selected account,
display name, workflow stage and resolved Program authority; the review UI no longer substitutes a showcase
person, while the actual signing account remains separate immutable signature evidence.

Authenticated mutation attribution is server-owned (DEC-064). Browser request contracts no longer accept
caller-selected author, actor, owner, recorder, or executor values; durable provenance comes from the
authenticated session or service principal. Operations diagnostics are credentialless and session-free by
default, with an explicitly optional scoped-service probe for authentication capability.

Administrator recovery authority is also server-owned (DEC-065). An administrator with Project access may
complete the original author's Draft/deferred workflow or create an approved record's successor without
becoming that record's author. State, release and concurrency guards remain unchanged; durable audit, attachment
and controlled-editing evidence identifies the administrator as the actual actor. The rule and browser journey
are shared by System and Software change requests.

Test-procedure applicability begins at exact baseline materialization (DEC-066). Before that lifecycle point,
new procedure authoring is disabled with the reason and the governed materialization prerequisite; the former
Product Versions and Candidate Baselines pages are not exposed in the current product surface. Existing inherited
procedures remain tied to their predecessor revisions and change-impact work remains planned rather than
counted as coverage. Release readiness exposes traceability, coverage, verification, and evidence as
`WaitingForPrerequisite` with `baseline` as their dependency. Once materialized they become evaluated gates;
an empty effective population remains an explicit HOLD, not a successful or waiting `0/0`.

## How this project governs itself

AeroLink is developed under the same discipline it sells. Respect it — these conventions are the
reason the document set can be trusted.

- **Markdown in Git is authoritative.** Generated Word or PDF copies are snapshots, not sources.
- **Decisions are append-only.** Recorded in
  [DECISIONS_AND_OPEN_QUESTIONS.md](DECISIONS_AND_OPEN_QUESTIONS.md) as `DEC-nnn`. If a decision
  changes, add a superseding decision and retain the original. Never edit a decision's meaning in place
  and never silently change it in another document.
- **Capabilities get stable identifiers** in [FEATURE_CATALOG.md](FEATURE_CATALOG.md). Identifiers are
  never reused.
- **Scope changes are recorded**, not made quietly.
- **Normative language is deliberate**: **must** is mandatory, **should** is preferred, **may** is
  optional.
- **Deferrals are written down** with reason, resume trigger and excluded acceptance criteria — see
  Workstream 4 for the worked example.

## Lessons this project has already paid for

- **A green gate is not evidence a capability is reachable.** An identity migration was authored
  without the attributes Entity Framework needs to discover it, so it never ran; the tables existed
  only inside a hand-written test fixture, and every endpoint depending on them would have failed at
  runtime. Every test passed, because no test and no smoke step ever called those endpoints. Guard
  tests now fail the build if a migration is undiscoverable or the model drifts from its snapshot.
  When adding a capability, ask what would fail if it were entirely absent — and make sure something
  does.
- **Migrations must be generated, not hand-authored.** Set `AEROLINK_MIGRATIONS_CONNECTION` to a disposable
  PostgreSQL connection first (design-time EF fails closed and never defaults to the persistent database; see
  `product/docs/OPERATIONS.md`), then run
  `dotnet ef migrations add <Name> --project src/AeroLink.Infrastructure --startup-project src/AeroLink.Api --output-dir Persistence/Migrations`.
  Entities must also be mapped in `AeroLinkDbContext`, because the non-PostgreSQL path builds its
  schema from the model rather than from migrations.
- **Generated is not the same as correct — read every migration before running it.** EF scaffolds a new
  non-nullable string column with `defaultValue: ""`. Where that column holds an enum converted with
  `HasConversion<string>()`, `""` is not a member name and *every existing row fails to materialize* — a
  total outage of that entity from a migration that looks routine. It was caught twice in one day. The same
  habit catches a table rename scaffolded as `DropTable` plus `CreateTable`, which silently deletes the data.
- **Test a performance hypothesis before shipping the fix for it.** A plausible explanation for CI sign-in
  timeouts — that Entity Framework builds its model on the first query, after the readiness probe has already
  reported ready — was implemented and then measured at 167 ms against 170 ms. Identity seeding had already
  warmed the model. The change was deleted rather than shipped with a rationale that had just been disproven.
- **A green test suite is not a look at the page.** Four defects in one day passed every assertion and were
  found only by rendering the page and reading it: provenance dates shifted a day for anyone west of UTC, a
  CSS specificity collision stacked every checkbox above its label, two cards wore the same icon, and a grid
  showed a block of divider colour where its last row wrapped short. Screenshot a changed surface.
- **Prefer deferring honestly over building speculatively.** Workstream 4's remainder was deferred
  because nothing in it had a real user yet. Recording that is better than a silent backlog.
- **A test can pass by racing past the thing it checks.** The readability journey asserted that no text
  on the change-request surface renders below 12px, and it passed for months while the page in fact
  rendered 9px initials and an 11px lifecycle chip — it sampled the page before those rows appeared.
  Making the client faster removed the race and the assertion started failing, correctly. When a test
  starts failing after an unrelated performance change, suspect the test was never really exercising
  its subject.
- **A suite that cannot run where the work happens does not run.** The browser journeys launched both
  their servers through a PowerShell prologue, so they were Windows-only: they could not be executed on a
  Linux development machine at all, and CI paid the Windows rate to run them. Two real defects sat behind
  that wall — a flexbox row that overflowed the page and a release gate wired to the wrong transition —
  and neither was findable locally. The config now passes configuration through `webServer.env` and the
  same suite runs on either platform. Before trusting a gate, check that you can run it yourself.
- **A gate belongs on the transition the workflow can actually satisfy.** The verification-impact queue
  was first wired to block *baseline freeze*. Freezing and then materializing is what creates the
  requirement revisions a test engineer needs before a procedure can exist, so the gate withheld the test
  team's own inputs and deadlocked the release. It is now the `verification_impact` readiness gate on
  release approval, which is what was actually asked for. The gate also shipped with no test of its own;
  an existing journey caught it, and only once that journey could run.
- **Auditing default states is auditing the easy case.** The design contract was checked on each surface
  as it first rendered. A surface can be contained at rest and overflow the moment a panel opens — the
  requirements workspace did exactly that — and a queue with no rows in it hides every colour its rows
  use. Two contrast failures on My Work appeared only once other journeys had created work items. Audits
  now cover both densities, an opened inspector, and populated surfaces.
- **Density is spacing, not type.** Compact reduced body text to 14px, and every unstyled `<small>` — at
  the user agent's 0.8333em — silently fell from 12.5px to 11.67px, under the readability floor. The
  floor had never been measured in compact because nothing exercised compact. Relative font sizes make a
  floor unpredictable; pin the element instead of trusting inheritance.
- **A method nothing calls is a claim nothing keeps.** The verification feature shipped with
  `LinkRequirementRevision`, `CarriedForward`, `MarkSuspect` and `ConfirmStillValid` fully written, tested
  at the domain level, and never called from production code. The documentation described suspect
  carry-forward as product behaviour while no code path produced a suspect link. Domain tests pass happily
  against methods no caller reaches; before believing a capability exists, follow it from an endpoint.
- **Look for the mechanism that already exists before adding one.** Release reconciliation had been
  carrying coverage forward across baselines for as long as it existed — silently, and unmarked, which
  asserted that a procedure written against previous wording still verified the new one. Adding a second,
  safer carry-forward simply produced two mechanisms; the fix was to delete the unmarked one. A failing
  test in an unrelated area is often the first sign that the behaviour you are adding is already there.
- **An on-premises product must be measured on a hostile network, not a good one.** The client fetched
  its webfonts from a public CDN. On a fast connection this was invisible; when the request hung rather
  than failing fast — the normal behaviour of a firewall that drops packets instead of rejecting them —
  first paint took 12,994 ms instead of 147 ms. Fixed by self-hosting (DEC-047). Before shipping
  anything that loads a resource, ask what happens when that resource is unreachable *slowly*.
- **A benchmark measures what it drives, not what it is named after.** The scale harness had always
  reported 150 concurrent clients, and the documentation was careful to call them *database* clients —
  correctly, because the harness issued EF queries straight at PostgreSQL. Nobody had driven the HTTP
  path. Doing so found that sign-in refused 121 of 150 users, because the rate limiter partitioned on
  network address and an on-premises site reaches the product through one proxy: the product denied
  service to its own users, and no database-level measurement could ever have shown it. When a number is
  quoted in a unit, check that something actually measures that unit.
- **A read that writes will be slow, and the cost hides in a method that looks idempotent.** The
  requirements explorer called a project-wide backfill on every GET. It was correct, it was idempotent,
  and it loaded every requirement, revision, profile and specification node in the project before
  returning fifty rows — nine seconds a page at fifty thousand requirements. "Idempotent" says nothing
  about what it costs to discover there is nothing to do. The first guard was itself a join through
  fifty thousand rows and barely helped; the fix was to make the check one indexed count.
- **The control existed; the thing it controlled did not.** Document templates were numbered, approved by
  a named person, versioned, and hashed at approval — and their body was JSON that no generator ever
  opened. Every ceremony was real and none of it changed a document. The same shape appeared twice more
  in one week: a rich-text field nothing rendered, and an attachment vault reachable only from a screen
  nobody working on a change request would open. Before building a control, check whether the last one is
  wired to anything.
- **A rule that wins by being loaded last is a rule with no owner.** Splitting the client so each workspace
  arrives when somebody opens it also moves its stylesheet, which then lands after everything already on the
  page. Twenty row and card families immediately lost their density spacing, because Density.css set
  `padding-block` and each component set `padding` — identical specificity, decided purely by order, and the
  order had been an accident of the module graph. The same shape appeared twice more: two unrelated forms
  sharing the class `.buildForm`, and a setup-form rule imposing its grid placement on every error box in the
  product. None of the three was caused by the split; the split only removed the accident that had been
  hiding them. Before relying on a rule, ask what makes it win — and if the answer is "it happens to be last",
  it will stop being last.
- **Verify the mechanism, not just the failure.** The contrast audit began failing on a colour that had been
  wrong all along; the split had merely changed the timing enough for the element to be on screen when the
  audit sampled. Running the same test on an untouched checkout is what separated "I broke this" from "this
  was always broken" — two findings that look identical and need opposite responses.
- **A gate that cannot run on the deployment platform is not a gate for that platform.** The journeys were
  freed from Windows so they could run on Linux, and every check that could observe a Windows-only failure
  stayed on Linux with them. `RichContent.tsx` and `richContent.ts` differ only in case: two modules on
  Linux, one file on Windows, so `npm run build` and `npm run typecheck` failed on the platform this product
  is deployed to, for as long as both files existed. The Windows job ran only `npx playwright test`, and
  Playwright serves the journeys through `vite dev`, which transpiles each file without checking types — so
  the one job on the right platform was structurally incapable of seeing it. Moving a suite to where it runs
  easily is not the same as covering where the product runs.
- **Running a thing is not the same as running the thing you ship.** Every gate, both launchers and every
  journey served the client with `vite dev`. The production bundle was compiled on every pull request and
  never once rendered in a browser, on any platform — while the demonstration brief named a dry run from a
  production build as the one preparation that could not be skipped. It was not untested; the environment
  did not exist. Its first four runs found a page that scrolled sideways, an 11px label under the readability
  floor, and a content security policy that blocked eight self-hosted typefaces. Ask what artifact the gate
  is actually exercising, and whether anybody ships that one.
- **A hardcoded list of surfaces stops being a list of surfaces.** The design audit named twelve. Review
  Procedures and New Change Request arrived later and were never added, so neither had ever been measured,
  and both were breaking the contract. The production journey reads the navigation instead — it cannot go
  stale, because the product tells it what exists. Prefer enumerating the thing over describing it.
- **A fixture that changes what other tests find is not an isolated fixture.** The showcase's verification gap
  was first seeded on `SYSTP-000001`. Procedures are dealt requirements round-robin, so that procedure covers
  `SYSR-000001` and is therefore the first approved procedure any test looking for one discovers. Putting it
  into revision — the whole point of the fixture — removed it from the covering-procedure list and broke a
  journey that had nothing to do with the change. Demonstration data is shared mutable state; before changing a
  record in it, ask which gates find that record by searching rather than by name.
- **Adding a column costs the columns already there.** The coverage state needed a sixth column in the
  requirements table. Measured against an untouched checkout, the heading went from 35.8px to 52.6px and the row
  from 94.8px to 119.6px, because the narrower statement track pushed both onto a second line — one fewer
  requirement visible per screen. Nothing failed; the design contract passed throughout. A dense table has no
  spare width, so the cost of a new column is paid by the existing ones whether or not anybody measures it.
- **Specificity is the other way a rule loses.** `.richFileInput` set a visually-hidden control to one pixel
  and lost to `.controlledEditor input { width: 100% }` — (0,1,0) against (0,1,1) — so the input rendered at
  1160px and pushed the page 106px off screen. The cascade lesson above is about load order; this is the same
  failure through the other mechanism, and the same fix applies. When a rule matters, make it win on purpose.

# Current implementation checkpoint — 2026-08-08

Current `main` at `d06fcee94473a9128a98e58b3699c1f6c0ad3af6` includes the API-served production client,
qualified direct verification navigation, first-class manual Test Change Requests, multi-source impact-item
consolidation, configured staged TCR review, canonical review snapshots, assignment-aligned authority,
optimistic concurrency including true EF-collision tests, and atomic controlled TCR successor revisioning.

The post-merge Product Quality Gate passed. The fresh independent review raised #395–#402, reopened #214, and
left #365 open only for its remaining browser/history presentation. See
[CURRENT_PRODUCT_HANDOFF_2026-08-08.md](CURRENT_PRODUCT_HANDOFF_2026-08-08.md).