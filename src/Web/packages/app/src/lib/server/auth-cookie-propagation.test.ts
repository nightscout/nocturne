import { describe, it, expect } from "vitest";
import {
  propagateAuthCookies,
  type CookieSetter,
  type CookieSetOptions,
} from "./auth-cookie-propagation";
import { AUTH_COOKIE_NAMES } from "../config/auth-cookies";

interface SetCall {
  op: "set";
  name: string;
  value: string;
  opts: CookieSetOptions & { path: string };
}

interface DeleteCall {
  op: "delete";
  name: string;
  opts: { path: string; domain?: string };
}

type RecordedCall = SetCall | DeleteCall;

function createRecordingCookies(): {
  cookies: CookieSetter;
  calls: RecordedCall[];
} {
  const calls: RecordedCall[] = [];
  const cookies: CookieSetter = {
    set(name, value, opts) {
      calls.push({ op: "set", name, value, opts });
    },
    delete(name, opts) {
      calls.push({ op: "delete", name, opts });
    },
  };
  return { cookies, calls };
}

describe("propagateAuthCookies", () => {
  it("propagates a rotated access token Set-Cookie onto the outgoing response", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        ".Nocturne.AccessToken=new-access-token; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=900",
      ],
      cookies
    );

    expect(calls).toHaveLength(1);
    expect(calls[0]).toEqual({
      op: "set",
      name: ".Nocturne.AccessToken",
      value: "new-access-token",
      opts: {
        path: "/",
        httpOnly: true,
        secure: true,
        sameSite: "lax",
        maxAge: 900,
      },
    });
  });

  it("propagates a rotated refresh token so the browser gets the new token after rotation (regression for SSR auto-refresh bug)", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.accessToken}=access; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=900`,
        `${AUTH_COOKIE_NAMES.refreshToken}=rotated-refresh; Path=/; HttpOnly; Secure; SameSite=Lax; Max-Age=604800`,
        `IsAuthenticated=true; Path=/; Secure; SameSite=Lax; Max-Age=604800`,
      ],
      cookies
    );

    const setByName = Object.fromEntries(
      calls
        .filter((c): c is SetCall => c.op === "set")
        .map((c) => [c.name, c])
    );

    expect(setByName[AUTH_COOKIE_NAMES.accessToken]?.value).toBe("access");
    expect(setByName[AUTH_COOKIE_NAMES.refreshToken]?.value).toBe(
      "rotated-refresh"
    );
    expect(setByName[AUTH_COOKIE_NAMES.refreshToken]?.opts.httpOnly).toBe(true);
    expect(setByName[AUTH_COOKIE_NAMES.refreshToken]?.opts.maxAge).toBe(604800);

    expect(setByName.IsAuthenticated?.value).toBe("true");
    // IsAuthenticated is frontend-visible, so must not be httpOnly
    expect(setByName.IsAuthenticated?.opts.httpOnly).toBeUndefined();
  });

  it("ignores Set-Cookie headers for cookies that are not auth-related", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        "nocturne-language=en; Path=/; Max-Age=31536000",
        "some_analytics_id=abc123; Path=/",
      ],
      cookies
    );

    expect(calls).toHaveLength(0);
  });

  it("propagates the guest session cookie", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.guestSession}=encrypted-grant; Path=/; HttpOnly; Secure; SameSite=Lax`,
      ],
      cookies
    );

    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({
      op: "set",
      name: AUTH_COOKIE_NAMES.guestSession,
      value: "encrypted-grant",
      opts: { path: "/", httpOnly: true, secure: true, sameSite: "lax" },
    });
  });

  it("propagates the guest session cookie's deletion", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [`${AUTH_COOKIE_NAMES.guestSession}=; Path=/; Max-Age=0`],
      cookies
    );

    expect(calls[0]).toMatchObject({
      op: "delete",
      name: AUTH_COOKIE_NAMES.guestSession,
    });
  });

  it("propagates the recovery session, and the expiry that spends it", () => {
    // Recovery-code sign-in is a server-side form: dropped here, the visitor never receives the
    // credential their code bought and cannot register the passkey that gets them back in.
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.recoverySession}=recovery-token; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age=600`,
      ],
      cookies
    );

    expect(calls[0]).toMatchObject({
      op: "set",
      name: AUTH_COOKIE_NAMES.recoverySession,
      value: "recovery-token",
      opts: { path: "/", httpOnly: true, sameSite: "strict", maxAge: 600 },
    });

    const spent = createRecordingCookies();
    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.recoverySession}=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT`,
      ],
      spent.cookies
    );

    expect(spent.calls[0]).toMatchObject({
      op: "delete",
      name: AUTH_COOKIE_NAMES.recoverySession,
    });
  });

  it("propagates cookie deletion when the server expires an auth cookie", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.accessToken}=; Path=/; Max-Age=0`,
        `${AUTH_COOKIE_NAMES.refreshToken}=; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT`,
      ],
      cookies
    );

    expect(calls).toHaveLength(2);
    expect(calls[0]).toMatchObject({
      op: "delete",
      name: AUTH_COOKIE_NAMES.accessToken,
      opts: { path: "/" },
    });
    expect(calls[1]).toMatchObject({
      op: "delete",
      name: AUTH_COOKIE_NAMES.refreshToken,
      opts: { path: "/" },
    });
  });

  it("forwards the Domain attribute when the backend sets it", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        `${AUTH_COOKIE_NAMES.accessToken}=t; Path=/; Domain=.example.com; HttpOnly; Secure; SameSite=Lax; Max-Age=900`,
      ],
      cookies
    );

    expect(calls[0]).toMatchObject({
      op: "set",
      name: AUTH_COOKIE_NAMES.accessToken,
      opts: { domain: ".example.com" },
    });
  });

  it("handles an empty list safely", () => {
    const { cookies, calls } = createRecordingCookies();
    propagateAuthCookies([], cookies);
    expect(calls).toHaveLength(0);
  });

  it("defaults Path to / when the Set-Cookie omits it", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [`${AUTH_COOKIE_NAMES.accessToken}=t; HttpOnly; Secure`],
      cookies
    );

    expect(calls[0]).toMatchObject({
      op: "set",
      opts: { path: "/" },
    });
  });

  it("skips malformed Set-Cookie headers without throwing", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(
      [
        "", // empty
        "no-equals-sign",
        `=noname; Path=/`,
        `${AUTH_COOKIE_NAMES.accessToken}=valid; Path=/; HttpOnly`,
      ],
      cookies
    );

    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({
      op: "set",
      name: AUTH_COOKIE_NAMES.accessToken,
      value: "valid",
    });
  });
});

describe("propagateAuthCookies with a same-name pair", () => {
  const EXPIRED = "Thu, 01 Jan 1970 00:00:00 GMT";

  /** What the API emits on sign-out: the domain-wide cookie and the pre-widening host-scoped one. */
  const signOutHeaders = [
    `${AUTH_COOKIE_NAMES.accessToken}=; Path=/; Expires=${EXPIRED}`,
    `${AUTH_COOKIE_NAMES.accessToken}=; Path=/; Domain=.nocturne.run; Expires=${EXPIRED}`,
    `${AUTH_COOKIE_NAMES.refreshToken}=; Path=/; Expires=${EXPIRED}`,
    `${AUTH_COOKIE_NAMES.refreshToken}=; Path=/; Domain=.nocturne.run; Expires=${EXPIRED}`,
  ];

  /** What the API emits on a silent refresh: host-scoped expiry, then the domain-wide value. */
  const refreshHeaders = [
    `${AUTH_COOKIE_NAMES.accessToken}=; Path=/; Expires=${EXPIRED}`,
    `${AUTH_COOKIE_NAMES.accessToken}=rotated; Path=/; Domain=.nocturne.run; HttpOnly; Secure; SameSite=Lax; Max-Age=900`,
  ];

  it("keeps both halves of a sign-out pair alive", () => {
    const { cookies, calls } = createRecordingCookies();
    const raw: string[] = [];

    propagateAuthCookies(signOutHeaders, cookies, (h) => raw.push(h));

    // SvelteKit's jar keys pending cookies by name alone, so passing both halves through it
    // would drop one — leaving a host-scoped access token valid on the tenant host for the rest
    // of its lifetime after the user signed out.
    for (const name of [AUTH_COOKIE_NAMES.accessToken, AUTH_COOKIE_NAMES.refreshToken]) {
      expect(calls).toContainEqual({
        op: "delete",
        name,
        opts: { path: "/", domain: ".nocturne.run" },
      });
      expect(raw).toContainEqual(`${name}=; Path=/; Expires=${EXPIRED}`);
    }
    expect(calls).toHaveLength(2);
    expect(raw).toHaveLength(2);
  });

  it("keeps the host-scoped expiry alongside the rotated domain-wide value", () => {
    const { cookies, calls } = createRecordingCookies();
    const raw: string[] = [];

    propagateAuthCookies(refreshHeaders, cookies, (h) => raw.push(h));

    // Losing the expiry leaves the stale host-scoped cookie in the browser for its full life,
    // and the browser sends both under one indistinguishable Cookie header.
    expect(calls).toEqual([
      {
        op: "set",
        name: AUTH_COOKIE_NAMES.accessToken,
        value: "rotated",
        opts: {
          path: "/",
          domain: ".nocturne.run",
          httpOnly: true,
          secure: true,
          sameSite: "lax",
          maxAge: 900,
        },
      },
    ]);
    expect(raw).toEqual([
      `${AUTH_COOKIE_NAMES.accessToken}=; Path=/; Expires=${EXPIRED}`,
    ]);
  });

  it("routes a lone host-scoped cookie through the jar, not the raw sink", () => {
    const { cookies, calls } = createRecordingCookies();
    const raw: string[] = [];

    // A host-only deployment (localhost, an IP literal) emits no domain-wide sibling at all.
    propagateAuthCookies(
      [`${AUTH_COOKIE_NAMES.accessToken}=t; Path=/; HttpOnly; Secure`],
      cookies,
      (h) => raw.push(h)
    );

    expect(raw).toEqual([]);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ op: "set", name: AUTH_COOKIE_NAMES.accessToken });
  });

  it("keeps the domain-wide half when no sink is supplied", () => {
    const { cookies, calls } = createRecordingCookies();

    propagateAuthCookies(signOutHeaders, cookies);

    // No worse than before the sink existed: the wide cookie, which every host presents, is the
    // one that has to go.
    expect(calls.filter((c) => c.opts.domain === ".nocturne.run")).toHaveLength(2);
  });
});
