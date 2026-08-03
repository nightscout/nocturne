import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { TenantAlertSettingsResponse } from "$api-clients";

// The page seeds its form from the generated remote query; the mock resolves
// synchronously so `current` is populated on first render.
const settings: TenantAlertSettingsResponse = {
  dndManualActive: false,
  dndScheduleEnabled: false,
} as TenantAlertSettingsResponse;

vi.mock("$api/generated/tenantAlertSettings.generated.remote", () => ({
  get: () => ({ current: settings }),
  update: () => Promise.resolve(settings),
}));

import DndPage from "./+page.svelte";

const saveButton = () => page.getByRole("button", { name: "Save" });
const accessDenied = () => page.getByText("Access Denied");

describe("alerts/dnd page", () => {
  beforeEach(() => {
    pageState.data = {};
  });

  it("shows the access-denied card for a member without alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.read"] };

    render(DndPage, {});

    await expect.element(accessDenied()).toBeVisible();
    await expect.element(saveButton()).not.toBeInTheDocument();
  });

  it("shows the settings form for a member holding alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };

    render(DndPage, {});

    await expect.element(saveButton()).toBeVisible();
    await expect
      .element(page.getByText("Do Not Disturb is currently"))
      .toBeVisible();
    await expect.element(accessDenied()).not.toBeInTheDocument();
  });
});
