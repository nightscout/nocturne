import { describe, expect, it } from "vitest";
import { canChangeTenantSettings } from "./tenant-settings";

describe("canChangeTenantSettings", () => {
  it("allows a member holding tenant.settings", () => {
    expect(canChangeTenantSettings(["glucose.read", "tenant.settings"], false)).toBe(true);
  });

  it("allows full access", () => {
    expect(canChangeTenantSettings(["*"], false)).toBe(true);
  });

  it("refuses a member without tenant.settings", () => {
    expect(canChangeTenantSettings(["glucose.read"], false)).toBe(false);
  });

  it("refuses the demo visitor even though it holds tenant.settings", () => {
    expect(canChangeTenantSettings(["tenant.settings"], true)).toBe(false);
    expect(canChangeTenantSettings(["*"], true)).toBe(false);
  });

  it("refuses when nothing was resolved", () => {
    expect(canChangeTenantSettings(undefined, undefined)).toBe(false);
  });
});
