/**
 * A membership-request message is parked in localStorage while the visitor goes
 * off to sign up, then submitted once they come back authenticated. Both halves
 * must agree on the key, so they share this one.
 *
 * The key is namespaced by host rather than by tenant slug. The request is
 * submitted to whichever tenant the host resolves to, so the slug was never
 * load-bearing — and the apex and share hosts have no slug of their own to name.
 */
const STORAGE_KEY_PREFIX = "nocturne:membership-request:";

export function membershipRequestStorageKey(host: string): string {
  return `${STORAGE_KEY_PREFIX}${host}`;
}
