import { describe, it, expect } from "vitest";
import { safeReturnUrl } from "./return-url";

describe("safeReturnUrl", () => {
  it("keeps a rooted path", () => {
    expect(safeReturnUrl("/reports/daily")).toBe("/reports/daily");
    expect(safeReturnUrl("/reports?range=7d#top")).toBe("/reports?range=7d#top");
  });

  it("falls back for an absolute URL", () => {
    expect(safeReturnUrl("https://evil.test/steal")).toBe("/");
  });

  it("falls back for a protocol-relative URL", () => {
    expect(safeReturnUrl("//evil.test/steal")).toBe("/");
  });

  it("falls back for a backslash-smuggled host", () => {
    expect(safeReturnUrl("/\\evil.test")).toBe("/");
    expect(safeReturnUrl("/path\\..\\..")).toBe("/");
  });

  it("falls back for a relative path", () => {
    expect(safeReturnUrl("reports")).toBe("/");
  });

  it("falls back for blank and non-string values", () => {
    expect(safeReturnUrl("")).toBe("/");
    expect(safeReturnUrl("   ")).toBe("/");
    expect(safeReturnUrl(undefined)).toBe("/");
    expect(safeReturnUrl(null)).toBe("/");
    expect(safeReturnUrl(42)).toBe("/");
  });

  it("uses the caller's fallback", () => {
    expect(safeReturnUrl("https://evil.test", "/auth/login")).toBe(
      "/auth/login"
    );
  });
});
