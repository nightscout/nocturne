import { describe, expect, it } from "vitest";
import { SetupHubItemKey } from "$api";
import { setupHubItems, setupHubItemBySlug } from "./items.svelte";

describe("setup hub item registry", () => {
  it("presents every item the server can list, each at its own page", () => {
    const slugs = Object.values(SetupHubItemKey).map((key) => setupHubItems()[key].slug);

    expect(new Set(slugs).size).toBe(slugs.length);
    for (const key of Object.values(SetupHubItemKey)) {
      expect(setupHubItemBySlug(setupHubItems()[key].slug)).toBe(key);
    }
    expect(setupHubItemBySlug("nope")).toBeUndefined();
  });

  it("describes each item in whole sentences for each way of speaking of the patient", () => {
    for (const view of Object.values(setupHubItems())) {
      expect(view.description({ kind: "named", name: "Sam" })).toContain("Sam");
      expect(view.description({ kind: "self" })).not.toContain("Sam");
      expect(view.description({ kind: "neutral" })).toMatch(/\.$/);
    }
  });
});
