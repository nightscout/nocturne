import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse, TenantAlertSettingsResponse } from "#lib/api/generated/nocturne-api-client.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { toastError } = vi.hoisted(() => ({ toastError: vi.fn() }));

const rules = [
  {
    id: "11111111-1111-1111-1111-111111111111",
    name: "Nighttime low",
    isEnabled: true,
    severity: "warning",
    conditionType: "glucose_below",
    conditionParams: {},
    channels: [],
  },
] as unknown as AlertRuleResponse[];

// The backing values are module-level so each read hands back the same object:
// a resource that answered a fresh array every access would restart any effect
// deriving from it.
const activeAlerts: unknown[] = [];
const alertHistory = { items: [], totalCount: 0 };

// `toggleRule` is the shortest write path on the page (one click, no dialog), so
// it stands in for the server's refusal.
let toggleImpl: () => Promise<unknown>;

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => rules),
  deleteRule: remoteCommand(() => undefined),
  toggleRule: remoteCommand(() => toggleImpl()),
  testFire: remoteCommand(() => undefined),
}));

vi.mock("#lib/api/generated/alerts.generated.remote.js", () => ({
  getActiveAlerts: () => remoteQuery(() => activeAlerts),
  getAlertHistory: () => remoteQuery(() => alertHistory),
  acknowledge: remoteCommand(() => undefined),
}));

let dndSettings: TenantAlertSettingsResponse | null = null;

vi.mock("#lib/api/generated/tenantAlertSettings.generated.remote.js", () => ({
  get: () => remoteQuery(() => dndSettings),
  update: remoteCommand(() => undefined),
}));

vi.mock("svelte-sonner", () => ({ toast: { error: toastError } }));

import AlertsPage from "./+page.svelte";

const ruleName = () => page.getByText("Nighttime low");
const newRuleButton = () => page.getByRole("button", { name: "New rule" });
const enableSwitch = () => page.getByRole("switch", { name: "Enable rule" });
const rowActions = () => page.getByRole("button", { name: "Row actions" });

describe("alerts page", () => {
  beforeEach(() => {
    toggleImpl = () => Promise.resolve();
    dndSettings = null;
    toastError.mockClear();
    pageState.data = {};
  });

  it("shows the Do not disturb notice for a schedule in effect, without a way to turn it off", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    dndSettings = {
      dndManualActive: false,
      dndScheduleEnabled: true,
      dndScheduleActiveNow: true,
    };

    render(AlertsPage, {});

    await expect.element(page.getByText(/Do not disturb is on/)).toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Disable" }))
      .not.toBeInTheDocument();
  });

  it("offers to turn off a manual mute from the Do not disturb notice", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    dndSettings = { dndManualActive: true, dndScheduleEnabled: false };

    render(AlertsPage, {});

    await expect.element(page.getByText(/Do not disturb is on/)).toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Disable" }))
      .toBeVisible();
  });

  it("shows no Do not disturb notice for a schedule that is not in effect", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    dndSettings = { dndManualActive: false, dndScheduleEnabled: true, dndScheduleActiveNow: false };

    render(AlertsPage, {});

    await expect.element(ruleName()).toBeVisible();
    await expect
      .element(page.getByText(/Do not disturb is on/))
      .not.toBeInTheDocument();
  });

  it("hides the write controls from a member without alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.read"] };

    render(AlertsPage, {});

    // Anchor on the loaded list first: the boundary's pending snippet would
    // otherwise satisfy every absence assertion below.
    await expect.element(ruleName()).toBeVisible();
    await expect.element(newRuleButton()).not.toBeInTheDocument();
    await expect.element(enableSwitch()).not.toBeInTheDocument();

    await rowActions().click();
    await expect
      .element(page.getByRole("menuitem", { name: /Test fire/i }))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByRole("menuitem", { name: /Delete/i }))
      .not.toBeInTheDocument();
  });

  it("shows the write controls to a member holding alerts.readwrite", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };

    render(AlertsPage, {});

    await expect.element(ruleName()).toBeVisible();
    await expect.element(newRuleButton()).toBeVisible();
    await expect.element(enableSwitch()).toBeVisible();
  });

  it("surfaces a refused toggle instead of discarding it", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    toggleImpl = async () => error(403, "Forbidden");

    render(AlertsPage, {});

    await expect.element(enableSwitch()).toBeVisible();
    await enableSwitch().click();

    await vi.waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "Changing alerts requires the alerts.readwrite permission."
      )
    );
  });

  it("surfaces a stale rule as gone rather than as a permission problem", async () => {
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    toggleImpl = async () => error(404, "Not Found");

    render(AlertsPage, {});

    await expect.element(enableSwitch()).toBeVisible();
    await enableSwitch().click();

    await vi.waitFor(() =>
      expect(toastError).toHaveBeenCalledWith(
        "That item no longer exists. Refresh the page to see what's there now."
      )
    );
  });
});
