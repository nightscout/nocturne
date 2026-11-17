import { describe, expect, it, vi } from "vitest";
import { getActogramRange } from "#lib/api/actogram.remote.js";
import { servedDays } from "#lib/test-stubs/actogram-days.js";

const AUCKLAND_MINUTES = 13 * 60;

const hoisted = vi.hoisted(() => ({ days: [] as unknown[] }));

vi.mock("#lib/api/actogram.remote.js", () => ({ getActogramRange: vi.fn() }));
vi.mock("./date-params.svelte", () => ({
  requireDateParamsContext: () => ({
    fromDay: "2025-11-01",
    toDay: "2025-11-14",
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

  it("asks for the picker's days, leaving the instants to the server's reading of the patient's zone", () => {
    const report = useActogramReport("Error");
    (report.resource as unknown as { fetch: () => unknown }).fetch();

    expect(getActogramRange).toHaveBeenCalledWith({ from: "2025-11-01", to: "2025-11-14" });
  });
});
