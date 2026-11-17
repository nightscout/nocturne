import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

let insulinsCurrent: unknown[] = [];

vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => remoteQuery(() => insulinsCurrent),
}));

import BasalInjectionSection from "./BasalInjectionSection.svelte";

describe("BasalInjectionSection", () => {
  beforeEach(() => {
    insulinsCurrent = [];
  });

  it("labels the insulin picker and prompts for a choice", async () => {
    render(BasalInjectionSection, { injection: {} });

    await expect.element(page.getByText("Long-acting injection")).toBeVisible();
    await expect.element(page.getByText("Basal insulin")).toBeVisible();
    await expect.element(page.getByText("Select insulin...")).toBeVisible();
  });

  it("offers only current basal or premix insulins", async () => {
    insulinsCurrent = [
      { id: "a", name: "Tresiba", role: "Basal", isCurrent: true, insulinCategory: "UltraLongActing" },
      { id: "b", name: "Humalog Mix", role: "Both", isCurrent: true, insulinCategory: "Premix" },
      { id: "c", name: "Fiasp", role: "Bolus", isCurrent: true, insulinCategory: "RapidActing" },
      { id: "d", name: "Lantus", role: "Basal", isCurrent: false, insulinCategory: "LongActing" },
    ];
    render(BasalInjectionSection, { injection: {} });

    await page.getByText("Select insulin...").click();

    await expect.element(page.getByRole("option", { name: /Tresiba/ })).toBeVisible();
    await expect.element(page.getByRole("option", { name: /Humalog Mix/ })).toBeVisible();
    await expect.element(page.getByRole("option", { name: /Fiasp/ })).not.toBeInTheDocument();
    await expect.element(page.getByRole("option", { name: /Lantus/ })).not.toBeInTheDocument();
  });

  it("warns that a premix insulin needs only its basal portion logged", async () => {
    insulinsCurrent = [{ id: "b", name: "Humalog Mix", role: "Both", isCurrent: true }];
    render(BasalInjectionSection, { injection: { insulinContext: { patientInsulinId: "b" } } });

    await expect.element(page.getByText(/Log the basal\s+portion only/)).toBeVisible();
  });

  it("asks to confirm a dose above 100 U", async () => {
    render(BasalInjectionSection, { injection: { units: 150 } });

    await expect.element(page.getByText("Confirm the dose. This dose is higher than typical.")).toBeVisible();
  });
});
