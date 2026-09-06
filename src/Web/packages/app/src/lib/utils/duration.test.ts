import { describe, it, expect, vi } from "vitest";
import {
  formatElapsedDuration,
  formatElapsedMs,
  formatMinutesDuration,
} from "./duration";

describe("formatMinutesDuration", () => {
  it("renders zero and negatives as 0m", () => {
    expect(formatMinutesDuration(0)).toBe("0m");
    expect(formatMinutesDuration(-30)).toBe("0m");
  });

  it("renders a sub-hour span in minutes", () => {
    expect(formatMinutesDuration(45)).toBe("45m");
  });

  it("drops the minutes when the span is whole hours", () => {
    expect(formatMinutesDuration(180)).toBe("3h");
  });

  it("rounds to the nearest minute rather than truncating", () => {
    expect(formatMinutesDuration(89.6)).toBe("1h 30m");
  });

  it("carries a rounded 60th minute into the hour", () => {
    expect(formatMinutesDuration(119.7)).toBe("2h");
  });

  it("keeps counting in hours past a day", () => {
    expect(formatMinutesDuration(25 * 60 + 30)).toBe("25h 30m");
    expect(formatMinutesDuration(48 * 60)).toBe("48h");
  });
});

describe("formatElapsedDuration", () => {
  const start = new Date("2025-03-05T14:00:00Z");

  it("yields the caller's placeholder when there is no start", () => {
    expect(formatElapsedDuration(undefined, new Date())).toBe("");
    expect(formatElapsedDuration(null, new Date(), "-")).toBe("-");
  });

  it("yields the caller's placeholder for an unparseable start", () => {
    expect(formatElapsedDuration("not a date", new Date(), "-")).toBe("-");
  });

  it("renders anything under a minute as '< 1m'", () => {
    expect(formatElapsedDuration(start, new Date("2025-03-05T14:00:00Z"))).toBe(
      "< 1m"
    );
    expect(
      formatElapsedDuration(start, new Date("2025-03-05T14:00:59.900Z"))
    ).toBe("< 1m");
  });

  it("renders a sub-hour span in minutes", () => {
    expect(formatElapsedDuration(start, new Date("2025-03-05T14:45:00Z"))).toBe(
      "45m"
    );
  });

  it("renders a longer span in hours and minutes", () => {
    expect(formatElapsedDuration(start, new Date("2025-03-05T15:12:00Z"))).toBe(
      "1h 12m"
    );
  });

  it("accepts ISO strings on either side", () => {
    expect(
      formatElapsedDuration("2025-03-05T14:00:00Z", "2025-03-05T15:12:00Z")
    ).toBe("1h 12m");
  });

  it("measures a still-running span to now", () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2025-03-05T15:00:00Z"));
    try {
      expect(
        formatElapsedDuration(new Date("2025-03-05T14:30:00Z"), undefined)
      ).toBe("30m");
    } finally {
      vi.useRealTimers();
    }
  });

  it("clamps an end before the start", () => {
    expect(formatElapsedDuration(start, new Date("2025-03-05T13:00:00Z"))).toBe(
      "< 1m"
    );
  });
});

describe("formatElapsedMs", () => {
  it("yields the caller's placeholder when there is no measurement", () => {
    expect(formatElapsedMs(undefined)).toBe("N/A");
    expect(formatElapsedMs(null, "—")).toBe("—");
  });

  it("keeps milliseconds below a second", () => {
    expect(formatElapsedMs(0)).toBe("0ms");
    expect(formatElapsedMs(820)).toBe("820ms");
    expect(formatElapsedMs(999)).toBe("999ms");
  });

  it("switches to seconds at a second", () => {
    expect(formatElapsedMs(1000)).toBe("1.00s");
    expect(formatElapsedMs(1204)).toBe("1.20s");
  });
});
