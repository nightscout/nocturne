import { render } from "vitest-browser-svelte";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

type Window = { from: string; to: string };

const requested: Window[] = [];
const urlParams: Record<string, unknown> = { update: vi.fn() };

vi.mock("#lib/api/reports.remote.js", () => ({
  getReportsToday: () => remoteQuery(() => "2000-01-02"),
  getReportsComparison: (input: { a: Window; b: Window }) => {
    requested.push(input.a, input.b);
    return remoteQuery(() => ({
      periodA: { hasData: true },
      periodB: { hasData: true },
      metrics: [],
    }));
  },
}));

vi.mock("runed/kit", () => ({ useSearchParams: () => urlParams }));

vi.mock("#lib/components/reports/TIRStackedChart.svelte", () =>
  import("#lib/test-stubs/Empty.test-stub.svelte")
);

import ComparisonPage from "./+page.svelte";

function windows(): Window[] {
  return [...new Map(requested.map((w) => [`${w.from}/${w.to}`, w])).values()];
}

describe("comparison preset windows", () => {
  beforeEach(() => {
    requested.length = 0;
    for (const key of ["preset", "aFrom", "aTo", "aLabel", "bFrom", "bTo", "bLabel"]) urlParams[key] = null;
  });

  it("run to the patient's today, not the browser's", async () => {
    urlParams.preset = "last14-prior14";
    render(ComparisonPage);
    await vi.waitFor(() => expect(requested.length).toBeGreaterThan(0));
    expect(windows()).toEqual([
      { from: "1999-12-06", to: "1999-12-19" },
      { from: "1999-12-20", to: "2000-01-02" },
    ]);
  });

  it("put the month presets on the patient's months", async () => {
    urlParams.preset = "thisMonth-lastMonth";
    render(ComparisonPage);
    await vi.waitFor(() => expect(requested.length).toBeGreaterThan(0));
    expect(windows()).toEqual([
      { from: "1999-12-01", to: "1999-12-31" },
      { from: "2000-01-01", to: "2000-01-02" },
    ]);
  });
});
