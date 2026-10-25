import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse } from "$api-clients";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";

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

const refusal = vi.hoisted(() => ({ error: new Error("network down") as unknown }));

vi.mock("$api/generated/alertRules.generated.remote", () => ({
  getRules: () => remoteQuery(() => rules),
  getRule: () => remoteQuery(() => rule),
  createRule: remoteCommand(() => Promise.reject(refusal.error)),
  updateRule: remoteCommand(() => Promise.reject(refusal.error)),
  deleteRule: remoteCommand(() => Promise.reject(refusal.error)),
  testFire: remoteCommand(() => Promise.reject(refusal.error)),
  getInAppRecipients: () => remoteQuery(() => []),
}));

vi.mock("$api/generated/alerts.generated.remote", () => ({
  getAlertHistory: () => remoteQuery(() => history),
}));

vi.mock("$api/generated/alertReplays.generated.remote", () => ({
  replay: remoteCommand(() => Promise.reject(refusal.error)),
  replayDryRun: remoteCommand(() => Promise.reject(refusal.error)),
}));

vi.mock("$api/generated/linkedPlatforms.generated.remote", () => ({
  getLinkedPlatforms: () => remoteQuery(() => ({ platforms: [] })),
}));

vi.mock("$api/generated/systems.generated.remote", () => ({
  getChannelStatuses: () => remoteQuery(() => ({ channels: [] })),
}));

vi.mock("$api/generated/clientDevices.generated.remote", () => ({
  getCapabilityCatalog: () => remoteQuery(() => ({ capabilities: [] })),
}));

vi.mock("$api/generated/trackers.generated.remote", () => ({
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
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("asks the user to try again when creating the rule fails", async () => {
    openRule("new");

    render(RuleEditorPage, {});

    await page.getByRole("button", { name: "Create" }).click();

    await expect
      .element(page.getByText("Failed to save the alert rule. Try again."))
      .toBeVisible();
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

    const fire = page.getByRole("button", { name: "Fire saved rule" });
    await expect.element(fire).toBeEnabled();
    await fire.click();

    await expect
      .element(page.getByText("Failed to send a test alert. Try again."))
      .toBeVisible();
  });
});
