import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import type { AlertRuleResponse } from "$api-clients";

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

/** Stands in for a remote query: awaitable in the template, plus its methods. */
function remoteQuery<T>(value: T, extra: Record<string, unknown> = {}) {
  return Object.assign(Promise.resolve(value), {
    refresh: () => Promise.resolve(),
    ...extra,
  });
}

// `toggleRule` is the shortest write path on the page (one click, no dialog), so
// it stands in for the server's refusal.
let toggleImpl: () => Promise<unknown>;

vi.mock("$api/generated/alertRules.generated.remote", () => ({
  getRules: () => remoteQuery(rules),
  deleteRule: () => Promise.resolve(),
  toggleRule: () => toggleImpl(),
  testFire: () => Promise.resolve(),
}));

vi.mock("$api/generated/alerts.generated.remote", () => ({
  getActiveAlerts: () => remoteQuery([], { withOverride: () => undefined }),
  getAlertHistory: () => remoteQuery({ items: [], totalCount: 0 }),
  acknowledge: () => ({ updates: () => Promise.resolve() }),
}));

vi.mock("$api/generated/tenantAlertSettings.generated.remote", () => ({
  get: () => remoteQuery(null),
  update: () => Promise.resolve(),
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
    toastError.mockClear();
    pageState.data = {};
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
