# Security, Identity, and Electronic Approval Model

Status: implemented foundation, subject to formal organizational security review before production use.

## Purpose

AeroLink decisions must be attributable to authenticated people, not browser-supplied names. Identity, authority, electronic signatures, and security auditing are lifecycle controls rather than presentation features.

## Identity and session controls

- Accounts are local and on-premises in the current implementation. A future enterprise connector may federate Active Directory or another approved identity provider without changing artifact history.
- Usernames are normalized, unique, and retained after an account is disabled so historic authorship remains resolvable.
- Passwords are stored only as salted PBKDF2-SHA-256 derivations with 310,000 iterations. Plaintext passwords are never persisted.
- Eight consecutive failures lock an account. Successful authentication resets the failure counter.
- The browser receives a random opaque session token in an HTTP-only cookie. Only its SHA-256 digest is stored. Sessions expire after 12 hours and can be explicitly revoked.
- Account Security identifies the current session, lists retained session history, and permits a user to revoke
  other active sessions without revoking the session performing the action.
- Material API routes require an authenticated session. Actor names supplied by clients are ignored; the server derives the actor from the session.

The Documentation Center desktop connector never receives a reusable browser session. AeroLink issues a
short-lived one-use launch token scoped to one document revision and mode. After redemption, the connector keeps
its scoped access token only in memory, renews the exclusive lease while Word is open, and cannot use that token
to browse another Project or artifact. Remote origins require HTTPS; loopback HTTP remains available for local
demonstration. Stale-source check-ins fail without overwriting another user's work.

## Program authority

Authority is scoped to a Program through additive role assignments:

| Role family | Current roles and intended authority |
|---|---|
| General control | Engineer, Reviewer, Approver, Configuration Manager |
| Verification | Test Engineer, Test Lead |
| Program leadership | Program Manager, Administrator |
| Engineering jobs | System Engineer, Software Engineer, System Engineering Lead, Software Engineering Lead, Project Engineering Lead, Engineering Manager |
| Independent stakeholders | Software Quality Analyst, Airworthiness |

A precise engineering job satisfies the general Engineer authority it implies; naming the real job must not
remove ordinary authoring capability. Independent stakeholder roles do not implicitly gain engineering write
authority. Global system administration is separate from a Program's Administrator role.

Role delegations are time-bounded, Program-scoped, attributable, and revocable. Current, future, expired, and
revoked records retain Program, delegator, delegate, role, interval, reason, granting/revoking actor, and state.
Only an active interval grants authority. Delegation does not rewrite the original assignee or historic identity.

## Review-stage authority

A configured review procedure names the authority required by each stage rather than assuming every stage is a
generic Approver. The selected person must hold the exact required authority when the review starts; Administrator
substitution remains explicit. The authority actually used is frozen on the `ApprovalStep` so the historical
record remains explainable after memberships change.

Where no configured procedure exists, the backwards-compatible independent-Approver fallback applies. The
selected reviewer must hold Approver authority, including the established administrator/delegation rules. The
server remains authoritative even when a browser picker is filtered.

## Electronic approval

The required electronic-signature contract is:

- the user has an active authenticated session;
- the user is the person assigned to an active review stage;
- the applicable configured-stage or no-workflow fallback authority is satisfied;
- the user re-enters the correct password immediately before signing;
- the user provides an explicit signature meaning distinct from engineering rationale; and
- the server applies the domain transition and immutable signature record in one successful unit of work.

Each signature captures the user identifier, username and display-name snapshot, Program, artifact type and stable identity, artifact revision, action, meaning, exact content or snapshot hash, source address, and server timestamp. A signature supplements—not replaces—the artifact's ordered review history.

### Current implementation qualification

Ordinary System/Software Change Request approvals use password re-confirmation and configured-stage authority.
Managed-document and release approval paths retain their own qualified contracts.

Test Change Request approval currently records the active-stage signature without password re-confirmation and
uses approval rationale as the signature meaning. Its no-workflow approval route also lacks the defensive current
Approver check used by the SRCR closure path. This is an open review-integrity defect tracked by **#398**. Until
that issue is completed, do not describe TCR signatures as equivalent to the password-confirmed SRCR contract,
and do not rewrite historical TCR signatures.

