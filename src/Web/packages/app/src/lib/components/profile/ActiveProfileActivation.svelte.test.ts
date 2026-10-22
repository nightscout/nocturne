import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

let status_current: unknown;
let renew_args: unknown[];
let end_args: unknown[];

vi.mock("$api/generated/profileActivations.generated.remote", () => ({
  getActivationStatus: () => ({
    current: status_current,
    loading: false,
    error: undefined,
    refresh: () => Promise.resolve(),
  }),
  renewActivation: (id: unknown) => {
    renew_args.push(id);
    return Promise.resolve({});
  },
  endActivation: (id: unknown) => {
    end_args.push(id);
    return Promise.resolve({});
  },
}));

import ActiveProfileActivation from "./ActiveProfileActivation.svelte";

const recorded = {
  id: "11111111-1111-1111-1111-111111111111",
  baseProfileName: "Rhys",
  percentage: 110,
  timeshiftHours: 1,
  origin: "User",
  phase: "Requested",
  dataSource: "manual",
  enteredBy: "Rhys G",
  startMills: Date.now() - 60_000,
};

const reported = {
  id: "22222222-2222-2222-2222-222222222222",
  baseProfileName: "Pump",
  percentage: 100,
  timeshiftHours: 0,
  origin: "Uploader",
  phase: "Effective",
  app: "AAPS",
  startMills: Date.now() - 3_600_000,
};

describe("ActiveProfileActivation", () => {
  beforeEach(() => {
    renew_args = [];
    end_args = [];
  });

  it("shows who recorded the switch in force and ends it", async () => {
    status_current = { active: recorded, recorded, recordedSwitchesApply: true, recordedProfileChanged: false };
    render(ActiveProfileActivation);

    await expect.element(page.getByText("Recorded by Rhys G")).toBeVisible();
    await expect.element(page.getByText("110%")).toBeVisible();
    await expect.element(page.getByText("+1h")).toBeVisible();
    expect(page.getByRole("button", { name: "Switch again" }).elements()).toHaveLength(0);

    await page.getByRole("button", { name: "End switch" }).click();
    await expect.poll(() => end_args).toEqual([recorded.id]);
  });

  it("says the recorded switch is not followed while an AID system reports", async () => {
    status_current = { active: reported, reported, recorded, recordedSwitchesApply: false, recordedProfileChanged: false };
    render(ActiveProfileActivation);

    await expect.element(page.getByText("Uploaded by AAPS", { exact: false })).toBeVisible();
    await expect
      .element(page.getByText(/Nocturne's calculations follow the profile\s+AAPS reports/))
      .toBeVisible();
  });

  it("offers to switch again when the profile has changed since", async () => {
    status_current = { active: recorded, recorded, recordedSwitchesApply: true, recordedProfileChanged: true };
    render(ActiveProfileActivation);

    await expect.element(page.getByText(/Rhys has changed since this switch was recorded/)).toBeVisible();
    await page.getByRole("button", { name: "Switch again" }).click();
    await expect.poll(() => renew_args).toEqual([recorded.id]);
  });

  it("says the default profile applies when no switch is active", async () => {
    status_current = { recordedSwitchesApply: true, recordedProfileChanged: false };
    render(ActiveProfileActivation);

    await expect.element(page.getByText("No switch is active. The default profile applies.")).toBeVisible();
    expect(page.getByRole("button", { name: "End switch" }).elements()).toHaveLength(0);
  });
});
