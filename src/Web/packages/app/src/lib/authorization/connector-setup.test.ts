import { describe, expect, it } from "vitest";
import { canSetUpConnectors } from "./connector-setup";

describe("canSetUpConnectors", () => {
  it("allows a member holding connectors.readwrite", () => {
    expect(canSetUpConnectors(["glucose.read", "connectors.readwrite"], false)).toBe(true);
  });

  it("allows a member holding tenant.settings", () => {
    expect(canSetUpConnectors(["tenant.settings"], false)).toBe(true);
  });

  it("allows full access", () => {
    expect(canSetUpConnectors(["*"], false)).toBe(true);
  });

  it("refuses a member holding only connectors.read", () => {
    expect(canSetUpConnectors(["connectors.read"], false)).toBe(false);
  });

  it("refuses the demo visitor", () => {
    expect(canSetUpConnectors(["*"], true)).toBe(false);
  });

  it("refuses when nothing was resolved", () => {
    expect(canSetUpConnectors(undefined, undefined)).toBe(false);
  });
});
