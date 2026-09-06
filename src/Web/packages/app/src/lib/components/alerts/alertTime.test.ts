import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { formatRange, formatTimeSince } from "./alertTime";

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

  describe("formatTimeSince", () => {
    beforeEach(() => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2025-03-05T15:00:00Z"));
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    it("returns 'Unknown' when undefined", () => {
      expect(formatTimeSince(undefined)).toBe("Unknown");
    });
    it("returns 'Just now' for very recent", () => {
      expect(formatTimeSince(new Date("2025-03-05T14:59:50Z"))).toBe("Just now");
    });
    it("returns minutes for sub-hour", () => {
      expect(formatTimeSince(new Date("2025-03-05T14:48:00Z"))).toBe("12m ago");
    });
    it("returns hours+minutes for sub-day", () => {
      expect(formatTimeSince(new Date("2025-03-05T11:55:00Z"))).toBe(
        "3h 5m ago"
      );
    });
    it("returns days for older", () => {
      expect(formatTimeSince(new Date("2025-03-03T15:00:00Z"))).toBe("2d ago");
    });
  });
});
