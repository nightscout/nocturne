import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { TenantAlertSettingsResponse } from "#lib/api/generated/nocturne-api-client.js";

// The page seeds its form from the generated remote query; the mock resolves
// synchronously so `current` is populated on first render.
let settings: TenantAlertSettingsResponse = {
  dndManualActive: false,
  dndScheduleEnabled: false,
} as TenantAlertSettingsResponse;

let updateImpl: () => Promise<TenantAlertSettingsResponse>;

vi.mock("#lib/api/generated/tenantAlertSettings.generated.remote.js", () => ({
  get: () => ({ current: settings }),
  update: () => updateImpl(),
}));

import DndPage from "./+page.svelte";

const saveButton = () => page.getByRole("button", { name: "Save" });
const accessDenied = () => page.getByText("Access denied");

describe("alerts/dnd page", () => {
  beforeEach(() => {
    pageState.data = {};
    settings = { dndManualActive: false, dndScheduleEnabled: false } as TenantAlertSettingsResponse;
    updateImpl = () => Promise.resolve(settings);
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
      .element(page.getByText("Do not disturb is currently"))
      .toBeVisible();
    await expect.element(accessDenied()).not.toBeInTheDocument();
  });

  it("says the schedule is on now while its window is in effect", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    settings = { dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: true };

    render(DndPage, {});

    await expect.element(page.getByText("Scheduled, on now")).toBeVisible();
  });

  it("says only Scheduled while the window is configured but not in effect", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    settings = { dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: false };

    render(DndPage, {});

    await expect.element(page.getByText("Scheduled", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Scheduled, on now")).not.toBeInTheDocument();
  });

  it("shows no schedule status when none is configured", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };

    render(DndPage, {});

    await expect.element(saveButton()).toBeVisible();
    await expect.element(page.getByText("Scheduled", { exact: true })).not.toBeInTheDocument();
  });

  it("shows the server's reason when the save is refused", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    // A rejected generated remote function is SvelteKit's `HttpError`: a plain
    // `{ status, body }` with no `Error` in its prototype chain.
    updateImpl = () =>
      Promise.reject({
        status: 400,
        body: { message: "The schedule must end after it starts." },
      });

    render(DndPage, {});
    await saveButton().click();

    await expect
      .element(page.getByText("The schedule must end after it starts."))
      .toBeVisible();
  });

  it("keeps a server fault behind the page's own wording", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    updateImpl = () =>
      Promise.reject({
        status: 500,
        body: { message: "npgsql: connection reset" },
      });

    render(DndPage, {});
    await saveButton().click();

    await expect
      .element(page.getByText("Failed to save DND settings"))
      .toBeVisible();
    await expect
      .element(page.getByText("npgsql: connection reset"))
      .not.toBeInTheDocument();
  });
});
