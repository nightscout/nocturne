import { describe, expect, it } from "vitest";
import { CSP_REPORT_URI, KIT_CSP, REPORT_ONLY_DIRECTIVES } from "./content-security-policy";
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

describe("report-only Content-Security-Policy", () => {
  const REPORT_ONLY = "Content-Security-Policy-Report-Only";
  const FROM_KIT = "script-src 'self' 'wasm-unsafe-eval' 'nonce-abc'; report-uri /api/otel/errors";

  it("is left as SvelteKit wrote it, beside the enforced frame-ancestors", () => {
    const headers = apply("/", {
      frameAncestors: ["https://ha.example.com"],
      initial: { [REPORT_ONLY]: FROM_KIT },
    });
    expect(headers.get(REPORT_ONLY)).toBe(FROM_KIT);
    expect(headers.get("Content-Security-Policy")).toBe("frame-ancestors 'self' https://ha.example.com");
  });

  it("is left alone where framing is allowed and where it is forbidden", () => {
    for (const [path, isShareHost] of [["/clock/abc", false], ["/", true], ["/settings", false]] as const) {
      expect(apply(path, { isShareHost, initial: { [REPORT_ONLY]: FROM_KIT } }).get(REPORT_ONLY)).toBe(FROM_KIT);
    }
  });

  it("is sent by SvelteKit as report-only with a nonce and never enforced", async () => {
    (globalThis as { __SVELTEKIT_DEV__?: boolean }).__SVELTEKIT_DEV__ = false;
    const { Csp } = await import(
      // @ts-expect-error Kit publishes no declarations for its internal CSP module
      "../../../node_modules/@sveltejs/kit/src/runtime/server/page/csp.js"
    );
    const csp = new Csp({ directives: {}, ...KIT_CSP, reportOnly: { ...KIT_CSP.reportOnly } }, { prerender: false });

    await csp.add_script("hydrate()");

    expect(csp.script_needs_nonce).toBe(true);
    expect(csp.csp_provider.get_header()).toBe("");
    const header = csp.report_only_provider.get_header();
    expect(header).toMatch(/script-src 'self' 'wasm-unsafe-eval' 'nonce-[^']+'/);
    expect(header).toContain("object-src 'none'");
    expect(header).toContain("base-uri 'self'");
    expect(header).toContain(`report-uri ${CSP_REPORT_URI}`);
    expect(header).not.toContain("unsafe-inline");
  });

  it("is configured as report-only: no enforcing directives block", () => {
    expect(KIT_CSP.mode).toBe("auto");
    expect(KIT_CSP).not.toHaveProperty("directives");
    expect(KIT_CSP.reportOnly).toBe(REPORT_ONLY_DIRECTIVES);
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
