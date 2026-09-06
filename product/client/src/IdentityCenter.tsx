import { useCallback, useEffect, useState } from "react";
import {
  grantableProgramRoles,
  programRoleLabel,
  stateLabel,
} from "./presentation";
import type { FormEvent } from "react";
import "./IdentityCenter.css";
import {
  apiRequest,
  operationError,
  recordClientOperationFailure,
} from "./apiClient";
import "./IdentitySetup.css";
import { Icon } from "./icons";
import { PersonAvatar } from "./People";

export type AuthUser = {
  id: string;
  userName: string;
  displayName: string;
  email: string;
  isAdministrator: boolean;
  mustChangePassword: boolean;
  programs: { programId: string; roles: string[] }[];
};
export function LoginPage({
  api,
  onLogin,
}: {
  api: string;
  onLogin: (user: AuthUser) => void;
}) {
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [setupComplete, setSetupComplete] = useState(false);
  const [setup, setSetup] = useState<{
    bootstrapRequired: boolean;
    bootstrapEnabled: boolean;
  }>();
  useEffect(() => {
    let active = true;
    fetch(`${api}/api/setup/status`)
      .then(async (response) => {
        if (!response.ok) throw new Error();
        return response.json();
      })
      .then((status) => {
        if (active) setSetup(status);
      })
      .catch(() => {
        if (active)
          setSetup({ bootstrapRequired: false, bootstrapEnabled: false });
      });
    return () => {
      active = false;
    };
  }, [api]);
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    const f = new FormData(e.currentTarget);
    try {
      const response = await fetch(`${api}/api/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          userName: f.get("userName"),
          password: f.get("password"),
          mfaCode: f.get("mfaCode") || undefined,
        }),
      });
      if (!response.ok) {
        let message = "The username or password was not accepted.";
        try {
          const body = await response.json();
          if (body?.error) message = body.error;
        } catch {
          /* Preserve the safe fallback when the server returns no JSON. */
        }
        setError(message);
        return;
      }
      onLogin(await response.json());
    } catch {
      setError(
        "AeroLink could not reach its local API. Run START_AEROLINK.bat, then try again.",
      );
    } finally {
      setBusy(false);
    }
  };
  const bootstrap = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    const f = new FormData(e.currentTarget),
      password = String(f.get("password") ?? "");
    if (password !== f.get("confirmation")) {
      setError("The administrator passwords do not match.");
      setBusy(false);
      return;
    }
    try {
      const response = await fetch(`${api}/api/setup/bootstrap`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-AeroLink-Bootstrap-Secret": String(f.get("secret") ?? ""),
        },
        body: JSON.stringify({
          displayName: f.get("displayName"),
          email: f.get("email"),
          password,
        }),
      });
      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        setError(body.error || "The administrator could not be created.");
        return;
      }
      setSetup({ bootstrapRequired: false, bootstrapEnabled: false });
      setSetupComplete(true);
    } catch {
      setError(
        "AeroLink could not reach its setup service. Verify the API and try again.",
      );
    } finally {
      setBusy(false);
    }
  };
  const workspaceOrigin = window.location.origin;
  let apiOrigin = "";
  try {
    apiOrigin = new URL(api, workspaceOrigin).origin;
  } catch {
    apiOrigin = "";
  }
  const showApiOrigin = apiOrigin !== "" && apiOrigin !== workspaceOrigin;
  const signIn = (
    <form onSubmit={submit}>
      <div className="securityMark">◈</div>
      <h2>{setupComplete ? "Administrator created" : "Welcome back"}</h2>
      <p>
        {setupComplete
          ? "First-install setup is closed. Sign in with the new admin account."
          : "Sign in to your on-premises AeroLink workspace."}
      </p>
      {setupComplete && (
        <div className="setupSuccess" role="status">
          ✓ The one-time administrator bootstrap completed successfully.
        </div>
      )}
      <label>
        Username
        <input name="userName" autoComplete="username" required autoFocus />
      </label>
      <label>
        Password
        <input
          name="password"
          type="password"
          autoComplete="current-password"
          required
        />
      </label>
      {error && <div className="loginError">{error}</div>}
      <button disabled={busy}>
        {busy ? "Authenticating…" : "Sign in securely →"}
      </button>
    </form>
  );
  const setupPanel = setup?.bootstrapRequired ? (
    setup.bootstrapEnabled ? (
      <form className="bootstrapForm" onSubmit={bootstrap}>
        <div className="securityMark">◇</div>
        <p className="eyebrow">ONE-TIME SECURE ACTIVATION</p>
        <h2>Create the global administrator</h2>
        <p>
          This empty deployment accepts exactly one bootstrap. After it
          succeeds, this setup path closes permanently.
        </p>
        <label>
          Bootstrap secret
          <input
            name="secret"
            type="password"
            autoComplete="off"
            required
            autoFocus
          />
        </label>
        <label>
          Administrator username
          <input value="admin" readOnly aria-readonly="true" />
        </label>
        <label>
          Display name
          <input name="displayName" autoComplete="name" required />
        </label>
        <label>
          Email
          <input name="email" type="email" autoComplete="email" required />
        </label>
        <label>
          Password
          <input
            name="password"
            type="password"
            autoComplete="new-password"
            minLength={14}
            required
          />
          <small>
            At least 14 characters with uppercase, lowercase, a number, and a
            symbol.
          </small>
        </label>
        <label>
          Confirm password
          <input
            name="confirmation"
            type="password"
            autoComplete="new-password"
            minLength={14}
            required
          />
        </label>
        {error && <div className="loginError">{error}</div>}
        <button disabled={busy}>
          {busy ? "Creating administrator…" : "Activate controlled workspace →"}
        </button>
        <aside>
          <b>One-way setup boundary</b>
          <span>
            The secret is checked in constant time and never stored in the
            database.
          </span>
          <small>
            Remove the bootstrap secret from service configuration immediately
            after activation.
          </small>
        </aside>
      </form>
    ) : (
      <div className="setupBlocked">
        <div className="securityMark">◇</div>
        <p className="eyebrow">ACTIVATION REQUIRED</p>
        <h2>Protected setup is not enabled</h2>
        <p>
          This deployment has no accounts. An operator must set{" "}
          <code>Identity__BootstrapSecret</code> to a protected random value of
          at least 32 characters, restart the API, then refresh this page.
        </p>
        <aside>
          <b>Fail-closed by design</b>
          <span>
            No default administrator or known production password was created.
          </span>
          <small>
            See the Operations guide for the approved first-install sequence.
          </small>
        </aside>
      </div>
    )
  ) : (
    signIn
  );
  return (
    <main className="loginPage">
      <section className="loginStory">
        <div className="loginBrand">
          <span aria-hidden="true" className="loginBrandMark">
            <Icon name="brandMark" />
          </span>
          AeroLink
        </div>
        {setup && !setup.bootstrapRequired && (
          <>
            <div className="loginStoryContext">
              <p className="loginStoryEyebrow">
                CONTROLLED ENGINEERING WORKSPACE
              </p>
              <h1>
                Requirements, Verification, Changes, Evidence, Document, and
                more in one connected record
              </h1>
            </div>
            <div className="loginStoryEndpoint">
              <div>
                <span>WORKSPACE ORIGIN</span>
                <b>{workspaceOrigin}</b>
              </div>
              {showApiOrigin && (
                <div>
                  <span>API ORIGIN</span>
                  <b>{apiOrigin}</b>
                </div>
              )}
            </div>
          </>
        )}
      </section>
      <section className="loginPanel">
        {setup ? (
          setupPanel
        ) : (
          <div className="setupChecking" role="status">
            <div className="securityMark">◈</div>
            <h2>Checking workspace security</h2>
            <p>Resolving the controlled sign-in boundary…</p>
          </div>
        )}
      </section>
    </main>
  );
}

export function RequiredPasswordChange({
  api,
  onComplete,
}: {
  api: string;
  onComplete: () => void;
}) {
  const [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (busy) return;
    const form = e.currentTarget,
      f = new FormData(form),
      next = String(f.get("newPassword") || "");
    if (next !== f.get("confirmation")) {
      setError("The new passwords do not match.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/auth/password`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          currentPassword: f.get("currentPassword"),
          newPassword: next,
        }),
      });
      onComplete();
    } catch (error) {
      recordClientOperationFailure("identity.password.change", error);
      setError(
        operationError(error, "The temporary password could not be rotated."),
      );
    } finally {
      setBusy(false);
    }
  };
  return (
    <main className="loginPage">
      <section className="loginStory">
        <div className="loginBrand">
          <span aria-hidden="true" className="loginBrandMark">
            <Icon name="brandMark" />
          </span>
          AeroLink
        </div>
        <div>
          <p className="eyebrow">CONTROLLED IDENTITY · REQUIRED ACTION</p>
          <h1>
            Protect the account
            <br />
            before entering
            <br />
            <em>the workspace.</em>
          </h1>
          <p>
            The administrator-issued password is temporary. Choose a private
            password before any Program information is released.
          </p>
        </div>
        <div className="trustStrip">
          <span>SESSION REVOKED AFTER CHANGE</span>
          <span>AUDIT EVIDENCE RETAINED</span>
        </div>
      </section>
      <section className="loginPanel">
        <form onSubmit={submit}>
          <div className="securityMark">◇</div>
          <h2>Replace temporary password</h2>
          <p>You will sign in again after the password is changed.</p>
          <label>
            Temporary password
            <input
              name="currentPassword"
              type="password"
              autoComplete="current-password"
              required
              autoFocus
            />
          </label>
          <label>
            New password
            <input
              name="newPassword"
              type="password"
              autoComplete="new-password"
              minLength={10}
              required
            />
          </label>
          <label>
            Confirm new password
            <input
              name="confirmation"
              type="password"
              autoComplete="new-password"
              minLength={10}
              required
            />
          </label>
          {error && <div className="loginError">{error}</div>}
          <button disabled={busy}>
            {busy ? "Rotating password…" : "Change password securely →"}
          </button>
          <aside>
            <b>Mandatory first-use rotation</b>
            <span>
              Program data remains unavailable until this step succeeds.
            </span>
            <small>
              All sessions are revoked after the change so the new credential
              begins with a clean session boundary.
            </small>
          </aside>
        </form>
      </section>
    </main>
  );
}

