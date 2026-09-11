import { browser } from "$app/environment";

/** One rejected API call, as the support form reports it. */
export interface ApiFailure {
  /** ISO 8601, UTC. */
  at: string;
  /** HTTP status, when the rejection carried one. */
  status?: number;
  /** Route the user was on, not the endpoint — the transport does not carry one. */
  route: string;
  /** The sentence the user was shown. */
  message: string;
}

/**
 * Most recent failures kept. A support report wants the run-up to the problem,
 * not a session's history, and the whole buffer is pasted into an issue body.
 */
const CAPACITY = 20;

/** Cap on a recorded sentence, so one long server message cannot dominate the buffer. */
const MAX_MESSAGE_LENGTH = 300;

/**
 * Window within which an identical failure is treated as the same one. A rejected
 * query is described again on every re-render of the surface showing it, so without
 * this the buffer fills with one repeated line.
 */
const DEDUPE_WINDOW_MS = 2000;

/**
 * Deliberately a plain array rather than `$state`: nothing renders from it, and a
 * reactive buffer written to during render — which is when a rejected query is
 * described — would invalidate whatever read it.
 */
let failures: ApiFailure[] = [];

/**
 * Records a rejected API call for the support form.
 *
 * No-op on the server. The buffer is module state, so on the server it would be
 * shared across every request the process handles and would leak one tenant's
 * failures into another tenant's support report.
 */
export function recordApiFailure(
  status: number | undefined,
  message: string
): void {
  if (!browser) return;

  const route = window.location.pathname;
  const trimmed =
    message.length > MAX_MESSAGE_LENGTH
      ? `${message.slice(0, MAX_MESSAGE_LENGTH)}…`
      : message;
  const now = Date.now();

  const previous = failures[failures.length - 1];
  if (
    previous &&
    previous.status === status &&
    previous.route === route &&
    previous.message === trimmed &&
    now - Date.parse(previous.at) < DEDUPE_WINDOW_MS
  ) {
    return;
  }

  failures.push({ at: new Date(now).toISOString(), status, route, message: trimmed });
  if (failures.length > CAPACITY) failures = failures.slice(-CAPACITY);
}

/** The recorded failures, oldest first. */
export function readApiFailures(): readonly ApiFailure[] {
  return failures;
}

/** Drops everything recorded so far. */
export function clearApiFailures(): void {
  failures = [];
}
