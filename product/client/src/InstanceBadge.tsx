import { useEffect, useState } from "react";
import { API_ORIGIN } from "./apiOrigin";
import "./InstanceBadge.css";

/**
 * Which AeroLink am I looking at?
 *
 * The mistake this exists to prevent is a specific one, and an easy one: adding a Change Request on
 * 127.0.0.1 at work and assuming it is therefore in the HOME database. Two installations, identical to the
 * pixel, holding different controlled records.
 *
 * So the badge is persistent and quiet. The label is the whole signal at a glance; the source revision,
 * database name and snapshot age live in the tooltip, where an operator can find them when they are looking
 * for them and nobody has to read them the rest of the time. Deployment diagnostics do not belong in a
 * product surface, but "which installation is this" is not a diagnostic — it is the context every record on
 * the screen belongs to.
 *
 * Canonical status is never inferred here. The API reports what the installation declared, and an
 * installation that declared nothing gets a modest label rather than a flattering one.
 */

type InstanceIdentity = {
  sourceShortSha?: string;
  mode?: string;
  instance?: {
    label?: string;
    classification?: string;
    snapshot?: {
      sourceLabel?: string | null;
      sourceSha?: string | null;
      createdAtUtc?: string | null;
      activatedAtUtc?: string | null;
    } | null;
  };
  database?: { name?: string | null };
};

const snapshotAge = (createdAtUtc?: string | null) => {
  if (!createdAtUtc) return undefined;
  const created = Date.parse(createdAtUtc);
  if (Number.isNaN(created)) return undefined;
  const days = Math.floor((Date.now() - created) / 86_400_000);
  if (days <= 0) return "taken today";
  return days === 1 ? "1 day old" : `${days} days old`;
};

export default function InstanceBadge() {
  const [identity, setIdentity] = useState<InstanceIdentity | null>(null);

  useEffect(() => {
    let cancelled = false;
    // Anonymous, cheap, and never retried in a loop: an installation that cannot answer this is an
    // installation with larger problems than a missing badge, and every other surface will say so.
    fetch(`${API_ORIGIN}/health/identity`)
      .then((response) => (response.ok ? response.json() as Promise<InstanceIdentity> : null))
      .then((value) => { if (!cancelled) setIdentity(value); })
      .catch(() => { if (!cancelled) setIdentity(null); });
    return () => { cancelled = true; };
  }, []);

  if (!identity) return null;

  const label = identity.instance?.label ?? "AEROLINK";
  const classification = identity.instance?.classification ?? "Undeclared";
  const snapshot = identity.instance?.snapshot ?? null;
  const age = snapshotAge(snapshot?.createdAtUtc);

  // Routine surfaces name the installation, not its deployment classification. The owner asked for one
  // specific thing (#925 P2): the declared HOME CANONICAL label reads as the plain installation name it
  // identifies. That is handled by the single explicit rule below — same declared label, same declared
  // classification, spaced or not — because a general suffix-stripping algorithm guesses at other
  // operators' labels and can erase meaningful distinctions like a Demo declaration. Every label the
  // rule does not name is shown verbatim, and the tooltip keeps the whole declared label,
  // classification, source, database, mode and snapshot facts, so nothing is reclassified, renamed, or
  // inferred — the badge just stops shouting the operator word.
  const plainLabelRules: ReadonlyArray<{ declaredLabel: string; classification: string; plain: string }> = [
    { declaredLabel: "HOME CANONICAL", classification: "HomeCanonical", plain: "HOME" },
  ];
  const normalize = (value: string) => value.replace(/\s+/gu, " ").trim().toUpperCase();
  const matched = plainLabelRules.find(rule =>
    rule.classification === classification.trim() && normalize(rule.declaredLabel) === normalize(label));
  const visibleLabel = matched ? matched.plain : label;

  const detail = [
    `Instance: ${label} (${classification})`,
    identity.database?.name ? `Database: ${identity.database.name}` : undefined,
    identity.sourceShortSha ? `Source: ${identity.sourceShortSha}` : undefined,
    identity.mode ? `Mode: ${identity.mode}` : undefined,
    snapshot ? `Snapshot from ${snapshot.sourceLabel ?? "another installation"}${age ? `, ${age}` : ""}` : undefined,
  ].filter(Boolean).join("\n");

  return (
    <span
      className={`instanceBadge instanceBadge--${classification.toLowerCase()}`}
      title={detail}
      data-testid="instance-badge"
      data-classification={classification}
    >
      {visibleLabel}
      {snapshot ? <em className="instanceBadgeSnapshot">snapshot{age ? ` ${age}` : ""}</em> : null}
    </span>
  );
}