## Separation of duties

The foundation prevents identity impersonation and requires explicit assignment. Program policy can impose
artifact-specific combinations such as author-not-approver, independent assurance approval, or mandatory
Configuration Manager release authority. Configured workflows are versioned controlled procedures because
appropriate separation varies by organization, artifact type, and assurance level.

TCR self-approval is refused and active-stage identity is enforced. Ordinary Test Engineers work unassigned or
self-held packages; Test Leads and Administrators use attributable supervisory authority. These rules do not
remove the electronic-signature gap identified above.

## My Work and management visibility

My Work is a server-derived queue, not a manually maintained task list. It identifies active change-request,
downstream-assessment, verification, procedure, Test Change Request, release, and owned-draft work for the
authenticated person. Administrators can inspect accounts, state, last access, and current Program roles;
create local accounts; grant or revoke one role; and disable accounts without deleting historic attribution.
Duplicate grants fail as conflicts. Security audit events retain successful and denied logins, logout, account
administration, role grant/revocation, session revocation, and delegation lifecycle actions.

## Untrusted input and response hardening

Everything a browser or a machine account sends is a claim, and the boundary treats it as one.

- **Session reachability is decided in one place.** A middleware ahead of endpoint routing decides which paths are reachable without a session, from an explicit path list. `.AllowAnonymous()` on an endpoint has no effect there, because that middleware runs before the endpoint is selected and has no metadata to read. Anything intended to be reachable from a mail client — the unsubscribe link — must be named in that list and must prove its own authority instead, which it does through an HMAC over the recipient. An unsubscribe request answers identically whether the account exists or not, so the link cannot be used to enumerate people.
- **Authentication is not authorization.** Every project/artifact read and write must resolve the owning Program and prove the caller is entitled to know it exists. The independent Aug. 8 audit found verification read/download routes that do not consistently enforce this boundary; **#395** tracks the required cross-Program correction. Until fixed, the verification evidence/trace read surface is not qualified for multi-Program confidentiality.
- **A declared content type is not a fact.** An uploaded inline image is checked against its own signature bytes before it is stored, because it is later streamed back from this deployment's own origin and referenced from an approved requirement.
- **Imported XML cannot reach outward or expand without bound.** ReqIF packages and spreadsheet parts are read with document type definitions prohibited, no resolver, and a ceiling on characters actually read. A zip file's declared entry sizes are checked too, but that check alone governs a number the sender chose.
- **Every API response carries protection headers** — `nosniff`, `DENY` framing, no referrer, and a content security policy of `default-src 'none'; sandbox`. An API returns data, never a document, so nothing it serves should be able to load or run anything. The API-served production client has its own explicit same-origin document CSP; deployment reverse proxies may add stricter organization-owned headers but must not weaken it.
- **Secret comparison is constant-time** for passwords, session tokens, service API keys, webhook signatures, one-time codes, the bootstrap secret, and stored file digests.
- **The sign-in limiter is flood control for one network address, not the control against password guessing.** That control is the account itself, which locks after eight failures wherever they come from. On-premises, an engineering group can reach the server through one corporate proxy and present one address, so the limit is budgeted for a site rather than a person.
- **Rich content is stored as structured blocks, never as markup.** There is no stored HTML anywhere in the product, so there is nothing to sanitize and no injection surface to keep closed.

## Production hardening still required

Before operational deployment, the organization must define password policy, TLS and certificate management, reverse-proxy topology and its document response headers, backup and recovery, session revocation procedures, the procedure for releasing a locked account, privileged-administration oversight, clock synchronization, log retention/export, vulnerability management, and enterprise identity integration. The local demonstration password must be replaced and demonstration credential prefill removed.

The product must also close #395 and #398 and re-run their multi-Program/security and electronic-signature
qualification before claiming the corresponding controls are production-ready.

Account lockout is deliberately permanent until an administrator releases it, which means anybody who can reach the sign-in page can lock an account they know the name of. That is the correct trade for a controlled tool — an attacker must not be handed an automatic retry window — but it makes the release procedure an operational requirement rather than an optional one.