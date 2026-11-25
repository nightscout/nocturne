import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse } from "@nocturne/api-client";
import { ALERT_SETTING_OPTIONS } from "#lib/test-stubs/alert-setting-options.js";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const RULE_ID = "11111111-1111-1111-1111-111111111111";

const stored = {
  id: RULE_ID,
  name: "Nighttime low",
  isEnabled: true,
  severity: "warning",
  conditionType: "threshold",
  conditionParams: { direction: "below", value: 70 },
  channels: [],
  onUnknown: "not_met",
  glucoseFreshnessMinutes: 20,
} as unknown as AlertRuleResponse;

const state = vi.hoisted(() => ({
  rule: null as unknown,
  options: null as unknown,
  optionsFail: false,
  replayed: [] as unknown[],
  update: (_request: unknown): Promise<unknown> => Promise.resolve(),
}));

vi.mock("#lib/api/generated/alertRules.generated.remote.js", () => ({
  getRules: () => remoteQuery(() => [state.rule]),
  getRule: () => remoteQuery(() => state.rule),
  getSettingOptions: () =>
    state.optionsFail
      ? { current: undefined, error: new Error("down"), loading: false, ready: false }
      : remoteQuery(() => state.options),
  updateRule: remoteCommand((request: unknown) => state.update(request)),
  deleteRule: remoteCommand(() => Promise.resolve()),
  testFire: remoteCommand(() => Promise.resolve({ notSent: [] })),
  getInAppRecipients: () => remoteQuery(() => []),
  keepChannel: remoteCommand(() => Promise.reject(new Error("not under test"))),
  suggestAutoResolve: remoteCommand(() => Promise.resolve({ condition: null })),
}));

vi.mock("#lib/api/generated/alerts.generated.remote.js", () => ({
  getAlertHistory: () => remoteQuery(() => ({ items: [], totalCount: 0 })),
}));

