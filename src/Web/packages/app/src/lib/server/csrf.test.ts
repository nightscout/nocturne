import { describe, expect, it } from "vitest";
import { isCrossSiteFormRequest, isProxiedApiPath, isSignedWebhookPath, isWebAppApiPath } from "./csrf.js";

const SELF = "https://tenant.example";

function req(method: string, path: string, headers: Record<string, string> = {}): [Request] {
  return [new Request(`${SELF}${path}`, { method, headers })];
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

  it.each(["/auth/logout", "/api/v4/bot/dispatch"])("refuses a bodyless cross-site DELETE outside the proxy to %s", (path) => {
    expect(isCrossSiteFormRequest(...req("DELETE", path))).toBe(true);
  });

  it("passes a cross-site form POST with no Origin to a webhook", () => {
    expect(
      isCrossSiteFormRequest(...req("POST", "/api/v4/webhooks/slack", { "content-type": "application/x-www-form-urlencoded" })),
    ).toBe(false);
  });

  it.each(["POST", "DELETE"])("passes a bodyless cross-site %s to a webhook, which the route itself refuses", (method) => {
    expect(isCrossSiteFormRequest(...req(method, "/api/v4/webhooks/telegram"))).toBe(false);
  });

  it.each(["/settings", "/api/v4/bot/dispatch", "/api/v4/webhooksx", "/api/v4/webhooksx/slack"])(
    "refuses a cross-site form POST with no Origin to %s",
    (path) => {
      expect(isCrossSiteFormRequest(...req("POST", path, { "content-type": "application/x-www-form-urlencoded" }))).toBe(true);
    },
  );

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
    ["/api/v4/webhooksx", true],
    ["/api/v4/bot/dispatch", false],
    ["/api/v4/botx", true],
    ["/api/otel/errors", false],
    ["/api/otelx", true],
    ["/dashboard", false],
  ])("%s -> %s", (path, expected) => {
    expect(isProxiedApiPath(path)).toBe(expected);
  });
});

describe("isWebAppApiPath", () => {
  it.each([
    ["/api/v4/webhooks/slack", true],
    ["/api/v4/bot/dispatch", true],
    ["/api/otel/errors", true],
    ["/api/v4/bot", true],
    ["/api/v4/webhooksx", false],
    ["/api/v4/botx", false],
    ["/api/otelx", false],
    ["/api/v3/treatments", false],
    ["/settings", false],
  ])("%s -> %s", (path, expected) => {
    expect(isWebAppApiPath(path)).toBe(expected);
  });
});

describe("isSignedWebhookPath", () => {
  it.each([
    ["/api/v4/webhooks/slack", true],
    ["/api/v4/webhooks", true],
    ["/api/v4/webhooksx", false],
    ["/api/v4/webhooksx/slack", false],
    ["/api/v4/bot/dispatch", false],
    ["/api/otel/errors", false],
    ["/settings", false],
  ])("%s -> %s", (path, expected) => {
    expect(isSignedWebhookPath(path)).toBe(expected);
  });
});
