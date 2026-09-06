import { createHash, timingSafeEqual } from "crypto";
import { env } from "$env/dynamic/private";

/**
 * The shared instance key and the headers that carry it. Owns both directions:
 * the digest this app presents on outbound service calls, and the check applied
 * to inbound service calls. Kept apart from the API-client factory so routes can
 * verify a caller without pulling in the generated client.
 */

/**
 * Header carrying the SHA-256 hex digest of the shared instance key.
 * Must stay in sync with `ServiceNames.Headers.InstanceKey` on the API.
 */
export const INSTANCE_KEY_HEADER = "X-Instance-Key";

/**
 * Header naming the trusted service presenting the instance key. The API's
 * InstanceKeyHandler only authenticates the instance key as admin when this
 * marker is present, so a bare key accidentally forwarded onto an end-user
 * request cannot elevate that request and bypass per-tenant public access.
 * Must stay in sync with `ServiceNames.Headers.InstanceService` on the API.
 */
export const INSTANCE_SERVICE_HEADER = "X-Instance-Service";

/** This app's value for {@link INSTANCE_SERVICE_HEADER}. */
export const INSTANCE_SERVICE_NAME = "nocturne-web";

/**
 * The digest of the configured instance key, used for service authentication.
 * Null when no instance key is configured.
 */
export function getHashedInstanceKey(): string | null {
  const instanceKey = env.INSTANCE_KEY;
  return instanceKey
    ? createHash("sha256").update(instanceKey).digest("hex").toLowerCase()
    : null;
}

/**
 * Verifies that an inbound request carries the internal service credential:
 * {@link INSTANCE_KEY_HEADER} holding the digest of `INSTANCE_KEY`, plus an
 * {@link INSTANCE_SERVICE_HEADER} marker naming the calling service. This is the
 * same credential shape the API's `InstanceKeyValidator` accepts, so both
 * directions of service-to-service traffic use one convention.
 *
 * Use on any SvelteKit route that acts with instance-key (admin) privilege and
 * is exempted from the handles in `hooks.server.ts` — such routes are reachable
 * from the internet through the gateway's `/api/**` route.
 *
 * Fails closed: rejects when no instance key is configured, when either header
 * is missing, and when the presented digest does not match.
 */
export function isTrustedInstanceRequest(request: Request): boolean {
  const expected = getHashedInstanceKey();
  if (!expected) return false;

  const presented = request.headers.get(INSTANCE_KEY_HEADER);
  if (!presented) return false;

  // The service marker distinguishes a deliberate service call from a key that
  // leaked onto an end-user request, matching the API's rule.
  if (!request.headers.get(INSTANCE_SERVICE_HEADER)) return false;

  const presentedBytes = Buffer.from(presented.trim().toLowerCase(), "utf8");
  const expectedBytes = Buffer.from(expected, "utf8");
  // timingSafeEqual throws on differing lengths, so the length check comes first.
  if (presentedBytes.length !== expectedBytes.length) return false;

  return timingSafeEqual(presentedBytes, expectedBytes);
}
