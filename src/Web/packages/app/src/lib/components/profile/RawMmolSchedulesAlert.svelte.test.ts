import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  RawMmolReason,
  RawMmolScheduleKind,
  type RawMmolSchedule,
} from "$api-clients";

let schedules: RawMmolSchedule[] = [];
const dismissed: string[] = [];

vi.mock("$api/generated/profiles.generated.remote", () => ({
  getRawMmolSchedules: () => ({ current: schedules, loading: false }),
  dismissRawMmolIsf: (id: string) => {
    dismissed.push(id);
    return Promise.resolve();
  },
  getTargetRangeScheduleById: () => ({ current: undefined }),
  getSensitivityScheduleById: () => ({ current: undefined }),
  updateTargetRangeSchedule: () => Promise.resolve(),
  updateSensitivitySchedule: () => Promise.resolve(),
}));

import RawMmolSchedulesAlert from "./RawMmolSchedulesAlert.svelte";

const target: RawMmolSchedule = {
  kind: RawMmolScheduleKind.TargetRange,
  id: "0199a000-0000-7000-8000-000000000001",
  profileName: "Weekday",
  reason: RawMmolReason.TargetInMmolBand,
  canDismiss: false,
  entries: [{ time: "00:00", low: 5, high: 5.5 }],
};

const isf: RawMmolSchedule = {
  kind: RawMmolScheduleKind.Sensitivity,
  id: "0199a000-0000-7000-8000-000000000002",
  profileName: "Weekend",
  reason: RawMmolReason.IsfMayBeMmol,
  canDismiss: true,
  entries: [{ time: "00:00", value: 3.5 }],
};

const dismissButton = () =>
  page.getByRole("button", { name: "These values are already mg/dL" });

describe("RawMmolSchedulesAlert", () => {
  beforeEach(() => {
    schedules = [target, isf];
    dismissed.length = 0;
    pageState.data = { effectivePermissions: ["therapy.readwrite"] };
  });

  it("lists each schedule with its profile name, stored values and an edit button", async () => {
    render(RawMmolSchedulesAlert, {});

    await expect.element(page.getByText("Weekday")).toBeVisible();
    await expect.element(page.getByText("00:00 5-5.5")).toBeVisible();
    await expect.element(page.getByText("Weekend")).toBeVisible();
    await expect.element(page.getByText("00:00 3.5")).toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Edit values" }).nth(1))
      .toBeVisible();
  });

  it("offers the mg/dL dismiss only on the ISF item", async () => {
    schedules = [target];
    render(RawMmolSchedulesAlert, {});

    await expect.element(page.getByText("Weekday")).toBeVisible();
    await expect.element(dismissButton()).not.toBeInTheDocument();
  });

  it("dismisses the ISF item it belongs to", async () => {
    render(RawMmolSchedulesAlert, {});

    await dismissButton().click();

    expect(dismissed).toEqual([isf.id]);
  });

  it("renders nothing for a caller without therapy.readwrite", async () => {
    pageState.data = { effectivePermissions: ["therapy.read"] };

    render(RawMmolSchedulesAlert, {});

    await expect
      .element(page.getByText("Some profile values look like mmol/L"))
      .not.toBeInTheDocument();
  });

  it("renders nothing when no schedule is listed", async () => {
    schedules = [];

    render(RawMmolSchedulesAlert, {});

    await expect
      .element(page.getByText("Some profile values look like mmol/L"))
      .not.toBeInTheDocument();
  });
});
