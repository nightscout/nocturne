import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
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
let testFireImpl: (id: unknown) => Promise<unknown>;

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => rules),
  deleteRule: remoteCommand(() => deleteImpl()),
  toggleRule: remoteCommand(() => toggleImpl()),
  testFire: remoteCommand((id) => testFireImpl(id)),
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
    testFireImpl = () => Promise.resolve({ notSent: [] });
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

  describe("test fire", () => {
    const sendTest = async () => {
      await expect.element(ruleName()).toBeVisible();
      await rowActions().click();
      await page.getByRole("menuitem", { name: /Test fire/i }).click();
      await userEvent.keyboard("{Escape}");
    };

    it("lists each channel that did not get the test alert, with the reason", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = [
        {
          ...lowRule,
          channels: [
            { id: "c0", channelType: "webhook", destinationLabel: "Site A", sortOrder: 0 },
            { id: "c1", channelType: "webhook", destinationLabel: "Site B", sortOrder: 1 },
          ],
        } as unknown as AlertRuleResponse,
      ];
      testFireImpl = () => Promise.resolve({ notSent: [{ index: 1, channelType: "webhook", state: "stopped" }] });

      render(AlertsPage, {});
      await sendTest();

      await expect
        .element(page.getByText("The test alert for Nighttime low did not go to every channel."))
        .toBeVisible();
      await expect.element(page.getByText("Site B did not get the test alert. This channel has stopped.")).toBeVisible();
      expect(page.getByText(/Site A did not get the test alert/).elements()).toHaveLength(0);
    });

    it("says nothing of channels when the test alert went to every one", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      const fired = vi.fn(() => Promise.resolve({ notSent: [] }));
      testFireImpl = fired;

      render(AlertsPage, {});
      await sendTest();

      await vi.waitFor(() => expect(fired).toHaveBeenCalledOnce());
      expect(page.getByText(/did not go to every channel/).elements()).toHaveLength(0);
      expect(toastError).not.toHaveBeenCalled();
    });

    const twoRules = () => [
      {
        ...lowRule,
        id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        name: "Rule A",
        channels: [{ id: "a0", channelType: "webhook", destinationLabel: "Site A", sortOrder: 0 }],
      },
      {
        ...lowRule,
        id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        name: "Rule B",
        channels: [{ id: "b0", channelType: "webhook", destinationLabel: "Site B", sortOrder: 0 }],
      },
    ] as unknown as AlertRuleResponse[];

    it("shows the answer of the latest test alert when an earlier one answers last", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = twoRules();
      const answers = new Map<string, (value: unknown) => void>();
      testFireImpl = (id) => new Promise((resolve) => answers.set(id as string, resolve));
      const stopped = { notSent: [{ index: 0, channelType: "webhook", state: "stopped" }] };

      render(AlertsPage, {});
      await expect.element(page.getByText("Rule A")).toBeVisible();
      for (const row of [0, 1]) {
        await rowActions().nth(row).click();
        await page.getByRole("menuitem", { name: /Test fire/i }).click();
        await userEvent.keyboard("{Escape}");
      }
      await vi.waitFor(() => expect(answers.size).toBe(2));

      answers.get("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")!(stopped);
      await expect
        .element(page.getByText("The test alert for Rule B did not go to every channel."))
        .toBeVisible();

      answers.get("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")!(stopped);
      await new Promise((resolve) => setTimeout(resolve, 100));

      await expect
        .element(page.getByText("The test alert for Rule B did not go to every channel."))
        .toBeVisible();
      expect(page.getByText(/Rule A did not go to every channel/).elements()).toHaveLength(0);
      expect(page.getByText(/Site A did not get the test alert/).elements()).toHaveLength(0);
    });

    it("keeps a row marked as testing until its own test alert answers", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      rules = twoRules();
      const answers = new Map<string, (value: unknown) => void>();
      testFireImpl = (id) => new Promise((resolve) => answers.set(id as string, resolve));

      render(AlertsPage, {});
      await expect.element(page.getByText("Rule A")).toBeVisible();
      for (const row of [0, 1]) {
        await rowActions().nth(row).click();
        await page.getByRole("menuitem", { name: /Test fire/i }).click();
        await userEvent.keyboard("{Escape}");
      }
      await vi.waitFor(() => expect(answers.size).toBe(2));

      answers.get("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")!({ notSent: [] });
      await new Promise((resolve) => setTimeout(resolve, 100));

      await rowActions().first().click();
      await expect.element(page.getByRole("menuitem", { name: /Test fire/i })).toBeDisabled();
    });

    it("drops the list of a rule that is deleted", async () => {
      pageState.data = { effectivePermissions: ["alerts.readwrite"] };
      testFireImpl = () => Promise.resolve({ notSent: [{ index: 0, channelType: "webhook", state: "stopped" }] });

      render(AlertsPage, {});
      await sendTest();
      await expect
        .element(page.getByText("The test alert for Nighttime low did not go to every channel."))
        .toBeVisible();

      await rowActions().click();
      await page.getByRole("menuitem", { name: /Delete/i }).click();
      await page.getByRole("button", { name: "Delete", exact: true }).click();

      await expect
        .element(page.getByText("The test alert for Nighttime low did not go to every channel."))
        .not.toBeInTheDocument();
    });
  });
});
