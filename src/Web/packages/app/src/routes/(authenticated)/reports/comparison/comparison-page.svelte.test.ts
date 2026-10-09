import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ExtendedGlucoseAnalytics } from "$api/generated/nocturne-api-client";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let periodA: ExtendedGlucoseAnalytics;
let periodB: ExtendedGlucoseAnalytics;

vi.mock("$api/reports.remote", () => ({
  getReportsAnalysis: (input: { from: string }) =>
    remoteQuery(() => ({ analysis: input.from === "2026-09-01" ? periodA : periodB })),
}));

vi.mock("runed/kit", () => ({
  useSearchParams: () => ({
    preset: "custom",
    aFrom: "2026-09-01",
    aTo: "2026-09-01",
    bFrom: "2026-09-02",
    bTo: "2026-09-02",
    aLabel: "A",
    bLabel: "B",
    update: vi.fn(),
  }),
}));

vi.mock("$lib/components/reports/TIRStackedChart.svelte", () =>
  import("$lib/test-stubs/Empty.test-stub.svelte")
);

import ComparisonPage from "./+page.svelte";

function analysis(minutes: number, events: number): ExtendedGlucoseAnalytics {
  return {
    basicStats: { count: 20 },
    timeInRange: {
      durations: { belowRange: minutes, aboveRange: 60 },
      episodes: { belowRange: events, aboveRange: 2 },
    },
  };
}

async function cells(label: string) {
  const locator = page.getByText(label, { exact: true });
  await expect.element(locator).toBeVisible();
  const row = locator.element().parentElement!;
  return Array.from(row.children).map((cell) => cell.textContent?.trim());
}

describe("hypo comparison rows", () => {
  beforeEach(() => {
    periodA = analysis(0, 0);
    periodB = analysis(30, 1);
  });

  it("shows hours, integer events and a positive change from a measured zero", async () => {
    render(ComparisonPage);
    const duration = await cells("Hypo Duration");
    expect(duration.slice(1, 3)).toEqual(["0.0 h", "0.5 h"]);
    expect(duration[4]).toBe("+0.5 h");
    const events = await cells("Hypo Events");
    expect(events.slice(1, 3)).toEqual(["0", "1"]);
    expect(events[4]).toBe("+1");
    expect((await cells("Hyper Duration"))[4]).toBe("±0 h");
    expect((await cells("Hyper Events"))[4]).toBe("±0");
  });

  it("shows a negative change when the second period has fewer lows", async () => {
    periodA = analysis(90, 3);
    render(ComparisonPage);
    expect((await cells("Hypo Duration"))[4]).toBe("−1.0 h");
    expect((await cells("Hypo Events"))[4]).toBe("−2");
  });

  it("shows no data instead of comparing a missing period against zero", async () => {
    periodA = { basicStats: { count: 0 } };
    periodB = analysis(0, 0);
    render(ComparisonPage);
    const duration = await cells("Hypo Duration");
    expect(duration.slice(1, 3)).toEqual(["No data", "0.0 h"]);
    expect(duration[4]).toBe("—");
    const events = await cells("Hypo Events");
    expect(events.slice(1, 3)).toEqual(["No data", "0"]);
    expect(events[4]).toBe("—");
  });
});
