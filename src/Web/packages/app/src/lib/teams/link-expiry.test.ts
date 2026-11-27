import { describe, expect, it } from "vitest";
import { linkExpiryOptions } from "./link-expiry.svelte";

describe("link expiry options", () => {
  it("names each lifetime by the days the API takes", () => {
    expect(linkExpiryOptions()).toEqual([
      ["1", "1 day"],
      ["7", "7 days"],
      ["30", "30 days"],
      ["90", "90 days"],
    ]);
  });
});
