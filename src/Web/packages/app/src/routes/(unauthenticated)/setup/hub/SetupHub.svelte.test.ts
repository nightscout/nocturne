import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import type { SetupHubStatus } from "$api";

const hub = vi.hoisted(() => ({ status: undefined as unknown }));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => remoteQuery(() => hub.status),
}));

const tenant = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
  setUnits: vi.fn(),
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => tenant.relationship),
  setPatientRelationship: vi.fn(),
  getUnitsAndTimezone: () =>
    remoteQuery(() => ({ glucoseUnits: "mmol", timezone: "Pacific/Auckland" })),
  setUnitsAndTimezone: tenant.setUnits,
}));
vi.mock("$lib/stores/appearance-store.svelte", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  applyPreferences: vi.fn(),
}));

import { SetupHubItemKey, SetupHubItemState } from "$api";
import SetupHub from "./SetupHub.svelte";

const KEYS = Object.values(SetupHubItemKey);

function hubWith(resolved: number): SetupHubStatus {
  const items = KEYS.map((key, i) => ({
    key,
    state:
      i >= resolved
        ? SetupHubItemState.Open
        : i % 2 === 0
          ? SetupHubItemState.Done
          : SetupHubItemState.NotForMe,
  }));
  return {
    items,
    resolvedCount: resolved,
    openCount: KEYS.length - resolved,
    totalCount: KEYS.length,
    revision: `r${resolved}`,
    showStrip: resolved < KEYS.length,
  };
}

beforeEach(() => {
  tenant.relationship = {};
  tenant.setUnits.mockReset().mockResolvedValue({});
});

describe("SetupHub", () => {
  it("lists every item with where it stands, and counts the resolved ones", async () => {
    hub.status = hubWith(3);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-progress")).toHaveTextContent(/3 of 6 set up/);
    const done = page.getByTestId("hub-item-connect-data");
    await expect.element(done).toHaveAttribute("data-state", "Done");
    await expect.element(done.getByText("Done")).toBeVisible();

    const setAside = page.getByTestId("hub-item-alerts");
    await expect.element(setAside.getByText("Not for me")).toBeVisible();

    await expect.element(page.getByTestId("hub-item-about")).toHaveAttribute("data-state", "Open");
  });

  it("speaks of the patient by name", async () => {
    hub.status = hubWith(0);
    tenant.relationship = { relationship: "Caregiver", patientName: "Sam" };
    render(SetupHub);

    await expect.element(page.getByRole("heading", { name: "Set Nocturne up for Sam." })).toBeVisible();
    await expect
      .element(page.getByText("Choose who else can see Sam's data."))
      .toBeVisible();
    await expect.element(page.getByTestId("basics-who")).toHaveTextContent("Sam, someone you care for");
  });

  it("opens a finished hub collapsed to all set up", async () => {
    hub.status = hubWith(6);
    render(SetupHub);

    await expect.element(page.getByTestId("hub-all-set")).toBeVisible();
    await expect.element(page.getByTestId("hub-header")).not.toBeInTheDocument();
  });

  it("edits the units from Basics, saying which unit therapy settings are entered in", async () => {
    hub.status = hubWith(2);
    render(SetupHub);

    await expect.element(page.getByTestId("basics-units")).toHaveTextContent(/mmol\/L/);
    await expect.element(page.getByText(/are entered in this unit/)).toBeVisible();

    await page.getByRole("button", { name: "Change" }).nth(1).click();
    await page.getByRole("radio", { name: /mg\/dL/ }).click();
    await page.getByRole("button", { name: "Save" }).click();

    expect(tenant.setUnits).toHaveBeenCalledWith({
      glucoseUnits: "mg/dl",
      timezone: "Pacific/Auckland",
    });
  });
});
