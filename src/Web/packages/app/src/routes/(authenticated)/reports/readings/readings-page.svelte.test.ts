import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const hoisted = vi.hoisted(() => ({
  asked: [] as unknown[],
  served: {} as { startDate: string; endDate: string },
}));

vi.mock("#lib/api/reports.remote.js", () => ({
  getReportRange: (input: unknown) => {
    hoisted.asked.push(input);
    return remoteQuery(() => hoisted.served);
  },
}));

vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({
    fromDay: "2025-06-01",
    toDay: "2025-06-14",
  }),
}));

vi.mock("#lib/components/dashboard/glucose-chart/index.js", async () => ({
  GlucoseChartCard: (
    await import("#lib/test-stubs/GlucoseChartCard.test-stub.svelte")
  ).default,
}));

import ReadingsPage from "./+page.svelte";

describe("readings report", () => {
  beforeEach(() => {
    hoisted.asked = [];
    hoisted.served = {
      startDate: "2025-05-31T23:00:00.000Z",
      endDate: "2025-06-14T22:59:59.999Z",
    };
  });

  it("asks for the picked days and draws the instants the server read them as", async () => {
    render(ReadingsPage);

    await expect
      .element(page.getByTestId("glucose-chart"))
      .toHaveAttribute("data-from", "2025-05-31T23:00:00.000Z");
    await expect
      .element(page.getByTestId("glucose-chart"))
      .toHaveAttribute("data-to", "2025-06-14T22:59:59.999Z");
    expect(hoisted.asked).toEqual([{ from: "2025-06-01", to: "2025-06-14" }]);
  });
});
