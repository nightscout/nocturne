import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import DashboardWidgetConfigurator from "./DashboardWidgetConfigurator.svelte";
import { WidgetId } from "$lib/api/generated/nocturne-api-client";
import {
  TOP_WIDGET_IDS,
  type TopWidgetId,
} from "$lib/components/dashboard/widget-registry";

/** Mirrors the component's camelCase-to-Title-Case label. */
function label(id: string): string {
  return id.replace(/([A-Z])/g, " $1").trim();
}

describe("DashboardWidgetConfigurator", () => {
  it("offers exactly the widgets the grid has a loader for", async () => {
    render(DashboardWidgetConfigurator, { props: { value: [] } });

    for (const id of TOP_WIDGET_IDS) {
      await expect
        .element(page.getByRole("button", { name: label(id) }))
        .toBeVisible();
    }

    const notRenderable = Object.values(WidgetId).filter(
      (id) => !TOP_WIDGET_IDS.some((known) => known === id)
    );
    expect(notRenderable.length).toBeGreaterThan(0);
    for (const id of notRenderable) {
      await expect
        .element(page.getByRole("button", { name: label(id) }))
        .not.toBeInTheDocument();
    }
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
      .element(page.getByText(label(WidgetId.GlucoseChart), { exact: true }))
      .not.toBeInTheDocument();

    await page.getByRole("button", { name: label(WidgetId.Clock) }).click();

    expect(writes.at(-1)).toEqual([
      WidgetId.BgDelta,
      WidgetId.Tdd,
      WidgetId.Clock,
    ]);
  });
});
