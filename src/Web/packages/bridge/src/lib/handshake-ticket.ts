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
 * Payload: `{ h, t, exp, ...RealtimeGrant }`. `h` is the normalized host the
 * ticket authorizes, `t` the id of the tenant the API admitted the caller to,
 * `exp` is a unix-ms deadline, and the rest is the API's admission for the
 * credential the ticket was minted for. The bridge admits a ticket only while
 * `t` is the tenant its slug names, so a ticket for a deleted tenant does not
 * reach the rooms of a tenant created on its slug. Binding to the host
 * (not just the tenant slug) means a ticket minted for one tenant cannot be
 * replayed on a connection that arrives on a different host. The minting endpoint and the handshake both derive the host
 * from X-Forwarded-Host behind the gateway, so they agree for tenant subdomains
 * and the apex single-tenant case alike.
 * Signature: HMAC-SHA256 over the base64url payload using INSTANCE_KEY.
 */

/**
 * What the API's realtime admission at {@link REALTIME_ADMISSION_PATH} lets a
 * socket receive. Carried inside the signed ticket so a client cannot grant
 * itself a room.
 */
export interface RealtimeGrant {
  /** Whether the socket may join the tenant-wide room. */
  tenantRelay: boolean;
  /**
   * The subject whose per-subject room the socket may join. Present only when
   * the credential belongs to a subject; a guest link or share has none.
   */
  subjectId?: string;
  /** The API broadcast categories whose storage events the socket may receive. */
  categories: string[];
  /** Whether the credential may read only the last 24 hours. */
  historyClamped: boolean;
  /** Whether the credential may silence the tenant's alarms from the `/alarm` namespace. */
  mayAck: boolean;
}

export interface HandshakeTicketPayload extends RealtimeGrant {
  /** Normalized host (lowercased, port-stripped) the ticket authorizes. */
  h: string;
  /** The id of the tenant the API admitted the caller to. */
  t: string;
  /** Expiration timestamp in unix milliseconds. */
  exp: number;
}

const CANONICAL_SUBJECT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** The value as a subject id in canonical lowercase `D` GUID form, the only form the bridge rooms on, else undefined. */
export function canonicalSubjectId(value: unknown): string | undefined {
  return typeof value === 'string' && CANONICAL_SUBJECT_ID.test(value) ? value : undefined;
}

/**
 * Reads an admission, from the API or a ticket, failing closed: anything absent
 * or malformed grants no room or ack, and a missing clamp reads as clamped.
 */
export function readRealtimeGrant(value: unknown): RealtimeGrant {
  if (!isRecord(value)) return { tenantRelay: false, categories: [], historyClamped: true, mayAck: false };
  const { categories } = value;
  return {
    tenantRelay: value.tenantRelay === true,
    subjectId: canonicalSubjectId(value.subjectId),
    categories: Array.isArray(categories)
      ? categories.filter((c): c is string => typeof c === 'string')
      : [],
    historyClamped: value.historyClamped !== false,
    mayAck: value.mayAck === true,
  };
}

/**
 * The API endpoint answering whether a credential may read glucose live,
 * whether it may join the tenant-wide room and which category rooms it may
 * join. The ticket endpoint and the legacy `authorize` path both admit a socket
 * on it, so the bridge applies the same rule as the API's SignalR hub.
 */
export const REALTIME_ADMISSION_PATH = '/api/v4/me/realtime-admission';

/**
 * Tickets are short-lived. The client fetches a fresh one on every (re)connect,
 * and the bridge asks an admitted socket for another as its ticket expires.
 */
export const HANDSHAKE_TICKET_LIFETIME_MS = 2 * 60 * 1000; // 2 minutes

/** Normalize a host header value to a comparable host: lowercased, port stripped. */
export function normalizeHandshakeHost(host: string): string {
  return host.split(':')[0].trim().toLowerCase();
}

/** Sign a handshake ticket authorizing connections that arrive on `host` for the tenant `tenantId`. */
export function signHandshakeTicket(
  secret: string,
  host: string,
  tenantId: string,
  grant: RealtimeGrant,
  ttlMs: number = HANDSHAKE_TICKET_LIFETIME_MS,
  now: number = Date.now(),
): string {
  const payload: HandshakeTicketPayload = {
    h: normalizeHandshakeHost(host),
    t: tenantId,
    exp: now + ttlMs,
    ...grant,
  };
  const payloadB64 = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64url');
  const sig = createHmac('sha256', secret).update(payloadB64).digest('hex');
  return `${payloadB64}.${sig}`;
}

const TICKET_SHAPE = /^[A-Za-z0-9_-]+\.[0-9a-f]{64}$/;

/**
 * Whether a token has the wire format of a handshake ticket, valid or not. A
 * client access token (a classic Nightscout `name-digest`, a `noc_` token or a
 * JWT) never does.
 */
export function isHandshakeTicketShaped(token: string): boolean {
  return TICKET_SHAPE.test(token);
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
  const { h, t, exp } = parsed;
  if (typeof h !== 'string' || typeof t !== 'string' || !t || typeof exp !== 'number') return null;
  if (exp < now) return null;

  return { h, t, exp, ...readRealtimeGrant(parsed) };
}
