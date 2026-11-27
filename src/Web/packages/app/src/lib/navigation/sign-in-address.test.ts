import { describe, it, expect } from "vitest";
import { signInAddress } from "./sign-in-address";

describe("signInAddress", () => {
  it("returns to a page path with its query", () => {
    expect(signInAddress("/people/wall?group=family")).toBe(
      `/auth/login?returnUrl=${encodeURIComponent("/people/wall?group=family")}`
    );
  });

  it("returns to the home page when that is the page", () => {
    expect(signInAddress("/")).toBe("/auth/login?returnUrl=%2F");
  });

  it.each([
    ["an absolute URL", "https://evil.test/steal"],
    ["a protocol-relative path", "//evil.test/steal"],
    ["a backslash-smuggled host", "/\\evil.test"],
    ["a relative path", "reports"],
    ["an empty path", ""],
  ])("is the bare sign-in address for %s", (_, returnTo) => {
    expect(signInAddress(returnTo)).toBe("/auth/login");
  });

  it("is the bare sign-in address when the page is not known", () => {
    expect(signInAddress()).toBe("/auth/login");
  });
});
