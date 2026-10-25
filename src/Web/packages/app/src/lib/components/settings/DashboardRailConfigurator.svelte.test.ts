import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import {
  LegacyConsumableKind,
  RailItemId,
  type DashboardRailPreference,
} from "$lib/api/generated/nocturne-api-client";
import DashboardRailConfigurator from "./DashboardRailConfigurator.svelte";
import DashboardRailConfiguratorHost from "./DashboardRailConfigurator.test-host.svelte";

const { Cob, Basal, Iob, Loop, Reservoir, Trackers } = RailItemId;

function setup(value: DashboardRailPreference, trackerPillsEnabled = true) {
  const writes: DashboardRailPreference[] = [];
  const screen = render(DashboardRailConfigurator, {
    props: { value, trackerPillsEnabled, onchange: (pref) => writes.push(pref) },
  });
  return { writes, screen };
}

/** Rendered as the appearance page does it: each change comes straight back as the value. */
function host(initial: DashboardRailPreference) {
  const writes: DashboardRailPreference[] = [];
  render(DashboardRailConfiguratorHost, {
    props: { initial, onchange: (pref) => writes.push(pref) },
  });
  return writes;
}

const list = () => page.getByRole("list", { name: "Status pills" });

function rowOrder(): string[] {
  return [...list().element().querySelectorAll("[data-rail-item]")].map(
    (row) => row.getAttribute("data-rail-item") ?? ""
  );
}

describe("DashboardRailConfigurator", () => {
  it("lists every item in one list, ordered items first, with only list items inside it", async () => {
    setup({ order: [Iob, Cob], hidden: [Loop] });

    await expect.element(list()).toBeVisible();
    expect(rowOrder()).toEqual([Iob, Cob, Basal, Loop, Reservoir, Trackers]);
    const children = [...list().element().children];
    expect(children.every((child) => child.getAttribute("role") === "listitem")).toBe(true);
    await expect.element(page.getByRole("switch", { name: "Show Loop status" })).not.toBeChecked();
    await expect.element(page.getByRole("switch", { name: "Show Reservoir" })).toBeChecked();
  });

  it("re-renders when the value changes", async () => {
    const { screen } = setup({});

    expect(rowOrder()[0]).toBe(Cob);
    await screen.rerender({ value: { order: [Trackers] } });

    await expect.poll(() => rowOrder()[0]).toBe(Trackers);
  });

  it("moves an item down and keeps focus on its button", async () => {
    const writes = host({});
    const down = page.getByRole("button", { name: "Move Carbs on board (COB) down" });

    await down.click();

    expect(writes.at(-1)).toEqual({ order: [Basal, Cob, Iob, Loop, Reservoir, Trackers], hidden: [] });
    await expect.poll(() => rowOrder()[1]).toBe(Cob);
    await expect.element(down).toHaveFocus();
  });

  it("moves focus to the other button when the pressed one becomes disabled", async () => {
    host({});
    const up = page.getByRole("button", { name: "Move Basal rate up" });

    await up.click();

    await expect.element(up).toBeDisabled();
    await expect
      .element(page.getByRole("button", { name: "Move Basal rate down" }))
      .toHaveFocus();
  });

  it("reorders by drag and drop", async () => {
    const { writes } = setup({});
    const rows = list().getByRole("listitem");

    await rows.nth(2).dropTo(rows.nth(0));

    expect(writes.at(-1)?.order).toEqual([Iob, Cob, Basal, Loop, Reservoir, Trackers]);
  });

  it("hides and shows an item and keeps focus on its switch", async () => {
    const writes = host({ hidden: [Reservoir] });
    const trackers = page.getByRole("switch", { name: "Show Trackers" });

    await trackers.click();
    expect(writes.at(-1)?.hidden).toEqual([Reservoir, Trackers]);
    await expect.element(trackers).not.toBeChecked();
    await expect.element(trackers).toHaveFocus();

    await page.getByRole("switch", { name: "Show Reservoir" }).click();
    expect(writes.at(-1)?.hidden).toEqual([Trackers]);
  });

  it("shows trackers as unavailable while the tenant turns tracker pills off", async () => {
    setup({}, false);

    await expect.element(page.getByText("The owner turned off tracker pills.")).toBeVisible();
    expect(page.getByRole("switch", { name: "Show Trackers" }).elements()).toHaveLength(0);
  });

  it("resets to the default with an empty preference", async () => {
    const { writes } = setup({ order: [Loop], hidden: [Cob] });

    await page.getByRole("button", { name: "Reset to default" }).click();

    expect(writes.at(-1)).toEqual({});
  });

  it("has nothing to reset while the rail is the default", async () => {
    setup({});

    await expect.element(page.getByRole("button", { name: "Reset to default" })).toBeDisabled();
  });

  it("lists the hidden legacy ages and shows one again", async () => {
    const writes: LegacyConsumableKind[][] = [];
    render(DashboardRailConfigurator, {
      props: {
        value: {},
        trackerPillsEnabled: true,
        hiddenLegacyFallbacks: [LegacyConsumableKind.Site, LegacyConsumableKind.PumpBattery],
        onhiddenlegacyfallbackschange: (kinds) => writes.push(kinds),
      },
    });

    const hidden = page.getByTestId("hidden-legacy-fallbacks");
    await expect.element(hidden.getByText("Infusion site (CAGE)")).toBeVisible();
    await expect.element(hidden.getByText("Pump battery (BAGE)")).toBeVisible();

    await page.getByRole("button", { name: "Show Infusion site (CAGE)" }).click();

    expect(writes).toEqual([[LegacyConsumableKind.PumpBattery]]);
  });

  it("leaves out the hidden legacy ages while none is hidden", async () => {
    setup({});

    await expect.element(list()).toBeVisible();
    expect(page.getByTestId("hidden-legacy-fallbacks").elements()).toHaveLength(0);
  });
});
