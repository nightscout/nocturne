import { createHmac, timingSafeEqual } from 'node:crypto';

/**
 * HMAC-signed handshake ticket for the Socket.IO realtime bridge.
 *
 * Why: the bridge runs in the same Node process as the SvelteKit web app, but a
 * browser's Socket.IO handshake reaches the bridge directly — it does NOT pass
 * through the app's BFF proxy. That proxy is where the browser's short-lived
 * (15-minute) access token is transparently refreshed and the service instance
 * key is attached, so replaying the raw handshake cookie against the API fails
 * once the access token turns over (and can corrupt the session by triggering a
 * refresh-token rotation the bridge can't return to the browser).
 *
 * Instead, the web app's `/realtime/ticket` endpoint — which DOES run inside the
 * BFF — replays the connection's read against the API's per-tenant read policy
 * and, only on success, mints one of these tickets. The browser presents it in
 * the Socket.IO `auth` payload; the bridge verifies it locally with the shared
 * INSTANCE_KEY. No per-connection API call, no cookie replay, no rotation.
 *
 * Wire format: `base64url(json).hexSig`.
 * Payload: `{ h, exp }` — `h` is the normalized host the ticket authorizes,
 * `exp` is a unix-ms deadline. Binding to the host (not just the tenant slug)
 * means a ticket minted for one tenant cannot be replayed on a connection that
 * arrives on a different host. It requires the minting endpoint to sign the same
 * host the browser's handshake will present; both derive it from the connection
 * host (X-Forwarded-Host behind the gateway), so they agree for normal tenant
 * subdomains and the apex single-tenant case alike.
 * Signature: HMAC-SHA256 over the base64url payload using INSTANCE_KEY.
 */

export interface HandshakeTicketPayload {
  /** Normalized host (lowercased, port-stripped) the ticket authorizes. */
  h: string;
  /** Expiration timestamp in unix milliseconds. */
  exp: number;
}

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
  ttlMs: number = HANDSHAKE_TICKET_LIFETIME_MS,
  now: number = Date.now(),
): string {
  const payload: HandshakeTicketPayload = {
    h: normalizeHandshakeHost(host),
    exp: now + ttlMs,
  };
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

  let payload: HandshakeTicketPayload;
  try {
    payload = JSON.parse(Buffer.from(payloadB64, 'base64url').toString('utf-8')) as HandshakeTicketPayload;
  } catch {
    return null;
  }

  if (typeof payload.h !== 'string' || typeof payload.exp !== 'number') return null;
  if (payload.exp < now) return null;

  return payload;
}
