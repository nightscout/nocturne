import { describe, expect, it } from "vitest";
import {
  SETUP_TENANT_COOKIE,
  extractTenantSlug,
  getEffectiveHost,
  isShareHost,
} from "./request-host";

describe("isShareHost", () => {
  it("matches the {token}.share.{baseDomain} form", () => {
    expect(isShareHost("k7m2q9x4r3wt.share.nocturne.run")).toBe(true);
    expect(isShareHost("k7m2q9x4r3wt.share.localhost:1612")).toBe(true);
    expect(isShareHost("ABCDEF123456.SHARE.nocturne.run")).toBe(true);
  });

  it("rejects bare tenant hosts and the apex", () => {
    expect(isShareHost("rhys.nocturne.run")).toBe(false);
    expect(isShareHost("as-notrune.nocturne.run")).toBe(false);
    expect(isShareHost("nocturne.run")).toBe(false);
  });

  it("rejects a slug that merely contains 'share'", () => {
    expect(isShareHost("myshare.nocturne.run")).toBe(false);
    expect(isShareHost("shared.nocturne.run")).toBe(false);
  });

  it("matches with a port or trailing dot", () => {
    expect(isShareHost("abc.share.nocturne.run:443")).toBe(true);
    expect(isShareHost("abc.share.nocturne.run.")).toBe(true);
  });

  it("rejects the literal share label without a token", () => {
    expect(isShareHost("share.nocturne.run")).toBe(false);
  });

  it("handles null and undefined", () => {
    expect(isShareHost(null)).toBe(false);
    expect(isShareHost(undefined)).toBe(false);
  });
});

describe("extractTenantSlug", () => {
  it("strips a two-label base domain", () => {
    expect(extractTenantSlug("rhys.nocturne.run", "nocturne.run")).toBe("rhys");
  });

  it("returns null on the apex", () => {
    expect(extractTenantSlug("nocturne.run", "nocturne.run")).toBeNull();
  });

  // A base domain may have any number of labels. These two cases are what
  // hostname label-counting got wrong: it read the apex's own first label as a
  // tenant slug.
  it("strips a base domain with more than two labels", () => {
    expect(
      extractTenantSlug("bob.nocturne.example.com", "nocturne.example.com"),
    ).toBe("bob");
  });

  it("returns null on the apex of a base domain with more than two labels", () => {
    expect(
      extractTenantSlug("nocturne.example.com", "nocturne.example.com"),
    ).toBeNull();
  });

  it("returns null for a host outside the base domain", () => {
    expect(extractTenantSlug("bob.example.com", "nocturne.example.com")).toBeNull();
    expect(extractTenantSlug("evil-nocturne.run", "nocturne.run")).toBeNull();
  });

  it("ignores ports on either side", () => {
    expect(
      extractTenantSlug("sleepy.nocturne.localhost:1612", "nocturne.localhost:1612"),
    ).toBe("sleepy");
    expect(extractTenantSlug("sleepy.nocturne.localhost", "nocturne.localhost:1612")).toBe(
      "sleepy",
    );
  });

  // Matches SubdomainParser: the suffix compares case-insensitively, but the
  // slug keeps the host's casing because the tenant lookup is case-sensitive.
  it("matches the suffix case-insensitively and preserves the slug's case", () => {
    expect(extractTenantSlug("RHYS.Nocturne.Run", "nocturne.run")).toBe("RHYS");
  });

  it("returns the share host's token-and-label rather than a tenant", () => {
    expect(extractTenantSlug("tok.share.nocturne.run", "nocturne.run")).toBe("tok.share");
  });

  it("returns null when the base domain is missing", () => {
    expect(extractTenantSlug("rhys.nocturne.run", null)).toBeNull();
    expect(extractTenantSlug("rhys.nocturne.run", "")).toBeNull();
    expect(extractTenantSlug(null, "nocturne.run")).toBeNull();
  });
});

describe("getEffectiveHost", () => {
  /** A request as it reaches the app behind the gateway, on `host`. */
  function requestOn(host: string): Request {
    return new Request("http://internal/", { headers: { "x-forwarded-host": host } });
  }

  /** A browser presenting the setup cookie left by whoever last ran the wizard here. */
  const setupCookie = { get: (name: string) => (name === SETUP_TENANT_COOKIE ? "acme" : undefined) };

  const noCookies = { get: () => undefined };

  it("prepends the setup tenant slug on the apex, so the API resolves that tenant", () => {
    expect(getEffectiveHost(requestOn("nocturne.run"), setupCookie)).toBe("acme.nocturne.run");
  });

  it("leaves a share host alone, whatever the browser carries", () => {
    const share = "k7m2q9x4r3wt.share.nocturne.run";
    expect(getEffectiveHost(requestOn(share), setupCookie)).toBe(share);
    expect(getEffectiveHost(requestOn(share), noCookies)).toBe(share);
  });
});
