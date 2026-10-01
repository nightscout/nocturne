import { describe, expect, it } from "vitest";
import { canManageConnectors } from "./connector-management";

describe("canManageConnectors", () => {
  it("allows a member holding tenant.settings", () => {
    expect(canManageConnectors(["tenant.settings"], false)).toBe(true);
  });

  it("allows full access", () => {
    expect(canManageConnectors(["*"], false)).toBe(true);
  });

  it("refuses a member without tenant.settings", () => {
    expect(
      canManageConnectors(["glucose.read", "treatments.readwrite"], false)
    ).toBe(false);
  });

  it("refuses the demo visitor even though it holds tenant.settings", () => {
    expect(canManageConnectors(["tenant.settings"], true)).toBe(false);
    expect(canManageConnectors(["*"], true)).toBe(false);
  });

  it("refuses when nothing was resolved", () => {
    expect(canManageConnectors(undefined, undefined)).toBe(false);
  });
});
