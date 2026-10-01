import { createHmac, timingSafeEqual } from 'node:crypto';
import { isRecord } from './payload.js';

/**
 * HMAC-signed handshake ticket for the Socket.IO realtime bridge.
 *
 * A browser's Socket.IO handshake reaches the bridge directly, not through the
 * app's BFF proxy, so the bridge cannot refresh the browser's short-lived
 * access token. Replaying the raw cookie would fail once the token turns over,
 * and could trigger a refresh-token rotation the bridge cannot return to the
 * browser.
 *
 * Instead the web app's `/realtime/ticket` endpoint, which runs inside the BFF,
 * asks the API for the connection's realtime admission and, only on success,
 * mints one of these tickets. The browser presents it in the Socket.IO `auth`
 * payload and the bridge verifies it locally with the shared INSTANCE_KEY.
 *
 * Wire format: `base64url(json).hexSig`.
 * Payload: `{ h, exp, tenantRelay, subjectId? }`. `h` is the normalized host
 * the ticket authorizes, `exp` is a unix-ms deadline, and `tenantRelay` is the
 * API's admission for the credential the ticket was minted for. `subjectId` is
 * the subject whose per-subject room the socket may join, present only when the
 * credential belongs to one. Binding to the host (not just the tenant slug) means a ticket
 * minted for one tenant cannot be replayed on a connection that arrives on a
 * different host. The minting endpoint and the handshake both derive the host
 * from X-Forwarded-Host behind the gateway, so they agree for tenant subdomains
 * and the apex single-tenant case alike.
 * Signature: HMAC-SHA256 over the base64url payload using INSTANCE_KEY.
 */

export interface HandshakeTicketPayload {
  /** Normalized host (lowercased, port-stripped) the ticket authorizes. */
  h: string;
  /** Expiration timestamp in unix milliseconds. */
  exp: number;
  /**
   * Whether the socket may join the tenant-wide room, as the API decided it at
   * {@link REALTIME_ADMISSION_PATH}. Inside the signed payload so a client
   * cannot grant itself the room; a ticket without it verifies as false.
   */
  tenantRelay: boolean;
  /**
   * The subject whose per-subject room the socket may join, as the API returned
   * it at {@link REALTIME_ADMISSION_PATH}. Present only when the credential
   * belongs to a subject; a guest link or share has none.
   */
  subjectId?: string;
}

const CANONICAL_SUBJECT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** The value as a subject id in canonical lowercase `D` GUID form, the only form the bridge rooms on, else undefined. */
export function canonicalSubjectId(value: unknown): string | undefined {
  return typeof value === 'string' && CANONICAL_SUBJECT_ID.test(value) ? value : undefined;
}

/**
 * The API endpoint answering whether a credential may read glucose live and
 * whether it may join the tenant-wide room. The ticket endpoint and the legacy
 * `authorize` path both admit a socket on it, so the bridge applies the same
 * rule as the API's SignalR hub.
 */
export const REALTIME_ADMISSION_PATH = '/api/v4/me/realtime-admission';

/** Tickets are short-lived; the client fetches a fresh one on every (re)connect. */
export const HANDSHAKE_TICKET_LIFETIME_MS = 2 * 60 * 1000; // 2 minutes

/** Normalize a host header value to a comparable host: lowercased, port stripped. */
export function normalizeHandshakeHost(host: string): string {
  return host.split(':')[0].trim().toLowerCase();
}

/** Sign a handshake ticket authorizing connections that arrive on `host`. */
export function signHandshakeTicket(
  secret: string,
  host: string,
  tenantRelay: boolean,
  subjectId?: string,
  ttlMs: number = HANDSHAKE_TICKET_LIFETIME_MS,
  now: number = Date.now(),
): string {
  const payload: HandshakeTicketPayload = {
    h: normalizeHandshakeHost(host),
    exp: now + ttlMs,
    tenantRelay,
  };
  if (subjectId) payload.subjectId = subjectId;
  const payloadB64 = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64url');
  const sig = createHmac('sha256', secret).update(payloadB64).digest('hex');
  return `${payloadB64}.${sig}`;
}

/**
 * Verify a handshake ticket. Returns the payload when the signature is valid and
 * the ticket has not expired, otherwise null. Fails closed on a missing secret
 * or token.
 */
export function verifyHandshakeTicket(
  secret: string,
  token: string | undefined,
  now: number = Date.now(),
): HandshakeTicketPayload | null {
  if (!secret || !token) return null;

  const dot = token.indexOf('.');
  if (dot < 0) return null;
  const payloadB64 = token.slice(0, dot);
  const sigHex = token.slice(dot + 1);
  if (!payloadB64 || !sigHex) return null;

  let expectedHex: string;
  try {
    expectedHex = createHmac('sha256', secret).update(payloadB64).digest('hex');
  } catch {
    return null;
  }

  const actual = Buffer.from(sigHex, 'hex');
  const expected = Buffer.from(expectedHex, 'hex');
  if (actual.length !== expected.length || !timingSafeEqual(actual, expected)) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(Buffer.from(payloadB64, 'base64url').toString('utf-8'));
  } catch {
    return null;
  }

  if (!isRecord(parsed)) return null;
  const { h, exp, subjectId } = parsed;
  if (typeof h !== 'string' || typeof exp !== 'number') return null;
  if (exp < now) return null;

  return {
    h,
    exp,
    tenantRelay: parsed.tenantRelay === true,
    subjectId: canonicalSubjectId(subjectId),
  };
}
