import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse } from "@nocturne/api-client";
import { ALERT_SETTING_OPTIONS } from "#lib/test-stubs/alert-setting-options.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const RULE_ID = "11111111-1111-1111-1111-111111111111";

// Module-level so every read hands back the same object; a fresh value per access
// would re-run the effects that derive from it.
let rule = {
  id: RULE_ID,
  name: "Nighttime low",
  isEnabled: true,
  severity: "warning",
  conditionType: "threshold",
  conditionParams: { direction: "below", value: 70 },
  channels: [],
} as unknown as AlertRuleResponse;
const lastCover = { ...rule, coversLow: true, isLastLowCover: true } as unknown as AlertRuleResponse;
const rules = [rule];
const settingOptions = ALERT_SETTING_OPTIONS;
const history = { items: [], totalCount: 0 };

const refusal = vi.hoisted(() => ({
  error: new Error("network down") as unknown,
  /** When set, the test alert is sent instead of refused. */
  testSends: false,
  /** The channels the answer says the test alert did not go to. */
  notSent: [] as unknown[],
  search: null as unknown,
  update: (): Promise<unknown> => Promise.reject(refusal.error),
  remove: (): Promise<unknown> => Promise.reject(refusal.error),
}));

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => rules),
  getRule: () => remoteQuery(() => rule),
  getSettingOptions: () => remoteQuery(() => settingOptions),
  updateRule: remoteCommand(() => refusal.update()),
  deleteRule: remoteCommand(() => refusal.remove()),
  testFire: remoteCommand(() =>
    refusal.testSends ? Promise.resolve({ notSent: refusal.notSent }) : Promise.reject(refusal.error),
  ),
  getInAppRecipients: () => remoteQuery(() => []),
  keepChannel: remoteCommand(() => Promise.reject(new Error("not under test"))),
  suggestAutoResolve: remoteCommand(() => Promise.resolve({ condition: null })),
}));

vi.mock("#lib/api/generated/alerts.generated.remote.js", () => ({
  getAlertHistory: () => remoteQuery(() => history),
}));

vi.mock("#lib/api/generated/alertReplays.generated.remote.js", () => ({
  replay: remoteCommand(() => Promise.reject(refusal.error)),
  replayDryRun: remoteCommand(() => Promise.reject(refusal.error)),
  findFirings: remoteCommand(() =>
    refusal.search ? Promise.resolve(refusal.search) : Promise.reject(refusal.error),
  ),
}));

vi.mock("#lib/api/generated/linkedPlatforms.generated.remote.js", () => ({
  getLinkedPlatforms: () => remoteQuery(() => ({ platforms: [] })),
}));

vi.mock("#lib/api/generated/systems.generated.remote.js", () => ({
  getChannelStatuses: () => remoteQuery(() => ({ channels: [] })),
}));

vi.mock("#lib/api/generated/clientDevices.generated.remote.js", () => ({
  getCapabilityCatalog: () => remoteQuery(() => ({ capabilities: [] })),
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getDefinitions: () => remoteQuery(() => []),
}));

import RuleEditorPage from "./+page.svelte";

function openRule(id: string) {
  (pageState.params as Record<string, string>).id = id;
  pageState.data = { effectivePermissions: ["alerts.readwrite"] };
}

const original = rule;

