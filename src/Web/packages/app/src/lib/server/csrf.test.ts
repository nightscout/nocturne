import { describe, expect, it } from "vitest";
import { isCrossSiteFormRequest, isProxiedApiPath } from "./csrf.js";

const SELF = "https://rhys.nocturne.run";

function req(method: string, path: string, headers: Record<string, string> = {}): [Request, string, string] {
  return [new Request(`${SELF}${path}`, { method, headers }), SELF, path];
}

describe("isCrossSiteFormRequest", () => {
  it.each(["DELETE", "PUT", "PATCH"])("passes a bodyless %s to a proxied API path", (method) => {
    expect(isCrossSiteFormRequest(...req(method, "/api/v3/treatments/01a1b01390727e4fb8372df4"))).toBe(false);
  });

  it("refuses a bodyless POST to a proxied API path", () => {
    expect(isCrossSiteFormRequest(...req("POST", "/api/v1/treatments"))).toBe(true);
  });

  it.each(["application/x-www-form-urlencoded", "multipart/form-data; boundary=x", "Text/Plain;charset=utf-8"])(
    "refuses a cross-site %s DELETE to a proxied API path",
    (contentType) => {
      expect(isCrossSiteFormRequest(...req("DELETE", "/api/v3/treatments/x", { "content-type": contentType }))).toBe(true);
    },
  );

  it("passes a JSON POST to a proxied API path", () => {
    expect(isCrossSiteFormRequest(...req("POST", "/api/v1/entries", { "content-type": "application/json" }))).toBe(false);
  });

  it("refuses a bodyless cross-site DELETE outside the proxy", () => {
    expect(isCrossSiteFormRequest(...req("DELETE", "/auth/logout"))).toBe(true);
    expect(isCrossSiteFormRequest(...req("DELETE", "/api/v4/webhooks/telegram"))).toBe(true);
  });

  it("refuses a cross-site form POST to a page", () => {
    expect(isCrossSiteFormRequest(...req("POST", "/auth/logout", { "content-type": "application/x-www-form-urlencoded" }))).toBe(true);
  });

  it("passes a same-origin form POST", () => {
    expect(
      isCrossSiteFormRequest(...req("POST", "/auth/logout", { origin: SELF, "content-type": "application/x-www-form-urlencoded" })),
    ).toBe(false);
  });

  it("passes reads", () => {
    expect(isCrossSiteFormRequest(...req("GET", "/api/v1/entries", { "content-type": "text/plain" }))).toBe(false);
  });
});

describe("isProxiedApiPath", () => {
  it.each([
    ["/api/v3/treatments", true],
    ["/api/v4/webhooks/slack", false],
    ["/api/v4/bot/dispatch", false],
    ["/api/otel/errors", false],
    ["/dashboard", false],
  ])("%s -> %s", (path, expected) => {
    expect(isProxiedApiPath(path)).toBe(expected);
  });
});
