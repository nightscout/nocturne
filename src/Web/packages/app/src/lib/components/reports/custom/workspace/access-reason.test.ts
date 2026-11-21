import { describe, expect, it } from "vitest";
import { accessReason } from "./vocabulary-words.svelte.js";

describe("why an access cannot read what a block needs", () => {
  it("joins three missing scopes as a list with the last word set apart", () => {
    const text = accessReason(["glucose.read", "treatments.read", "stepcount.read"]);

    expect(text).toMatch(/^Your access cannot read \S+, \S+, or \S+, which this needs\.$/);
  });

  it("names no scope when none is given", () => {
    expect(accessReason([])).toBe("Your access cannot read the data this needs.");
  });
});
