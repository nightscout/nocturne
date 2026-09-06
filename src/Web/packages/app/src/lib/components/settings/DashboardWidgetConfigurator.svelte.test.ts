import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import {
  WidgetId,
  WidgetPlacement,
} from "$lib/api/generated/nocturne-api-client";
import type { TopWidgetId } from "$lib/components/dashboard/widget-registry";

const top = (id: string, name: string, renderable = true) => ({
  id,
  name,
  placement: WidgetPlacement.Top,
  renderable,
});

const main = (id: string, name: string, renderable = true) => ({
  id,
  name,
  placement: WidgetPlacement.Main,
  renderable,
});

const CATALOGUE = [
  top(WidgetId.BgDelta, "BG Delta"),
  top(WidgetId.LastUpdated, "Last Updated"),
  top(WidgetId.ConnectionStatus, "Connection Status"),
  top(WidgetId.Meals, "Recent Meals"),
  top(WidgetId.Trackers, "Trackers"),
  top(WidgetId.TirChart, "Time in Range"),
  top(WidgetId.DailySummary, "Daily Summary"),
  top(WidgetId.Clock, "Clock"),
  top(WidgetId.Tdd, "Total Daily Dose"),
  // Not offerable: a top widget this build has no loader for, a main section,
  // and a top widget the server has marked unrenderable.
  top("Sparklines", "Sparklines"),
  main(WidgetId.GlucoseChart, "Glucose Chart"),
  main(WidgetId.BatteryStatus, "Battery Status", false),
];

// The picker reads `.current` and `.error`; both stay writable so a test can
// stand the query up loaded, degraded or failed.
let current: { definitions: unknown[] } | undefined;
let error: unknown;

vi.mock("$api/generated/metadatas.generated.remote", () => ({
  getWidgetDefinitions: () => ({
    get current() {
      return current;
    },
    get error() {
      return error;
    },
  }),
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
  beforeEach(() => {
    current = { definitions: CATALOGUE };
    error = undefined;
  });

  it("offers the catalogue's top widgets the grid can render, under their catalogue names", async () => {
    render(DashboardWidgetConfigurator, { props: { value: [] } });

    for (const name of OFFERED) {
      await expect.element(page.getByRole("button", { name })).toBeVisible();
    }
    expect(page.getByRole("button").elements()).toHaveLength(OFFERED.length);
  });

  it("does not offer a top widget the server marks unrenderable", async () => {
    current = {
      definitions: [
        top(WidgetId.BgDelta, "BG Delta"),
        top(WidgetId.Clock, "Clock", false),
      ],
    };

    render(DashboardWidgetConfigurator, { props: { value: [] } });

    await expect
      .element(page.getByRole("button", { name: "BG Delta" }))
      .toBeVisible();
    expect(page.getByRole("button").elements()).toHaveLength(1);
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

  it("labels an active widget the catalogue does not name with its id", async () => {
    current = { definitions: CATALOGUE.filter((d) => d.id !== WidgetId.Meals) };

    render(DashboardWidgetConfigurator, {
      props: { value: [WidgetId.Meals] },
    });

    expect(page.getByRole("listitem").elements()).toHaveLength(1);
    await expect
      .element(page.getByText(WidgetId.Meals, { exact: true }))
      .toBeVisible();
  });

  it("degrades to ids when the catalogue cannot be fetched", async () => {
    current = undefined;
    error = new Error("boom");

    render(DashboardWidgetConfigurator, {
      props: { value: [WidgetId.BgDelta] },
    });

    await expect
      .element(page.getByText(/Widget names could not be loaded/))
      .toBeVisible();
    expect(page.getByRole("listitem").elements()).toHaveLength(1);
    await expect
      .element(page.getByText(WidgetId.BgDelta, { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: WidgetId.TirChart }))
      .toBeVisible();
    await expect
      .element(page.getByText("Time in Range", { exact: true }))
      .not.toBeInTheDocument();
  });
});
