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
