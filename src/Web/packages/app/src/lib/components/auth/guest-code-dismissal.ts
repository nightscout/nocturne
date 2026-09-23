/**
 * Cookie set when someone on this device says they are not signing in with a guest code, so the
 * login page stops opening on code entry. Host-scoped, so it applies to one tenant. It lasts as
 * long as a guest code does (48 hours, GuestLinkService.LinkLifetime): a code created after the
 * dismissal has lapsed opens on code entry again.
 */
export const GUEST_CODE_DISMISSED_COOKIE = "nocturne-guest-code-dismissed";

const MAX_AGE_SECONDS = 48 * 60 * 60;

export function dismissGuestCode(): void {
  document.cookie = `${GUEST_CODE_DISMISSED_COOKIE}=1; path=/; max-age=${MAX_AGE_SECONDS}; samesite=lax`;
}
