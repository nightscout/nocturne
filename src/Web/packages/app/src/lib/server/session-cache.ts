import { createHash } from "node:crypto";
import type { SessionInfo } from "#lib/api/generated/nocturne-api-client.js";

/**
 * A short memo of the API's answer to "who is this caller", keyed by the credentials presented.
 *
 * A page issues its remote queries in parallel and the auth hook asks the API for the session on
 * every one of them, so without this each query waits on its own session round-trip before it
 * starts. Concurrent checks for the same credentials share one call, and an answer is reused for
 * {@link SESSION_TTL_MS}.
 *
 * Only an authenticated answer whose response set no cookie is kept: a response that rotated the
 * tokens is tied to the request that carried it, and the rotated cookies key a fresh entry anyway.
 * The API authenticates every data call itself, so a reused answer can only delay what SSR shows
 * (the signed-in shell, the granted scopes) by up to the TTL after a role or membership change;
 * it never widens what the API returns.
 *
 * The answer also carries the caller's own preferences, which the browser writes back over its
 * local copy, so a write must not leave a stale one behind: a request that may write asks with
 * `fresh` and calls {@link forgetSession} once it has finished, and an answer whose lookup was
 * already running when a write finished is not kept.
 */
const SESSION_TTL_MS = 15_000;
const MAX_ENTRIES = 5_000;

export interface SessionLoad {
  session: SessionInfo;
  /** False when the response carried a Set-Cookie, i.e. the API rotated the caller's tokens. */
  cacheable: boolean;
}

interface Entry {
  session: SessionInfo;
  expiresAt: number;
}

const answers = new Map<string, Entry>();
const inflight = new Map<string, Promise<SessionLoad>>();
/** Bumped by every {@link forgetSession}; an answer read under an older value is not kept. */
let generation = 0;

/** Hashes the credentials and request facts the API's session answer depends on. */
export function sessionCacheKey(parts: (string | undefined | null)[]): string {
  return createHash("sha256").update(parts.map((p) => p ?? "").join("\u0000")).digest("base64url");
}

export async function getCachedSession(
  key: string,
  load: () => Promise<SessionLoad>,
  { fresh = false, now = Date.now }: { fresh?: boolean; now?: () => number } = {}
): Promise<SessionInfo> {
  if (fresh) return (await load()).session;

  const hit = answers.get(key);
  if (hit && hit.expiresAt > now()) return structuredClone(hit.session);
  if (hit) answers.delete(key);

  const pending = inflight.get(key);
  if (pending) {
    // A shared answer that rotated tokens belongs to the request that triggered the rotation;
    // this one asks on its own, as it would have without the cache.
    const shared = await pending.catch(() => null);
    if (shared?.cacheable) return structuredClone(shared.session);
    return (await load()).session;
  }

  const startedAt = generation;
  const started = load();
  inflight.set(key, started);
  try {
    const result = await started;
    if (result.cacheable && result.session.isAuthenticated && generation === startedAt)
      remember(key, result.session, now());
    return result.session;
  } finally {
    inflight.delete(key);
  }
}

function remember(key: string, session: SessionInfo, at: number) {
  let expiresAt = at + SESSION_TTL_MS;
  // Never past the access token's own expiry: from then on the API has a rotation to perform.
  const tokenExpiry = session.expiresAt ? new Date(session.expiresAt).getTime() : NaN;
  if (Number.isFinite(tokenExpiry)) expiresAt = Math.min(expiresAt, tokenExpiry);
  if (expiresAt <= at) return;

  if (answers.size >= MAX_ENTRIES) {
    for (const [k, e] of answers) if (e.expiresAt <= at) answers.delete(k);
    // Map iteration is insertion order, so this drops the oldest entries first.
    for (const k of answers.keys()) {
      if (answers.size < MAX_ENTRIES) break;
      answers.delete(k);
    }
  }
  answers.set(key, { session: structuredClone(session), expiresAt });
}

/** Drops the answer for these credentials, and any lookup in flight, after a request that may have changed it. */
export function forgetSession(key: string) {
  answers.delete(key);
  generation++;
}

/** Test seam: forget every answer. */
export function clearSessionCache() {
  answers.clear();
  inflight.clear();
}
