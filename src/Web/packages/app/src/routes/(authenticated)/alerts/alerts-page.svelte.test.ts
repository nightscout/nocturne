import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse, TenantAlertSettingsResponse } from "@nocturne/api-client";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { toastError } = vi.hoisted(() => ({ toastError: vi.fn() }));

const lowRule = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Nighttime low",
  isEnabled: true,
  severity: "warning",
  conditionType: "glucose_below",
  conditionParams: {},
  channels: [],
  coversLow: true,
  isLastLowCover: false,
} as unknown as AlertRuleResponse;

let rules: AlertRuleResponse[] = [lowRule];

// The backing values are module-level so each read hands back the same object:
// a resource that answered a fresh array every access would restart any effect
// deriving from it.
const activeAlerts: unknown[] = [];
const alertHistory = { items: [], totalCount: 0 };

// `toggleRule` is the shortest write path on the page (one click, no dialog), so
// it stands in for the server's refusal.
let toggleImpl: () => Promise<unknown>;
let deleteImpl: () => Promise<unknown>;

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => rules),
  deleteRule: remoteCommand(() => deleteImpl()),
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
const newRuleButton = () => page.getByRole("button", { name: "New alert" });
const enableSwitch = () => page.getByRole("switch", { name: "Enable rule" });
const rowActions = () => page.getByRole("button", { name: "Row actions" });

describe("alerts page", () => {
  beforeEach(() => {
    toggleImpl = () => Promise.resolve();
    deleteImpl = () => Promise.resolve();
    rules = [lowRule];
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

  describe("low glucose coverage", () => {
    const noLowAlert = () => page.getByText(/No alert is on for low glucose/);
    const addLowAlert = () => page.getByRole("button", { name: "Add a low alert" });
    const lastCoverDialog = () => page.getByText(/No alert for low glucose will remain/);

    it("warns when no rule covers low glucose, with a way to add one", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = [{ ...lowRule, coversLow: false }];

      render(AlertsPage, {});

      await expect.element(ruleName()).toBeVisible();
      await expect.element(noLowAlert()).toBeVisible();
      await expect.element(addLowAlert()).toBeVisible();
    });

    it("shows no warning while a rule covers low glucose", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };

      render(AlertsPage, {});

      await expect.element(ruleName()).toBeVisible();
      await expect.element(noLowAlert()).not.toBeInTheDocument();
    });

    it("leaves the empty state to say a tenant with no rules has no alert", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = [];

      render(AlertsPage, {});

      await expect.element(page.getByText("No alerts yet")).toBeVisible();
      await expect.element(noLowAlert()).not.toBeInTheDocument();
    });

    it("offers no add action to a member who cannot change alerts", async () => {
      pageState.data = { effectivePermissions: ["alerts.read"] };
      rules = [{ ...lowRule, coversLow: false }];

      render(AlertsPage, {});

      await expect.element(noLowAlert()).toBeVisible();
      await expect.element(addLowAlert()).not.toBeInTheDocument();
    });

    it("asks before turning off the last rule that covers low glucose", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = [{ ...lowRule, isLastLowCover: true }];
      const toggled = vi.fn(() => Promise.resolve());
      toggleImpl = toggled;

      render(AlertsPage, {});

      await expect.element(enableSwitch()).toBeVisible();
      await enableSwitch().click();

      await expect.element(lastCoverDialog()).toBeVisible();
      expect(toggled).not.toHaveBeenCalled();

      await page.getByRole("button", { name: "Disable", exact: true }).click();
      await vi.waitFor(() => expect(toggled).toHaveBeenCalledOnce());
    });

    it("turns off a rule that is not the last cover without asking", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      const toggled = vi.fn(() => Promise.resolve());
      toggleImpl = toggled;

      render(AlertsPage, {});

      await expect.element(enableSwitch()).toBeVisible();
      await enableSwitch().click();

      await vi.waitFor(() => expect(toggled).toHaveBeenCalledOnce());
      await expect.element(lastCoverDialog()).not.toBeInTheDocument();
    });

    it("asks before deleting the last rule that covers low glucose", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = [{ ...lowRule, isLastLowCover: true }];
      const deleted = vi.fn(() => Promise.resolve());
      deleteImpl = deleted;

      render(AlertsPage, {});

      await expect.element(ruleName()).toBeVisible();
      await rowActions().click();
      await page.getByRole("menuitem", { name: /Delete/i }).click();

      await expect.element(lastCoverDialog()).toBeVisible();
      expect(deleted).not.toHaveBeenCalled();
    });

    it("words the delete confirmation without the low warning for any other rule", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };

      render(AlertsPage, {});

      await expect.element(ruleName()).toBeVisible();
      await rowActions().click();
      await page.getByRole("menuitem", { name: /Delete/i }).click();

      await expect.element(page.getByText(/This action cannot be undone/)).toBeVisible();
      await expect.element(lastCoverDialog()).not.toBeInTheDocument();
    });
  });
});
