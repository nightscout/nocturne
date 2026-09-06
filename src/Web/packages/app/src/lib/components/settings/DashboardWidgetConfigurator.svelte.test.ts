import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import {
  WidgetId,
  WidgetPlacement,
} from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/widget-registry";

const top = (id: string, name: string) => ({
  id,
  name,
  placement: WidgetPlacement.Top,
  renderable: true,
});

// The catalogue the picker reads: the top widgets it can render, plus a main
// section, a retired id, and a top widget from a server newer than this build.
const definitions = [
  top(WidgetId.BgDelta, "BG Delta"),
  top(WidgetId.LastUpdated, "Last Updated"),
  top(WidgetId.ConnectionStatus, "Connection Status"),
  top(WidgetId.Meals, "Recent Meals"),
  top(WidgetId.Trackers, "Trackers"),
  top(WidgetId.TirChart, "Time in Range"),
  top(WidgetId.DailySummary, "Daily Summary"),
  top(WidgetId.Clock, "Clock"),
  top(WidgetId.Tdd, "Total Daily Dose"),
  top("Sparklines", "Sparklines"),
  {
    id: WidgetId.GlucoseChart,
    name: "Glucose Chart",
    placement: WidgetPlacement.Main,
    renderable: true,
  },
  {
    id: WidgetId.BatteryStatus,
    name: "Battery Status",
    placement: WidgetPlacement.Main,
    renderable: false,
  },
];

vi.mock("$api/generated/metadatas.generated.remote", () => ({
  getWidgetDefinitions: () => ({ current: { definitions } }),
}));

import DashboardWidgetConfigurator from "./DashboardWidgetConfigurator.svelte";

const OFFERED = [
  "BG Delta",
  "Last Updated",
  "Connection Status",
  "Recent Meals",
  "Trackers",
  "Time in Range",
  "Daily Summary",
  "Clock",
  "Total Daily Dose",
];

describe("DashboardWidgetConfigurator", () => {
  it("offers the catalogue's top widgets the grid has a loader for, under their catalogue names", async () => {
    render(DashboardWidgetConfigurator, { props: { value: [] } });

    for (const name of OFFERED) {
      await expect.element(page.getByRole("button", { name })).toBeVisible();
    }
    expect(page.getByRole("button").elements()).toHaveLength(OFFERED.length);
  });

  it("ignores a stored widget id the grid cannot render", async () => {
    const writes: TopWidgetId[][] = [];
    render(DashboardWidgetConfigurator, {
      props: {
        value: [WidgetId.BgDelta, WidgetId.GlucoseChart, WidgetId.Tdd],
        onchange: (widgets) => writes.push(widgets),
      },
    });

    expect(page.getByRole("listitem").elements()).toHaveLength(2);
    await expect
      .element(page.getByText("Glucose Chart", { exact: true }))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByText("Total Daily Dose", { exact: true }))
      .toBeVisible();

    await page.getByRole("button", { name: "Clock" }).click();

    expect(writes.at(-1)).toEqual([
      WidgetId.BgDelta,
      WidgetId.Tdd,
      WidgetId.Clock,
    ]);
  });
});
