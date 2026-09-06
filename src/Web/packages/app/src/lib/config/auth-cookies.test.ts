import { describe, it, expect } from "vitest";
import { AUTH_COOKIE_NAMES, clearAuthCookies, type CookieDeleter } from "./auth-cookies";

function recordingCookies() {
  const deleted: { name: string; path: string }[] = [];
  const cookies: CookieDeleter = {
    delete(name, opts) {
      deleted.push({ name, path: opts.path });
    },
  };
  return { cookies, deleted };
}

describe("clearAuthCookies", () => {
  it("clears the guest session so a guest who logs out stays logged out", () => {
    // authHandle falls back to the guest cookie exactly when both token cookies
    // are absent — the post-logout state — so leaving it behind re-signs them in.
    const { cookies, deleted } = recordingCookies();
    clearAuthCookies(cookies);
    expect(deleted.map((d) => d.name)).toContain(AUTH_COOKIE_NAMES.guestSession);
  });

  it("clears the platform-access grant", () => {
    const { cookies, deleted } = recordingCookies();
    clearAuthCookies(cookies);
    expect(deleted.map((d) => d.name)).toContain(AUTH_COOKIE_NAMES.platformAccess);
  });

  it("clears every session cookie at the root path", () => {
    const { cookies, deleted } = recordingCookies();
    clearAuthCookies(cookies);
    expect(deleted.map((d) => d.name).sort()).toEqual(
      [
        AUTH_COOKIE_NAMES.accessToken,
        AUTH_COOKIE_NAMES.refreshToken,
        AUTH_COOKIE_NAMES.isAuthenticated,
        AUTH_COOKIE_NAMES.guestSession,
        AUTH_COOKIE_NAMES.platformAccess,
      ].sort()
    );
    expect(deleted.every((d) => d.path === "/")).toBe(true);
  });
});