type SecurityStatus = {
  mfaEnabled: boolean;
  mfaPending: boolean;
  recoveryCodesRemaining: number;
  activeSessions: number;
};
type AccountSession = {
  id: string;
  ipAddress: string;
  userAgent: string;
  createdAt: string;
  lastSeenAt: string;
  expiresAt: string;
  revokedAt?: string;
  current: boolean;
};
type RoleDelegationView = {
  id: string;
  program: string;
  delegator: string;
  delegateName: string;
  role: string;
  startsAt: string;
  endsAt: string;
  reason: string;
  actor: string;
  status: string;
  canRevoke: boolean;
};
export function AccountSecurityDialog({
  api,
  onClose,
}: {
  api: string;
  onClose: () => void;
}) {
  const [status, setStatus] = useState<SecurityStatus>(),
    [sessions, setSessions] = useState<AccountSession[]>([]),
    [delegations, setDelegations] = useState<RoleDelegationView[]>([]),
    [secret, setSecret] = useState(""),
    [uri, setUri] = useState(""),
    [recoveryCodes, setRecoveryCodes] = useState<string[]>([]),
    [code, setCode] = useState(""),
    [password, setPassword] = useState(""),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const load = useCallback(async () => {
    const [statusResponse, sessionsResponse, delegationsResponse] =
      await Promise.all([
        fetch(`${api}/api/auth/security`),
        fetch(`${api}/api/auth/sessions`),
        fetch(`${api}/api/delegations`),
      ]);
    if (!statusResponse.ok || !sessionsResponse.ok || !delegationsResponse.ok)
      throw new Error("Account security status is unavailable.");
    setStatus(await statusResponse.json());
    setSessions(await sessionsResponse.json());
    setDelegations(await delegationsResponse.json());
  }, [api]);
  useEffect(() => {
    load().catch((error) => setError(error.message));
  }, [load]);
  const enroll = async () => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      const body = await apiRequest<{ secret: string; otpauthUri: string }>(
        `${api}/api/auth/mfa/enroll`,
        { method: "POST" },
      );
      setSecret(body.secret);
      setUri(body.otpauthUri);
      setStatus((current) =>
        current ? { ...current, mfaPending: true } : current,
      );
    } catch (error) {
      recordClientOperationFailure("identity.mfa.enroll", error);
      setError(operationError(error, "Enrollment could not begin."));
    } finally {
      setBusy(false);
    }
  };
  const confirm = async () => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      const body = await apiRequest<{ recoveryCodes: string[] }>(
        `${api}/api/auth/mfa/confirm`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ code }),
        },
      );
      setRecoveryCodes(body.recoveryCodes || []);
      setSecret("");
      setUri("");
      setCode("");
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.mfa.confirm", error);
      setError(
        operationError(error, "The authenticator code was not accepted."),
      );
    } finally {
      setBusy(false);
    }
  };
  const disable = async () => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/auth/mfa/disable`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ password, code }),
      });
      setPassword("");
      setCode("");
      setRecoveryCodes([]);
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.mfa.disable", error);
      setError(operationError(error, "MFA could not be disabled."));
    } finally {
      setBusy(false);
    }
  };
  const revokeOtherSessions = async () => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/auth/sessions/revoke-others`, {
        method: "POST",
      });
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.sessions.revoke", error);
      setError(operationError(error, "Other sessions were not revoked."));
    } finally {
      setBusy(false);
    }
  };
  const revokeDelegation = async (id: string) => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/delegations/${id}`, { method: "DELETE" });
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.delegation.revoke", error);
      setError(operationError(error, "The delegation was not revoked."));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div
      className="identityModal securityDialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="account-security-title"
    >
      <div>
        <button
          className="securityClose"
          onClick={onClose}
          aria-label="Close account security"
        >
          ×
        </button>
        <p className="eyebrow">IDENTITY PROTECTION</p>
        <h2 id="account-security-title">Account security</h2>
        <p>
          Protect this controlled engineering identity with a
          standards-compatible authenticator and one-time recovery codes.
        </p>
        {error && (
          <div className="loginError" role="alert">
            {error}
          </div>
        )}
        {!status && !error && (
          <div className="securityLoading">Loading security posture…</div>
        )}
        {status && (
          <section className="securityStatus">
            <article>
              <span>Authenticator</span>
              <b className={status.mfaEnabled ? "secure" : "attention"}>
                {status.mfaEnabled ? "Enabled" : "Not enabled"}
              </b>
            </article>
            <article>
              <span>Recovery codes</span>
              <b>{status.recoveryCodesRemaining}</b>
            </article>
            <article>
              <span>Active sessions</span>
              <b>{status.activeSessions}</b>
            </article>
          </section>
        )}
        {status && (
          <section className="securityEnrollment">
            <h3>Sessions</h3>
            <p>Review this account's session history and end every other active session.</p>
            {sessions.map((session) => (
              <article key={session.id}>
                <b>{session.current ? "Current session" : session.revokedAt ? "Revoked session" : "Other session"}</b>
                <span>{session.ipAddress} · {session.userAgent || "Unknown client"}</span>
                <small>Last used {new Date(session.lastSeenAt).toLocaleString()}</small>
              </article>
            ))}
            <button
              className="securityPrimary"
              disabled={busy || !sessions.some((x) => !x.current && !x.revokedAt && new Date(x.expiresAt) > new Date())}
              onClick={revokeOtherSessions}
            >
              Revoke other active sessions
            </button>
          </section>
        )}
        {status && (
          <section className="securityEnrollment">
            <h3>Delegated authority</h3>
            <p>Delegations you granted or received remain visible after expiry or revocation.</p>
            {!delegations.length && <span>No delegation history.</span>}
            {delegations.map((delegation) => (
              <article key={delegation.id}>
                <b>{delegation.program} · {programRoleLabel(delegation.role)} · {delegation.status}</b>
                <span>{delegation.delegator} → {delegation.delegateName}</span>
                <small>
                  {new Date(delegation.startsAt).toLocaleString()} – {new Date(delegation.endsAt).toLocaleString()} · {delegation.reason} · created by {delegation.actor}
                </small>
                {delegation.canRevoke && (
                  <button disabled={busy} onClick={() => revokeDelegation(delegation.id)}>
                    Revoke delegation
                  </button>
                )}
              </article>
            ))}
          </section>
        )}
        {status && !status.mfaEnabled && !secret && (
          <>
            <div className="securityCallout">
              <b>Add a second factor</b>
              <span>
                A password alone should not authorize lifecycle decisions or
                controlled evidence.
              </span>
            </div>
            <button
              className="securityPrimary"
              disabled={busy}
              onClick={enroll}
            >
              {busy ? "Preparing enrollment…" : "Set up authenticator →"}
            </button>
          </>
        )}
        {secret && (
          <section className="securityEnrollment">
            <h3>Connect your authenticator</h3>
            <ol>
              <li>Open your authenticator app and add a setup key.</li>
              <li>
                Use the account key below, or open the setup URI on a compatible
                device.
              </li>
              <li>Enter the current six-digit code to prove enrollment.</li>
            </ol>
            <label>
              Setup key<code>{secret}</code>
            </label>
            <a href={uri}>Open authenticator setup URI</a>
            <label>
              Current authenticator code
              <input
                value={code}
                onChange={(event) => setCode(event.target.value)}
                inputMode="numeric"
                autoComplete="one-time-code"
              />
            </label>
            <button
              className="securityPrimary"
              disabled={busy || code.length < 6}
              onClick={confirm}
            >
              {busy ? "Verifying…" : "Verify and enable →"}
            </button>
          </section>
        )}
        {recoveryCodes.length > 0 && (
          <section className="recoveryEvidence">
            <h3>Save these recovery codes now</h3>
            <p>Each code works once. AeroLink will not reveal them again.</p>
            <div>
              {recoveryCodes.map((item) => (
                <code key={item}>{item}</code>
              ))}
            </div>
          </section>
        )}
        {status?.mfaEnabled && recoveryCodes.length === 0 && (
          <section className="securityEnrollment">
            <h3>Disable authenticator</h3>
            <p>
              This downgrade requires both your password and a current
              authenticator or unused recovery code.
            </p>
            <label>
              Password
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                autoComplete="current-password"
              />
            </label>
            <label>
              Authenticator or recovery code
              <input
                value={code}
                onChange={(event) => setCode(event.target.value)}
                autoComplete="one-time-code"
              />
            </label>
            <button
              className="securityDanger"
              disabled={busy || !password || !code}
              onClick={disable}
            >
              {busy ? "Confirming…" : "Disable MFA"}
            </button>
          </section>
        )}
      </div>
    </div>
  );
}

type WorkTask = {
  id: string;
  type: string;
  artifact: string;
  title: string;
  priority: string;
  dueAt: string;
  ageDays: number;
  route: string;
  discipline: string;
};
export function MyWorkCenter({
  api,
  projectId,
  releaseId,
  user,
  onBack,
  onOpenScr,
  onOpenRelease,
  onOpenVerification,
  onOpenManagedDocument,
}: {
  api: string;
  projectId: string;
  releaseId: string;
  user: AuthUser;
  onBack: () => void;
  onOpenScr: (id: string, discipline: "system" | "software") => void;
  onOpenRelease: () => void;
  onOpenVerification: (discipline: string) => void;
  onOpenManagedDocument: (id: string) => void;
}) {
  const [data, setData] = useState<{
    generatedAt: string;
    summary: {
      total: number;
      approvals: number;
      overdue: number;
      drafts: number;
    };
    tasks: WorkTask[];
  }>();
  const [loadError, setLoadError] = useState("");
  // A failed queue load must degrade to a message on this page, never take the workspace down with it.
  useEffect(() => {
    let live = true;
    setLoadError("");
    fetch(`${api}/api/my-work?projectId=${projectId}&releaseId=${releaseId}`)
      .then(async (response) => {
        if (!response.ok)
          throw new Error("The work queue is unavailable right now.");
        return response.json();
      })
      .then((body) => {
        if (live) setData(body);
      })
      .catch(() => {
        if (live)
          setLoadError(
            "Your work queue could not be loaded. Refresh to try again.",
          );
      });
    return () => {
      live = false;
    };
  }, [api, projectId, releaseId]);
  const openTask = (task: WorkTask) =>
    task.route === "scr"
      ? onOpenScr(
          task.id,
          task.discipline === "software" ? "software" : "system",
        )
      : task.route === "testingCoverage"
        ? onOpenVerification(task.discipline)
        : task.route === "managedDocuments"
          ? onOpenManagedDocument(task.id)
        : onOpenRelease();
  return (
    <main className="identityPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">
            PERSONAL WORKSPACE / {user.userName.toUpperCase()}
          </p>
          <h1>My Work</h1>
          <p>
            One accountable queue for reviews, signatures, drafts, and release
            decisions.
          </p>
          {loadError && <div className="workspaceError">{loadError}</div>}
        </div>
        <div className="identityBadge">
          <span>
            {user.displayName
              .split(" ")
              .map((x) => x[0])
              .join("")
              .slice(0, 2)}
          </span>
          <div>
            <b>{user.displayName}</b>
            <small>Authenticated · secure session</small>
          </div>
        </div>
      </header>
      {data && (
        <>
          {/* The scope is a fact about all four metrics at once, so it is stated once (#925 P3) — as a
              leading cell of the same row, so stating it costs no extra vertical space and the work
              queue below moves up by the full card compaction. The overdue card keeps its own urgent
              note because it says something the scope line does not. */}
          <section className="workMetrics" aria-label="My Work metrics — current program scope">
            <div className="workMetricsGrid">
              <p className="workMetricsScope">Current program scope</p>
              {[
                ["Assigned to me", data.summary.total],
                ["Awaiting signature", data.summary.approvals],
                ["Overdue", data.summary.overdue],
                ["Drafts I own", data.summary.drafts],
              ].map(([x, n], i) => (
                <article
                  className={i === 2 && Number(n) > 0 ? "urgent" : ""}
                  key={String(x)}
                >
                  <span>{x}</span>
                  <b>{n}</b>
                  {i === 2 && <small>Requires immediate attention</small>}
                </article>
              ))}
            </div>
          </section>
          <section className="workQueue">
            <div className="queueTitle">
              <div>
                <h2>Priority queue</h2>
                <p>Ordered by formal authority and due date</p>
              </div>
              <span>
                LIVE · {new Date(data.generatedAt).toLocaleTimeString()}
              </span>
            </div>
            {data.tasks.length ? (
              data.tasks.map((task) => (
                <article
                  key={`${task.type}-${task.id}`}
                  onClick={() => openTask(task)}
                >
                  <i className={task.priority.toLowerCase()}>{task.priority}</i>
                  <div>
                    <span>{task.type}</span>
                    <b>
                      {task.artifact} · {task.title}
                    </b>
                    <small>
                      Assigned {task.ageDays} day{task.ageDays === 1 ? "" : "s"}{" "}
                      ago · due {new Date(task.dueAt).toLocaleDateString()}
                    </small>
                  </div>
                  <button>Open work item →</button>
                </article>
              ))
            ) : (
              <div className="workZero">
                <span>✓</span>
                <h3>You are caught up</h3>
                <p>
                  No controlled work is awaiting your action in this program.
                </p>
              </div>
            )}
          </section>
        </>
      )}
    </main>
  );
}

type AdminUser = {
  id: string;
  userName: string;
  displayName: string;
  email: string;
  state: string;
  lastLoginAt?: string;
  isGlobalAdministrator: boolean;
  memberships: { programId: string; role: string }[];
};
export function AdministrationCenter({
  api,
  programId,
  onBack,
}: {
  api: string;
  programId: string;
  onBack: () => void;
}) {
  const [users, setUsers] = useState<AdminUser[]>([]),
    [selected, setSelected] = useState<AdminUser>(),
    [error, setError] = useState(""),
    [query, setQuery] = useState(""),
    [page, setPage] = useState(1),
    [busy, setBusy] = useState(false);
  const load = useCallback(
    () =>
      fetch(`${api}/api/admin/users`)
        .then(async (x) => {
          if (!x.ok) throw new Error("Administrator access required.");
          const loaded = (await x.json()) as AdminUser[];
          setUsers(loaded);
          setSelected((current) =>
            current?.id ? loaded.find((user) => user.id === current.id) : current,
          );
        })
        .catch((x) => setError(x.message)),
    [api],
  );
  useEffect(() => {
    load();
  }, [load]);
  useEffect(() => setPage(1), [query]);
  const setState = async (user: AdminUser) => {
    if (busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/admin/users/${user.id}/state`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: user.state !== "Active" }),
      });
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.account.state", error);
      setError(operationError(error, "The account state was not changed."));
    } finally {
      setBusy(false);
    }
  };
  const grant = async (role: string) => {
    if (!selected || busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/admin/users/${selected.id}/memberships`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ programId, role }),
      });
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.role.grant", error);
      setError(operationError(error, "The role was not granted."));
    } finally {
      setBusy(false);
    }
  };
  const revoke = async (role: string) => {
    if (!selected || busy) return;
    setBusy(true);
    setError("");
    try {
      await apiRequest(
        `${api}/api/admin/users/${selected.id}/memberships/${programId}/${role}`,
        { method: "DELETE" },
      );
      await load();
    } catch (error) {
      recordClientOperationFailure("identity.role.revoke", error);
      setError(operationError(error, "The role was not revoked."));
    } finally {
      setBusy(false);
    }
  };
  const normalized = query.trim().toLowerCase(),
    filtered = users.filter(
      (user) =>
        !normalized ||
        `${user.displayName} ${user.userName} ${user.email} ${stateLabel(user.state)} ${user.memberships.map((item) => item.role).join(" ")}`
          .toLowerCase()
          .includes(normalized),
    ),
    pageSize = 25,
    totalPages = Math.max(1, Math.ceil(filtered.length / pageSize)),
    safePage = Math.min(page, totalPages),
    visible = filtered.slice((safePage - 1) * pageSize, safePage * pageSize);
  return (
    <main className="identityPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">ENTERPRISE CONTROL / IDENTITY & ACCESS</p>
          <h1>People & Authority</h1>
          <p>
            Manage accounts, program roles, access state, and accountable
            authority.
          </p>
        </div>
        <button
          className="primaryAction"
          disabled={busy}
          onClick={() =>
            setSelected({
              id: "",
              userName: "",
              displayName: "",
              email: "",
              state: "New",
              isGlobalAdministrator: false,
              memberships: [],
            })
          }
        >
          + Create account
        </button>
      </header>
      {error && (
        <div className="loginError" role="alert" aria-live="assertive">
          {error}
        </div>
      )}
      <section className="adminSummary">
        <article>
          <b>{users.filter((x) => x.state === "Active").length}</b>
          <span>Active accounts</span>
        </article>
        <article>
          <b>
            {
              users.filter((x) =>
                x.memberships.some((m) => m.role === "Approver"),
              ).length
            }
          </b>
          <span>Authorized approvers</span>
        </article>
        <article>
          <b>{users.filter((x) => x.state !== "Active").length}</b>
          <span>Disabled or locked</span>
        </article>
        <article>
          <b>{users.reduce((n, x) => n + x.memberships.length, 0)}</b>
          <span>Role assignments</span>
        </article>
      </section>
      <section className="directoryToolbar">
        <label>
          <span>Find a person, account, role, or state</span>
          <input
            aria-label="Search people and authority"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search people and authority…"
          />
        </label>
        <div>
          <b>{filtered.length.toLocaleString()}</b>
          <span>of {users.length.toLocaleString()} accounts</span>
        </div>
      </section>
      <section className="userTable">
        <div className="userHead">
          <span>PERSON</span>
          <span>PROGRAM AUTHORITY</span>
          <span>LAST ACCESS</span>
          <span>STATE</span>
          <span />
        </div>
        {visible.map((user) => (
          <article key={user.id}>
            <div className="person">
              <PersonAvatar
                userName={user.userName}
                displayName={user.displayName}
                size="small"
              />
              <div>
                <b>{user.displayName}</b>
                {user.isGlobalAdministrator && <small>Global system administrator</small>}
                <small>
                  {user.userName} · {user.email}
                </small>
              </div>
            </div>
            <div className="roleCloud">
              {user.memberships
                .filter((x) => x.programId === programId)
                .map((x) => (
                  <span key={x.role}>{programRoleLabel(x.role)}</span>
                ))}
            </div>
            <time>
              {user.lastLoginAt
                ? new Date(user.lastLoginAt).toLocaleString()
                : "Never"}
            </time>
            <strong className={user.state.toLowerCase()}>
              {stateLabel(user.state)}
            </strong>
            <div>
              <button disabled={busy} onClick={() => setSelected(user)}>
                Manage roles
              </button>
              <button
                className="quiet"
                disabled={busy}
                onClick={() => setState(user)}
              >
                {user.state === "Active" ? "Disable" : "Enable"}
              </button>
            </div>
          </article>
        ))}
        {!visible.length && (
          <div className="directoryEmpty">
            <b>No matching accounts</b>
            <span>Try a name, username, email, role, or access state.</span>
          </div>
        )}
        {filtered.length > pageSize && (
          <div className="directoryPager">
            <button
              disabled={safePage === 1}
              onClick={() => setPage((value) => Math.max(1, value - 1))}
            >
              ← Previous
            </button>
            <span>
              Page {safePage} of {totalPages}
            </span>
            <button
              disabled={safePage === totalPages}
              onClick={() =>
                setPage((value) => Math.min(totalPages, value + 1))
              }
            >
              Next →
            </button>
          </div>
        )}
      </section>
      {selected && (
        <div className="identityModal">
          <div>
            {selected.id ? (
              <>
                <p className="eyebrow">PROGRAM AUTHORITY</p>
                <h2>{selected.displayName}</h2>
                <p>
                  Program roles apply only inside this Program. The global system administrator is a separate break-glass identity.
                </p>
                <div className="rolePicker">
                  {grantableProgramRoles.map((role) => {
                    const held = selected.memberships.some(
                      (membership) => membership.programId === programId && membership.role === role,
                    );
                    return held ? (
                      <button disabled={busy} onClick={() => revoke(role)} key={role}>
                        {programRoleLabel(role)} · Current · Revoke
                      </button>
                    ) : (
                      <button disabled={busy} onClick={() => grant(role)} key={role}>
                        Grant {programRoleLabel(role)}
                      </button>
                    );
                  })}
                </div>
              </>
            ) : (
              <CreateAccount
                api={api}
                onDone={() => {
                  setSelected(undefined);
                  load();
                }}
              />
            )}
            <button
              className="cancel"
              disabled={busy}
              onClick={() => setSelected(undefined)}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
    </main>
  );
}
function CreateAccount({ api, onDone }: { api: string; onDone: () => void }) {
  const [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (busy) return;
    const f = new FormData(e.currentTarget);
    setBusy(true);
    setError("");
    try {
      await apiRequest(`${api}/api/admin/users`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(Object.fromEntries(f)),
      });
      onDone();
    } catch (error) {
      recordClientOperationFailure("identity.account.create", error);
      setError(operationError(error, "The secure account was not created."));
    } finally {
      setBusy(false);
    }
  };
  return (
    <form className="accountForm" onSubmit={submit}>
      <p className="eyebrow">NEW CONTROLLED IDENTITY</p>
      <h2>Create account</h2>
      <label>
        Username
        <input name="userName" required />
      </label>
      <label>
        Display name
        <input name="displayName" required />
      </label>
      <label>
        Email
        <input name="email" type="email" required />
      </label>
      <label>
        Temporary password
        <input
          name="temporaryPassword"
          type="password"
          minLength={10}
          required
        />
      </label>
      {error && (
        <div className="loginError" role="alert" aria-live="assertive">
          {error}
        </div>
      )}
      <button disabled={busy}>
        {busy ? "Creating secure account…" : "Create secure account"}
      </button>
    </form>
  );
}

export function SignatureDialog({
  title,
  meaning,
  onCancel,
  onSign,
}: {
  title: string;
  meaning: string;
  onCancel: () => void;
  onSign: (password: string, meaning: string) => Promise<void>;
}) {
  const [password, setPassword] = useState(""),
    [busy, setBusy] = useState(false);
  return (
    <div className="identityModal signatureModal">
      <div>
        <div className="signatureSeal">✓</div>
        <p className="eyebrow">ELECTRONIC SIGNATURE</p>
        <h2>{title}</h2>
        <p>
          By signing, you confirm that you reviewed the exact controlled
          snapshot and accept the stated approval meaning. Your identity, time,
          content hash, and decision will be permanently recorded.
        </p>
        <label>
          Signature meaning
          <textarea value={meaning} readOnly />
        </label>
        <label>
          Re-enter your password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoFocus
          />
        </label>
        <div className="signatureActions">
          <button className="cancel" disabled={busy} onClick={onCancel}>
            Cancel
          </button>
          <button
            disabled={password.length < 1 || busy}
            onClick={async () => {
              if (busy) return;
              setBusy(true);
              try {
                await onSign(password, meaning);
              } finally {
                setBusy(false);
              }
            }}
          >
            {busy ? "Signing…" : "Sign & approve"}
          </button>
        </div>
      </div>
    </div>
  );
}
