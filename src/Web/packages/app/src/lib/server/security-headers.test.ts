import { describe, expect, it } from "vitest";
import { applySecurityHeaders, parseFrameAncestors } from "./security-headers";

function apply(
  pathname: string,
  { isShareHost = false, frameAncestors = [] as string[], initial = {} as HeadersInit } = {},
): Headers {
  const headers = new Headers(initial);
  applySecurityHeaders(headers, { isShareHost, pathname, frameAncestors });
  return headers;
}

describe("applySecurityHeaders", () => {
  it("forbids content sniffing on every response", () => {
    expect(apply("/").get("X-Content-Type-Options")).toBe("nosniff");
    expect(apply("/api/v4/alert-sounds/x/stream").get("X-Content-Type-Options")).toBe("nosniff");
    expect(apply("/", { isShareHost: true }).get("X-Content-Type-Options")).toBe("nosniff");
  });

  it("forbids framing the signed-in app when no frame ancestors are configured", () => {
    const headers = apply("/settings/alerts");
    expect(headers.get("X-Frame-Options")).toBe("DENY");
    expect(headers.has("Content-Security-Policy")).toBe(false);
  });

  it("lets anyone frame the clock face, and only the clock face", () => {
    expect(apply("/clock/abc123").has("X-Frame-Options")).toBe(false);
    expect(apply("/clock/abc123/").has("X-Frame-Options")).toBe(false);
    expect(apply("/clock/config/abc123").get("X-Frame-Options")).toBe("DENY");
    expect(apply("/clock/abc123/__data.json").get("X-Frame-Options")).toBe("DENY");
    expect(apply("/clock").get("X-Frame-Options")).toBe("DENY");
    expect(apply("/clockwork").get("X-Frame-Options")).toBe("DENY");
  });

  it("lets anyone frame a share host, whatever the path", () => {
    for (const path of ["/", "/reports/agp", "/clock/config/abc123"]) {
      const headers = apply(path, { isShareHost: true, frameAncestors: ["https://ha.example.com"] });
      expect(headers.has("X-Frame-Options")).toBe(false);
      expect(headers.has("Content-Security-Policy")).toBe(false);
    }
  });

  it("drops a proxied response's X-Frame-Options where framing is allowed", () => {
    const initial = { "X-Frame-Options": "DENY" };
    expect(apply("/api/v4/x", { isShareHost: true, initial }).has("X-Frame-Options")).toBe(false);
  });

  it("names the configured frame ancestors instead of forbidding framing", () => {
    const headers = apply("/", { frameAncestors: ["https://ha.example.com:8123"] });
    expect(headers.has("X-Frame-Options")).toBe(false);
    expect(headers.get("Content-Security-Policy")).toBe(
      "frame-ancestors 'self' https://ha.example.com:8123",
    );
  });

  it("sends the origin only, cross-origin, off the share host", () => {
    expect(apply("/").get("Referrer-Policy")).toBe("strict-origin-when-cross-origin");
    expect(apply("/").has("X-Robots-Tag")).toBe(false);
  });

  it("keeps a referrer policy a proxied response already chose", () => {
    const initial = { "Referrer-Policy": "no-referrer" };
    expect(apply("/api/x", { initial }).get("Referrer-Policy")).toBe("no-referrer");
  });

  it("sends no referrer and asks not to be indexed on the share host", () => {
    const headers = apply("/", { isShareHost: true, initial: { "Referrer-Policy": "unsafe-url" } });
    expect(headers.get("Referrer-Policy")).toBe("no-referrer");
    expect(headers.get("X-Robots-Tag")).toBe("noindex, nofollow");
  });
});

describe("parseFrameAncestors", () => {
  it("reads host and scheme sources separated by spaces or commas", () => {
    expect(
      parseFrameAncestors(" https://ha.example.com:8123, http://192.168.1.5:8123  *.example.org https: "),
    ).toEqual(["https://ha.example.com:8123", "http://192.168.1.5:8123", "*.example.org", "https:"]);
  });

  it("is empty when unset or blank", () => {
    expect(parseFrameAncestors(undefined)).toEqual([]);
    expect(parseFrameAncestors("  ")).toEqual([]);
  });

  it("drops anything that could add a keyword or another directive", () => {
    expect(
      parseFrameAncestors("'none' https://ok.example.com;script-src https://a.example.com/x 'unsafe-inline'"),
    ).toEqual([]);
    expect(parseFrameAncestors("https://ok.example.com\r\nX-Evil: 1;").join(" ")).not.toMatch(/[\r\n;]/);
  });
});
