import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse } from "#lib/api/generated/nocturne-api-client.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const RULE_ID = "11111111-1111-1111-1111-111111111111";

// Module-level so every read hands back the same object; a fresh value per access
// would re-run the effects that derive from it.
const rule = {
  id: RULE_ID,
  name: "Nighttime low",
  isEnabled: true,
  severity: "warning",
  conditionType: "threshold",
  conditionParams: { direction: "below", value: 70 },
  channels: [],
} as unknown as AlertRuleResponse;
const rules = [rule];
const history = { items: [], totalCount: 0 };

const refusal = vi.hoisted(() => ({
  error: new Error("network down") as unknown,
  /** When set, the test alert is sent instead of refused. */
  testSends: false,
  search: null as unknown,
}));

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => rules),
  getRule: () => remoteQuery(() => rule),
  updateRule: remoteCommand(() => Promise.reject(refusal.error)),
  deleteRule: remoteCommand(() => Promise.reject(refusal.error)),
  testFire: remoteCommand(() =>
    refusal.testSends ? Promise.resolve({ success: true }) : Promise.reject(refusal.error),
  ),
  getInAppRecipients: () => remoteQuery(() => []),
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

describe("alert rule editor", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    refusal.error = new Error("network down");
    refusal.testSends = false;
    refusal.search = null;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("asks the user to try again when deleting the rule fails", async () => {
    openRule(RULE_ID);
    vi.spyOn(window, "confirm").mockReturnValue(true);

    render(RuleEditorPage, {});

    await expect.element(page.getByRole("heading", { name: "Nighttime low" })).toBeVisible();
    await page.getByRole("button", { name: "Delete" }).click();

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
});
