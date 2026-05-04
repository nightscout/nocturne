import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  formatTime,
  formatDateTime,
  formatRange,
  formatTimeSince,
  formatDuration,
} from "./alertTime";

describe("alertTime", () => {
  describe("formatTime", () => {
    it("returns non-empty string for valid date", () => {
      expect(formatTime(new Date("2025-03-05T14:32:00Z"))).not.toBe("");
    });
    it("accepts ISO string", () => {
      expect(formatTime("2025-03-05T14:32:00Z")).not.toBe("");
    });
    it("returns empty string when undefined", () => {
      expect(formatTime(undefined)).toBe("");
    });
    it("returns empty string for invalid date string", () => {
      expect(formatTime("not-a-date")).toBe("");
    });
  });

  describe("formatDateTime", () => {
    it("returns non-empty string for valid date", () => {
      expect(formatDateTime(new Date("2025-03-05T14:32:00Z"))).not.toBe("");
    });
    it("returns empty string when undefined", () => {
      expect(formatDateTime(undefined)).toBe("");
    });
  });

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

  describe("formatDuration", () => {
    it("returns empty string when start undefined", () => {
      expect(formatDuration(undefined, new Date())).toBe("");
    });
    it("returns '< 1m' for very short range", () => {
      const s = new Date("2025-03-05T14:00:00Z");
      const e = new Date("2025-03-05T14:00:30Z");
      expect(formatDuration(s, e)).toBe("< 1m");
    });
    it("returns minutes for sub-hour", () => {
      const s = new Date("2025-03-05T14:00:00Z");
      const e = new Date("2025-03-05T14:45:00Z");
      expect(formatDuration(s, e)).toBe("45m");
    });
    it("returns hours+minutes for longer", () => {
      const s = new Date("2025-03-05T14:00:00Z");
      const e = new Date("2025-03-05T15:12:00Z");
      expect(formatDuration(s, e)).toBe("1h 12m");
    });
    it("uses Date.now() when end is undefined", () => {
      vi.useFakeTimers();
      vi.setSystemTime(new Date("2025-03-05T15:00:00Z"));
      try {
        const s = new Date("2025-03-05T14:30:00Z");
        expect(formatDuration(s, undefined)).toBe("30m");
      } finally {
        vi.useRealTimers();
      }
    });
  });
});
