import { describe, expect, it } from "vitest";
import { isShareHost } from "./request-host";

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
