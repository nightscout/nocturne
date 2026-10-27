import { getShellSession } from "#lib/api/shell-session.remote.js";
import { fresh } from "#lib/api/fresh-query.js";

export type ShellSession = Awaited<ReturnType<typeof getShellSession>>;

/** The part of the shell's page data a {@link ShellSession} is compared against. */
export interface ShellSessionData {
  user?: { subjectId?: string } | null;
  effectivePermissions?: string[];
  limitTo24Hours?: boolean;
  isGuestSession?: boolean;
  guestExpiresAt?: string | null;
  isShareHost?: boolean;
}

export function shellSessionChanged(probe: ShellSession, data: ShellSessionData): boolean {
  const scopes = new Set(data.effectivePermissions ?? []);
  return (
    (data.user?.subjectId ?? null) !== probe.subjectId ||
    (data.limitTo24Hours ?? false) !== probe.limitTo24Hours ||
    (data.isGuestSession ?? false) !== probe.isGuestSession ||
    (data.guestExpiresAt ?? null) !== probe.guestExpiresAt ||
    (data.isShareHost === true && !probe.shareLinkLive) ||
    scopes.size !== new Set(probe.scopes).size ||
    probe.scopes.some((scope) => !scopes.has(scope))
  );
}

/**
 * Reads the session and calls `onChanged` only when it no longer matches `current()`. A probe
 * that fails (offline, 5xx) is dropped: the next trigger asks again, and nothing else reacts.
 * Overlapping calls share the one probe in flight.
 */
export function createShellRevalidator(
  current: () => ShellSessionData,
  onChanged: () => void
): () => Promise<void> {
  let inFlight: Promise<void> | null = null;

  async function probe() {
    if (typeof navigator !== "undefined" && navigator.onLine === false) return;
    try {
      const session = await fresh(getShellSession());
      if (shellSessionChanged(session, current())) onChanged();
    } catch {
      return;
    }
  }

  return () => (inFlight ??= probe().finally(() => (inFlight = null)));
}
