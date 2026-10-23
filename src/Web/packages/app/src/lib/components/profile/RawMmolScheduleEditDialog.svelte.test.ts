import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
import {
  RawMmolReason,
  RawMmolScheduleKind,
  type RawMmolSchedule,
} from "$api-clients";

const saved = vi.hoisted(() => ({ sensitivity: [] as unknown[], target: [] as unknown[] }));

vi.mock("$api/generated/profiles.generated.remote", () => ({
  getTargetRangeScheduleById: () => ({
    current: { id: "t1", entries: [{ time: "00:00", timeAsSeconds: 0, low: 5, high: 5.5 }] },
  }),
  getSensitivityScheduleById: () => ({
    current: {
      id: "s1",
      entries: [
        { time: "00:00", timeAsSeconds: 0, value: 3.5 },
        { time: "06:00", timeAsSeconds: 21600, value: 2.7 },
      ],
    },
  }),
  updateTargetRangeSchedule: (args: unknown) => {
    saved.target.push(args);
    return Promise.resolve();
  },
  updateSensitivitySchedule: (args: unknown) => {
    saved.sensitivity.push(args);
    return Promise.resolve();
  },
}));

import RawMmolScheduleEditDialog from "./RawMmolScheduleEditDialog.svelte";

const isf: RawMmolSchedule = {
  kind: RawMmolScheduleKind.Sensitivity,
  id: "s1",
  profileName: "Weekend",
  reason: RawMmolReason.IsfMayBeMmol,
  canDismiss: true,
  entries: [{ time: "00:00", value: 3.5 }],
};

const target: RawMmolSchedule = {
  kind: RawMmolScheduleKind.TargetRange,
  id: "t1",
  profileName: "Weekday",
  reason: RawMmolReason.TargetInMmolBand,
  canDismiss: false,
  entries: [{ time: "00:00", low: 5, high: 5.5 }],
};

describe("RawMmolScheduleEditDialog", () => {
  beforeEach(async () => {
    await page.viewport(1000, 800);
    saved.sensitivity.length = 0;
    saved.target.length = 0;
    glucoseUnits.current = "mmol";
  });

  afterEach(() => {
    glucoseUnits.current = "mg/dl";
  });

  it("shows the stored values in the stored unit for a mmol display tenant", async () => {
    render(RawMmolScheduleEditDialog, { item: isf, open: true });

    await expect.element(page.getByText("mg/dL/U").first()).toBeVisible();
    await expect.element(page.getByRole("spinbutton").first()).toHaveValue(3.5);
    await expect.element(page.getByRole("spinbutton").nth(1)).toHaveValue(2.7);
  });

  it("sends an unedited block back exactly as stored", async () => {
    render(RawMmolScheduleEditDialog, { item: isf, open: true });

    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved.sensitivity).toHaveLength(1));
    const { id, request } = saved.sensitivity[0] as {
      id: string;
      request: { entries: { value: number }[] };
    };
    expect(id).toBe("s1");
    expect(request.entries.map((e) => e.value)).toEqual([3.5, 2.7]);
  });

  it("sends unedited target blocks back exactly as stored", async () => {
    render(RawMmolScheduleEditDialog, { item: target, open: true });

    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(saved.target).toHaveLength(1));
    const { request } = saved.target[0] as {
      request: { entries: { low: number; high: number }[] };
    };
    expect(request.entries.map((e) => [e.low, e.high])).toEqual([[5, 5.5]]);
  });
});
