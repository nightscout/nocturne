import { describe, it, expect } from "vitest";
import { satisfiesScope, satisfiesAllScopes } from "./scopes";

describe("satisfiesScope", () => {
  it("accepts an exact grant", () => {
    expect(satisfiesScope(["glucose.read"], "glucose.read")).toBe(true);
  });

  it("accepts full access for anything", () => {
    expect(satisfiesScope(["*"], "treatments.read")).toBe(true);
  });

  it("accepts a readwrite grant for its read counterpart", () => {
    expect(satisfiesScope(["treatments.readwrite"], "treatments.read")).toBe(
      true
    );
  });

  it("does not accept a read grant for a readwrite requirement", () => {
    expect(satisfiesScope(["treatments.read"], "treatments.readwrite")).toBe(
      false
    );
  });

  it("does not accept a grant on another category", () => {
    expect(satisfiesScope(["glucose.readwrite"], "treatments.read")).toBe(
      false
    );
  });

  it("refuses everything on an empty grant", () => {
    expect(satisfiesScope([], "glucose.read")).toBe(false);
  });
});

describe("satisfiesAllScopes", () => {
  it("requires every scope, not any", () => {
    expect(
      satisfiesAllScopes(["glucose.read"], ["glucose.read", "reports.read"])
    ).toBe(false);
    expect(
      satisfiesAllScopes(
        ["glucose.read", "reports.read"],
        ["glucose.read", "reports.read"]
      )
    ).toBe(true);
  });

  it("is satisfied by an empty requirement", () => {
    expect(satisfiesAllScopes([], [])).toBe(true);
  });
});
