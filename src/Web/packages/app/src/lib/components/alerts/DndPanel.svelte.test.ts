import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import type { TenantAlertSettingsResponse } from "$api-clients";

// The panel reads DND state through the generated remote functions; `update`
// is swapped per test so a rejection can stand in for the server's 403.
const idle: TenantAlertSettingsResponse = {
  dndManualActive: false,
  dndScheduleEnabled: false,
  dndScheduleActiveNow: false,
};
let settings: TenantAlertSettingsResponse = idle;

let updateImpl: () => Promise<TenantAlertSettingsResponse>;

vi.mock("$api/generated/tenantAlertSettings.generated.remote", () => ({
  get: () => ({ run: () => Promise.resolve(settings) }),
  update: () => updateImpl(),
}));

import DndPanel from "./DndPanel.svelte";

const dndToggle = () => page.getByRole("button", { name: /Do not disturb/i });

describe("DndPanel", () => {
  beforeEach(() => {
    settings = idle;
    updateImpl = () => Promise.resolve(settings);
    pageState.data = {};
  });

  it("renders nothing for a member without alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.read"] };

    render(DndPanel, {});

    await expect.element(dndToggle()).not.toBeInTheDocument();
  });

  it("renders for a member holding alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };

    render(DndPanel, {});

    await expect.element(dndToggle()).toBeVisible();
  });

  // The Owner resolves to the raw superuser set rather than the expanded scopes,
  // so the wildcard arm is the only thing keeping this control for them.
  it("renders for an owner holding the wildcard scope", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(DndPanel, {});

    await expect.element(dndToggle()).toBeVisible();
  });

  it("says the schedule is on now while its window is in effect", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    settings = { ...idle, dndScheduleEnabled: true, dndScheduleActiveNow: true };

    render(DndPanel, {});

    await expect.element(page.getByText("Scheduled, on now")).toBeVisible();
  });

  it("says only Scheduled while the window is configured but not in effect", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    settings = { ...idle, dndScheduleEnabled: true };

    render(DndPanel, {});

    await expect.element(page.getByText("Scheduled", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Scheduled, on now")).not.toBeInTheDocument();
  });

  it("surfaces the server's reason when the update is rejected", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    updateImpl = async () => error(409, "A scheduled quiet period is running.");

    render(DndPanel, {});

    await dndToggle().click();
    await page.getByRole("menuitem", { name: "30 minutes" }).click();

    await expect
      .element(page.getByText("A scheduled quiet period is running."))
      .toBeVisible();
  });
});