vi.mock("#lib/api/generated/alertReplays.generated.remote.js", () => ({
  replay: remoteCommand(() => Promise.resolve({})),
  replayDryRun: remoteCommand(() => Promise.resolve({})),
  findFirings: remoteCommand((request: unknown) => {
    state.replayed.push(request);
    return Promise.resolve({});
  }),
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

type Sent = { request: Record<string, unknown> };

const bound = () => page.getByLabelText("Minutes until a glucose reading counts as missing");

type ReplayedRule = { onUnknown?: string; glucoseFreshnessMinutes?: number };
const lastReplayed = () => (state.replayed.at(-1) as { rule: ReplayedRule }).rule;

function sentRequest(saved: ReturnType<typeof vi.fn>): Record<string, unknown> {
  return (saved.mock.calls[0][0] as Sent).request;
}

async function openEditor() {
  (pageState.params as Record<string, string>).id = RULE_ID;
  pageState.data = { effectivePermissions: ["alerts.readwrite"] };
  render(RuleEditorPage, {});
  await expect.element(page.getByRole("heading", { name: "Nighttime low" })).toBeVisible();
}

async function openAdvanced() {
  await openEditor();
  await page.getByRole("button", { name: "Advanced" }).click();
}

describe("alert rule editor, Advanced section", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    state.rule = stored;
    state.options = ALERT_SETTING_OPTIONS;
    state.optionsFail = false;
    state.replayed.length = 0;
    state.update = () => Promise.resolve();
  });

  it("is closed until it is opened", async () => {
    await openEditor();

    await expect.element(page.getByTestId("alert-on-unknown-met")).not.toBeVisible();
    await page.getByRole("button", { name: "Advanced" }).click();
    await expect.element(page.getByTestId("alert-on-unknown-met")).toBeVisible();
  });

  it("shows the stored choice and bound, with the default marked", async () => {
    await openAdvanced();

    await expect.element(page.getByRole("radio", { name: /Count the condition as false/ })).toBeChecked();
    await expect.element(page.getByTestId("alert-on-unknown-hold")).toHaveTextContent("Wait (default)");
    await expect.element(bound()).toHaveValue(20);
    await expect.element(page.getByText(/The default is 11 minutes\./)).toBeVisible();
  });

  it("warns about slow data sources for a choice other than the default only", async () => {
    state.rule = { ...stored, onUnknown: "hold" };
    await openAdvanced();

    await expect.element(page.getByTestId("alert-on-unknown-caution")).not.toBeInTheDocument();
    await page.getByTestId("alert-on-unknown-met").click();
    await expect.element(page.getByTestId("alert-on-unknown-caution")).toBeVisible();
  });

  it("saves the stored choice and bound when only another field changed", async () => {
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openEditor();

    await page.getByLabelText("Name").fill("Night low");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved)).toMatchObject({
      name: "Night low",
      onUnknown: "not_met",
      glucoseFreshnessMinutes: 20,
    });
    expect(sentRequest(saved).clearGlucoseFreshness).toBeUndefined();
  });

  it("saves a rule with no bound of its own without clearing or setting one", async () => {
    state.rule = { ...stored, glucoseFreshnessMinutes: null };
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openEditor();

    await page.getByLabelText("Name").fill("Night low");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved).glucoseFreshnessMinutes ?? null).toBeNull();
    expect(sentRequest(saved).clearGlucoseFreshness).toBeUndefined();
  });

  it("saves a changed choice and bound", async () => {
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await page.getByTestId("alert-on-unknown-met").click();
    await bound().fill("30");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved)).toMatchObject({ onUnknown: "met", glucoseFreshnessMinutes: 30 });
  });

  it("returns the bound to the default on request", async () => {
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await page.getByRole("button", { name: "Use the default" }).click();
    await expect.element(bound()).toHaveValue(null);
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved).clearGlucoseFreshness).toBe(true);
    expect(sentRequest(saved).glucoseFreshnessMinutes ?? null).toBeNull();
  });

  it("describes each choice with its own sentence", async () => {
    await openAdvanced();

    const hold = page.getByTestId("alert-on-unknown-hold");
    const met = page.getByTestId("alert-on-unknown-met");
    const notMet = page.getByTestId("alert-on-unknown-not_met");
    await expect.element(hold).toHaveTextContent("does not close an open alert");
    await expect.element(hold).not.toHaveTextContent("counts its full condition");
    await expect.element(met).toHaveTextContent("counts its full condition as true");
    await expect.element(met).toHaveTextContent("does not delay this alert");
    await expect.element(met).not.toHaveTextContent("as false");
    await expect.element(notMet).toHaveTextContent("counts its full condition as false");
    await expect.element(notMet).toHaveTextContent("An open alert closes");
    await expect.element(notMet).not.toHaveTextContent("as true");
  });

  it("clears a stored bound when the field is emptied", async () => {
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await bound().fill("");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved).clearGlucoseFreshness).toBe(true);
    expect(sentRequest(saved).glucoseFreshnessMinutes ?? null).toBeNull();
  });

  it("offers to use the default only when there is a bound to clear", async () => {
    state.rule = { ...stored, glucoseFreshnessMinutes: null };
    await openAdvanced();

    const useDefault = page.getByRole("button", { name: "Use the default" });
    await expect.element(useDefault).toBeDisabled();
    await bound().fill("30");
    await expect.element(useDefault).toBeEnabled();
    await useDefault.click();
    await expect.element(useDefault).toBeDisabled();
  });

  it("saves an emptied field on a rule with no bound without clearing anything", async () => {
    state.rule = { ...stored, glucoseFreshnessMinutes: null };
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await bound().fill("30");
    await bound().fill("");
    await page.getByLabelText("Name").fill("Night low");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved).clearGlucoseFreshness).toBeUndefined();
    expect(sentRequest(saved).glucoseFreshnessMinutes ?? null).toBeNull();
  });

  it("replays with the stored choice and bound", async () => {
    await openEditor();

    await vi.waitFor(() => expect(state.replayed.length).toBeGreaterThan(0));
    expect(lastReplayed()).toMatchObject({ onUnknown: "not_met", glucoseFreshnessMinutes: 20 });
  });

  it("replays with a choice and bound that are edited and not saved", async () => {
    await openAdvanced();
    await page.getByTestId("alert-on-unknown-met").click();
    await bound().fill("30");

    await vi.waitFor(() => expect(lastReplayed()).toMatchObject({ onUnknown: "met", glucoseFreshnessMinutes: 30 }));
  });

  it("replays with the default bound sent out, because a replay has no clear flag", async () => {
    await openAdvanced();
    await page.getByRole("button", { name: "Use the default" }).click();

    await vi.waitFor(() => expect(lastReplayed().glucoseFreshnessMinutes).toBe(11));
  });

  it("does not save a bound that is out of range, and says why", async () => {
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await bound().fill("5000");
    await expect.element(bound()).toHaveAttribute("aria-invalid", "true");
    await expect
      .element(bound())
      .toHaveAttribute("aria-describedby", "alert-glucose-freshness-help alert-glucose-freshness-error");
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByRole("alert").first()).toBeVisible();
    expect(saved).not.toHaveBeenCalled();
  });

  it("shows what is set on the closed section", async () => {
    await openEditor();

    await expect
      .element(page.getByTestId("alert-advanced-summary"))
      .toHaveTextContent("Count the condition as false, 20 min");
  });

  it("shows nothing extra on the closed section of a rule with the defaults", async () => {
    state.rule = { ...stored, onUnknown: "hold", glucoseFreshnessMinutes: null };
    await openEditor();

    await expect.element(page.getByTestId("alert-advanced-summary")).not.toBeInTheDocument();
  });

  it("says the choices could not be loaded, shows the stored choice, and still saves it", async () => {
    state.optionsFail = true;
    const saved = vi.fn(() => Promise.resolve());
    state.update = saved;
    await openAdvanced();

    await expect.element(page.getByText("The choices could not be loaded.")).toBeVisible();
    await expect.element(page.getByTestId("alert-on-unknown-not_met")).toHaveTextContent("Count the condition as false");
    await page.getByLabelText("Name").fill("Night low");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved).toHaveBeenCalledOnce());
    expect(sentRequest(saved)).toMatchObject({ onUnknown: "not_met", glucoseFreshnessMinutes: 20 });
  });

  it("shows a choice it does not know as the raw value, without a description", async () => {
    state.options = { ...ALERT_SETTING_OPTIONS, onUnknownValues: ["hold", "met", "not_met", "later"] };
    await openAdvanced();

    await expect.element(page.getByTestId("alert-on-unknown-later")).toHaveTextContent(/^\s*later\s*$/);
  });
});
