import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { TrackerCategory } from "#lib/api/generated/nocturne-api-client.js";
import { remoteForm, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import WithTooltipProvider from "#lib/test-stubs/WithTooltipProvider.test-stub.svelte";

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getTriggerEventTypes: () => remoteQuery(() => ["Pod Change", "Pod Activated", "Site Change"]),
  getEndEventTypes: () => remoteQuery(() => ["Sensor Stop", "Pod Deactivated"]),
  createDefinition: remoteForm(),
  updateDefinition: remoteForm(),
}));

import TrackerEditorDialog from "./TrackerEditorDialog.svelte";

const categoryLabels = Object.fromEntries(
  Object.values(TrackerCategory).map((c) => [c, c])
) as Record<TrackerCategory, string>;

function open(formCategory: TrackerCategory) {
  render(WithTooltipProvider, {
    props: {
      component: TrackerEditorDialog,
      componentProps: {
        open: true,
        isNewDefinition: true,
        editingDefinition: null,
        formCategory,
        categoryLabels,
      },
    },
  });
}

const postedCovers = () =>
  [...document.querySelectorAll<HTMLInputElement>('input[name^="covers["]')].map((i) => i.value);

describe("TrackerEditorDialog covers", () => {
  it("offers what a pump part replaces, and a low reservoir alert once it replaces the insulin", async () => {
    open(TrackerCategory.Consumable);

    await expect.element(page.getByTestId("tracker-covers")).toBeVisible();
    expect(postedCovers()).toEqual([""]);
    expect(document.getElementById("lowReservoirUnits")).toBeNull();

    await page.getByRole("checkbox", { name: "Insulin" }).click();

    await expect.element(page.getByLabelText("Low reservoir alert (units)")).toBeVisible();
    expect(postedCovers()).toEqual(["Reservoir"]);
  });

  it("ticks the site, the insulin and the battery when the category becomes Pod", async () => {
    open(TrackerCategory.Consumable);

    await page.getByTestId("tracker-category-trigger").click();
    await page.getByRole("option", { name: "Pod", exact: true }).click();

    await expect.element(page.getByRole("checkbox", { name: "Infusion site" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Insulin" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Pump battery" })).toBeChecked();
    expect(postedCovers()).toEqual(["Site", "Reservoir", "Power"]);
  });

  it("does not offer it for a sensor", async () => {
    open(TrackerCategory.Sensor);

    await expect.element(page.getByTestId("tracker-ends")).toBeVisible();
    expect(document.querySelector('[data-testid="tracker-covers"]')).toBeNull();
    expect(postedCovers()).toEqual([""]);
  });
});
