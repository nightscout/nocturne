import { describe, expect, it, vi } from "vitest";
import { getActogramData } from "#lib/api/actogram.remote.js";
import { servedDays } from "#lib/test-stubs/actogram-days.js";

const AUCKLAND_MINUTES = 13 * 60;
const HONOLULU_MINUTES = -10 * 60;

const hoisted = vi.hoisted(() => ({ days: [] as unknown[] }));

vi.mock("#lib/api/actogram.remote.js", () => ({ getActogramData: vi.fn() }));
vi.mock("./date-params.svelte", () => ({
  requireDateParamsContext: () => ({
    fromDay: "2025-11-01",
    toDay: "2025-11-14",
    dateRangeMillis: {
      from: Date.parse("2025-11-01T00:00:00Z") - HONOLULU_MINUTES * 60_000,
      to: Date.parse("2025-11-14T23:59:59.999Z") - HONOLULU_MINUTES * 60_000,
    },
  }),
}));
vi.mock("./resource-context.svelte", () => ({
  contextResource: (fetcher: () => unknown) => ({
    fetch: fetcher,
    get current() {
      return { days: hoisted.days };
    },
  }),
}));

import { useActogramReport } from "./actogram-report.svelte";

describe("useActogramReport", () => {
  it("draws the picker's patient-calendar days, not the days its browser instants fall on", () => {
    hoisted.days = servedDays("2025-10-18", 45, AUCKLAND_MINUTES);

    const report = useActogramReport("Error");

    expect(report.days[0].date).toBe("2025-11-14");
    expect(report.days.find((day) => day.date === "2025-11-15")).toBeUndefined();
    expect(report.rangeDayCount).toBe(14);
    expect(report.days.filter((day) => day.date >= "2025-11-01").map((day) => day.date)).toEqual(
      Array.from({ length: 14 }, (_, index) => `2025-11-${String(14 - index).padStart(2, "0")}`)
    );
  });

  it("asks for the summaries over the picker's range, ending just after its last millisecond", () => {
    const report = useActogramReport("Error");
    (report.resource as unknown as { fetch: () => unknown }).fetch();

    const from = Date.parse("2025-11-01T00:00:00Z") - HONOLULU_MINUTES * 60_000;
    const to = Date.parse("2025-11-14T23:59:59.999Z") - HONOLULU_MINUTES * 60_000;
    expect(getActogramData).toHaveBeenCalledWith(
      expect.objectContaining({ summaryFrom: from, summaryTo: to + 1 })
    );
  });
});
