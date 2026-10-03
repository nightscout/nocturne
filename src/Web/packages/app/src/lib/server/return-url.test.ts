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

  it("falls back for a whitespace-smuggled host", () => {
    expect(safeReturnUrl("/\t/evil.test")).toBe("/");
    expect(safeReturnUrl("/\n/evil.test")).toBe("/");
    expect(safeReturnUrl("/\r/evil.test")).toBe("/");
    expect(safeReturnUrl("/ /evil.test")).toBe("/");
    expect(safeReturnUrl("/reports\u0000")).toBe("/");
  });

  it("falls back for dot segments that resolve to a protocol-relative path", () => {
    expect(safeReturnUrl("/.//evil.test")).toBe("/");
    expect(safeReturnUrl("/..//evil.test")).toBe("/");
    expect(safeReturnUrl("/a/..//evil.test")).toBe("/");
    expect(safeReturnUrl("/%2e//evil.test")).toBe("/");
    expect(safeReturnUrl("/%2E%2E//evil.test")).toBe("/");
  });

  it("keeps dot segments that stay on a rooted path", () => {
    expect(safeReturnUrl("/a/../reports")).toBe("/a/../reports");
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

  it("falls back for mixed-case and backslash dot-segment variants", () => {
    expect(safeReturnUrl("/%2e%2E//evil.test")).toBe("/");
    expect(safeReturnUrl("/.%2e//evil.test")).toBe("/");
    expect(safeReturnUrl("/a/%2e%2e//evil.test")).toBe("/");
    expect(safeReturnUrl("/.\\/evil.test")).toBe("/");
    expect(safeReturnUrl("/..\\\\evil.test")).toBe("/");
  });

  it("keeps rooted paths whose separators are only percent-encoded", () => {
    expect(safeReturnUrl("/%2F/evil.test")).toBe("/%2F/evil.test");
    expect(safeReturnUrl("/%5C%5Cevil.test")).toBe("/%5C%5Cevil.test");
  });

  it("keeps legitimate paths verbatim", () => {
    expect(safeReturnUrl("/reports/../settings")).toBe("/reports/../settings");
    expect(safeReturnUrl("/join?token=a%2Fb")).toBe("/join?token=a%2Fb");
    expect(safeReturnUrl("/reports?range=7d&next=//x#section")).toBe(
      "/reports?range=7d&next=//x#section"
    );
  });
});