describe("alert rule editor", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    refusal.error = new Error("network down");
    refusal.testSends = false;
    refusal.notSent = [];
    refusal.search = null;
    refusal.update = () => Promise.reject(refusal.error);
    refusal.remove = () => Promise.reject(refusal.error);
    rule = original;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("asks the user to try again when deleting the rule fails", async () => {
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    await expect.element(page.getByRole("heading", { name: "Nighttime low" })).toBeVisible();
    await page.getByRole("button", { name: "Delete" }).click();
    await page.getByRole("button", { name: "Delete", exact: true }).nth(1).click();

    await expect
      .element(page.getByText("Failed to delete the alert rule. Try again."))
      .toBeVisible();
  });

  it("asks the user to try again when the test alert cannot be sent", async () => {
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    const fire = page.getByRole("button", { name: "Send a test alert" });
    await expect.element(fire).toBeEnabled();
    await fire.click();

    await expect
      .element(page.getByText("Failed to send a test alert. Try again."))
      .toBeVisible();
  });

  it("says that the test alert was sent", async () => {
    refusal.testSends = true;
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    await page.getByRole("button", { name: "Send a test alert" }).click();

    await expect.element(page.getByText("Test alert sent. Make sure that it arrived.")).toBeVisible();
    expect(page.getByText("The test alert did not go to every channel.").elements()).toHaveLength(0);
  });

  it("lists each channel that did not get the test alert, with the reason", async () => {
    refusal.testSends = true;
    refusal.notSent = [{ index: 1, channelType: "webhook", state: "stopped" }];
    rule = {
      ...original,
      channels: [
        { id: "c0", channelType: "webhook", destination: "https://example.test/a", destinationLabel: "Site A", sortOrder: 0 },
        { id: "c1", channelType: "webhook", destination: "https://example.test/b", destinationLabel: "Site B", sortOrder: 1 },
      ],
    } as unknown as AlertRuleResponse;
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    await page.getByRole("button", { name: "Send a test alert" }).click();

    await expect.element(page.getByText("The test alert did not go to every channel.")).toBeVisible();
    await expect.element(page.getByText("Site B did not get the test alert. This channel has stopped.")).toBeVisible();
    expect(page.getByText(/Site A did not get the test alert/).elements()).toHaveLength(0);
    expect(page.getByText("Test alert sent. Make sure that it arrived.").elements()).toHaveLength(0);
  });

  it("says how many times the alert would have started in the recent past", async () => {
    refusal.search = {
      searchedFrom: "2026-04-14T12:00:00Z",
      searchedTo: "2026-04-28T12:00:00Z",
      hasReadings: true,
      total: 2,
      firings: [
        { at: "2026-04-28T03:20:00Z", kind: "fired" },
        { at: "2026-04-26T02:05:00Z", kind: "fired" },
      ],
    };
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    await expect
      .element(page.getByTestId("alert-history-count"))
      .toHaveTextContent("This alert would have started 2 times in the last 14 days.");
  });

  // A seeded condition replaced the engine's own rule (extend only while glucose moves back
  // toward range) with one the person did not choose: a low alert kept snoozing while falling.
  it("switches smart snooze on with no condition of its own", async () => {
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    await page.getByRole("switch", { name: "Enable smart snooze" }).click();

    const card = page.getByTestId("alert-smart-snooze-card");
    await expect.element(card.getByTestId("smart-snooze-default")).toBeVisible();
    await expect.element(card.getByTestId("alert-condition-row")).not.toBeInTheDocument();
  });

  it("names the comparison of a condition in words", async () => {
    openRule(RULE_ID);

    render(RuleEditorPage, {});

    const row = page.getByTestId("alert-condition-card").getByTestId("alert-condition-row");
    await expect.element(row.getByRole("button", { name: "Direction" })).toHaveTextContent("below");
  });

  describe("the last rule that covers low glucose", () => {
    const warning = () => page.getByText(/No alert for low glucose will remain/);

    it("asks before deleting it", async () => {
      rule = lastCover;
      const removed = vi.fn(() => Promise.resolve());
      refusal.remove = removed;
      openRule(RULE_ID);

      render(RuleEditorPage, {});

      await expect.element(page.getByRole("heading", { name: "Nighttime low" })).toBeVisible();
      await page.getByRole("button", { name: "Delete" }).click();

      await expect.element(warning()).toBeVisible();
      expect(removed).not.toHaveBeenCalled();

      await page.getByRole("button", { name: "Delete", exact: true }).nth(1).click();
      await vi.waitFor(() => expect(removed).toHaveBeenCalledOnce());
    });

    it("asks before saving it turned off", async () => {
      rule = lastCover;
      const saved = vi.fn(() => Promise.resolve());
      refusal.update = saved;
      openRule(RULE_ID);

      render(RuleEditorPage, {});

      const enabled = page.getByTestId("alert-enabled");
      await expect.element(enabled).toBeVisible();
      await enabled.click();
      await page.getByRole("button", { name: "Save" }).click();

      await expect.element(warning()).toBeVisible();
      expect(saved).not.toHaveBeenCalled();

      await page.getByRole("button", { name: "Disable", exact: true }).click();
      await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    });

    it("saves other changes to it without asking", async () => {
      rule = lastCover;
      const saved = vi.fn(() => Promise.resolve());
      refusal.update = saved;
      openRule(RULE_ID);

      render(RuleEditorPage, {});

      const name = page.getByLabelText("Name");
      await expect.element(name).toBeVisible();
      await name.fill("Night low");
      await page.getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
      await expect.element(warning()).not.toBeInTheDocument();
    });

    it("turns off a rule that is not the last cover without asking", async () => {
      const saved = vi.fn(() => Promise.resolve());
      refusal.update = saved;
      openRule(RULE_ID);

      render(RuleEditorPage, {});

      const enabled = page.getByTestId("alert-enabled");
      await expect.element(enabled).toBeVisible();
      await enabled.click();
      await page.getByRole("button", { name: "Save" }).click();

      await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
      await expect.element(warning()).not.toBeInTheDocument();
    });
  });
});
