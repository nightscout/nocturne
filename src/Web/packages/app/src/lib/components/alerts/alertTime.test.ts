import { describe, it, expect } from "vitest";
import { formatRange } from "./alertTime.svelte";

describe("alertTime", () => {
  describe("formatRange", () => {
    it("returns empty string when either side missing", () => {
      expect(formatRange(undefined, new Date())).toBe("");
      expect(formatRange(new Date(), undefined)).toBe("");
      expect(formatRange(undefined, undefined)).toBe("");
    });
    it("contains an em-dash separator when both sides valid", () => {
      const s = formatRange(
        new Date("2025-03-05T14:32:00Z"),
        new Date("2025-03-05T15:00:00Z")
      );
      expect(s).toContain(" — ");
    });
  });
});
