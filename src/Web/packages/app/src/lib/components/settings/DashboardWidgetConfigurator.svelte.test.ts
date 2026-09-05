import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import DashboardWidgetConfigurator from "./DashboardWidgetConfigurator.svelte";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/widget-registry";

/**
 * Spelled out rather than derived from the registry: the point of the test is
 * that the picker cannot drift from the widgets the grid can render.
 */
const OFFERED = [
  "Bg Delta",
  "Last Updated",
  "Connection Status",
  "Meals",
  "Trackers",
  "Tir Chart",
  "Daily Summary",
  "Clock",
  "Tdd",
];

describe("DashboardWidgetConfigurator", () => {
  it("offers exactly the widgets the grid has a loader for", async () => {
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

    await page.getByRole("button", { name: "Clock" }).click();

    expect(writes.at(-1)).toEqual([
      WidgetId.BgDelta,
      WidgetId.Tdd,
      WidgetId.Clock,
    ]);
  });
});
