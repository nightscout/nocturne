import { describe, expect, it } from "vitest";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { buildProxyHeaders } from "./api-proxy-headers";
import {
  CLIENT_IP_HEADER,
  CLIENT_IP_SIGNATURE_HEADER,
} from "./client-address";

const JAR: Record<string, string> = {
  [AUTH_COOKIE_NAMES.accessToken]: "access-value",
  [AUTH_COOKIE_NAMES.refreshToken]: "refresh-value",
  [AUTH_COOKIE_NAMES.guestSession]: "guest-value",
  [AUTH_COOKIE_NAMES.platformAccess]: "platform-value",
  [AUTH_COOKIE_NAMES.recoverySession]: "recovery-value",
};

const cookies = { get: (name: string) => JAR[name] };
const emptyCookies = { get: () => undefined };

/** A browser request to a share host: the widened cookie domain means it carries the session. */
function shareRequestHeaders(): Headers {
  return new Headers({
    Cookie: `${AUTH_COOKIE_NAMES.accessToken}=access-value; ${AUTH_COOKIE_NAMES.refreshToken}=refresh-value`,
    Accept: "application/json",
  });
}

describe("buildProxyHeaders", () => {
  it("sends no cookies to the API from a share host", () => {
    const headers = buildProxyHeaders({
      requestHeaders: shareRequestHeaders(),
      effectiveHost: "tok.share.nocturne.run",
      proto: "https",
      isShareHost: true,
      cookies,
    });

    // Both halves matter: the jar must not be read, AND the raw Cookie header copied from the
    // browser request must be removed — it already carries the session and refresh tokens.
    expect(headers.has("Cookie")).toBe(false);
    expect([...headers.keys()].map((k) => k.toLowerCase())).not.toContain("cookie");
  });

  it("keeps forwarding the caller's cookies off a share host", () => {
    const headers = buildProxyHeaders({
      requestHeaders: new Headers(),
      effectiveHost: "acme.nocturne.run",
      proto: "https",
      isShareHost: false,
      cookies,
    });

    const cookie = headers.get("Cookie")!;
    expect(cookie).toContain(`${AUTH_COOKIE_NAMES.accessToken}=access-value`);
    expect(cookie).toContain(`${AUTH_COOKIE_NAMES.refreshToken}=refresh-value`);
    expect(cookie).toContain(`${AUTH_COOKIE_NAMES.guestSession}=guest-value`);
    expect(cookie).toContain(`${AUTH_COOKIE_NAMES.platformAccess}=platform-value`);
    // A recovery-code visitor's only credential: unforwarded, the passkey they came to register
    // cannot be enrolled and the recovery code is spent for nothing.
    expect(cookie).toContain(`${AUTH_COOKIE_NAMES.recoverySession}=recovery-value`);
  });

  it("leaves the incoming Cookie header alone when the jar holds no auth cookies", () => {
    const headers = buildProxyHeaders({
      requestHeaders: new Headers({ Cookie: "nocturne-language=fr" }),
      effectiveHost: "acme.nocturne.run",
      proto: "https",
      isShareHost: false,
      cookies: emptyCookies,
    });

    // Non-auth cookies (locale, preferences) are not the proxy's business to strip.
    expect(headers.get("Cookie")).toBe("nocturne-language=fr");
  });

  it("strips client-supplied instance auth on every host", () => {
    for (const isShareHost of [true, false]) {
      const headers = buildProxyHeaders({
        requestHeaders: new Headers({
          "X-Instance-Key": "smuggled",
          "X-Instance-Service": "web",
        }),
        effectiveHost: "acme.nocturne.run",
        proto: "https",
        isShareHost,
        cookies: emptyCookies,
      });

      expect(headers.has("X-Instance-Key")).toBe(false);
      expect(headers.has("X-Instance-Service")).toBe(false);
    }
  });

  it("strips a client-written client address on every host", () => {
    for (const isShareHost of [true, false]) {
      const headers = buildProxyHeaders({
        requestHeaders: new Headers({
          [CLIENT_IP_HEADER]: "198.51.100.9",
          [CLIENT_IP_SIGNATURE_HEADER]: "forged",
          "X-Forwarded-For": "203.0.113.4",
        }),
        effectiveHost: "acme.nocturne.run",
        proto: "https",
        isShareHost,
        cookies: emptyCookies,
      });

      expect(headers.has(CLIENT_IP_HEADER)).toBe(false);
      expect(headers.has(CLIENT_IP_SIGNATURE_HEADER)).toBe(false);
      // The address the gateway saw still travels — it is what the API partitions on here.
      expect(headers.get("X-Forwarded-For")).toBe("203.0.113.4");
    }
  });

  it("forwards the host and scheme the browser used", () => {
    const headers = buildProxyHeaders({
      requestHeaders: new Headers(),
      effectiveHost: "acme.nocturne.run",
      proto: "https",
      isShareHost: false,
      cookies: emptyCookies,
    });

    expect(headers.get("X-Forwarded-Host")).toBe("acme.nocturne.run");
    expect(headers.get("X-Forwarded-Proto")).toBe("https");
  });

  it("omits the forwarded host when there is none to forward", () => {
    const headers = buildProxyHeaders({
      requestHeaders: new Headers(),
      effectiveHost: null,
      proto: "http",
      isShareHost: false,
      cookies: emptyCookies,
    });

    expect(headers.has("X-Forwarded-Host")).toBe(false);
  });
});
