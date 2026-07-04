import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

// Mock the generated remote functions before importing the component. DeviceListState reads
// getDevices()/getDiscoveredSources() as reactive queries (`.current`) and calls reorderDevices()
// as a command.
const createDevice = Object.assign((..._args: unknown[]) => Promise.resolve({}), {
  enhance: () => ({}),
  pending: 0,
  result: undefined,
  fields: { allIssues: () => [] },
});
const updateDevice = Object.assign((..._args: unknown[]) => Promise.resolve({}), {
  enhance: () => ({}),
  pending: 0,
  result: undefined,
  fields: { allIssues: () => [] },
});
const reorderDevices = vi.fn().mockResolvedValue([]);
const deleteDevice = vi.fn().mockResolvedValue(undefined);

let devicesCurrent: unknown[] = [];
let discoveredCurrent: unknown[] = [];

vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getDevices: () => ({ get current() { return devicesCurrent; } }),
  getDiscoveredSources: () => ({ get current() { return discoveredCurrent; } }),
  createDevice,
  updateDevice,
  deleteDevice: (...args: unknown[]) => deleteDevice(...args),
  reorderDevices: (...args: unknown[]) => reorderDevices(...args),
}));

import PatientDeviceManager from "./PatientDeviceManager.svelte";

function makeDevice(id: string, over: Record<string, unknown> = {}) {
  return {
    id,
    deviceCategory: "cgm",
    manufacturer: "Dexcom",
    model: "G7",
    isCurrent: true,
    rank: null,
    ...over,
  };
}

describe("PatientDeviceManager", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    devicesCurrent = [];
    discoveredCurrent = [];
  });

  it("lists discovered sources with reading count and last-seen", async () => {
    discoveredCurrent = [
      { dataSource: "dexcom-connector", device: "G7-ABC", readingCount: 42, lastSeen: new Date().toISOString() },
    ];
    render(PatientDeviceManager, { variant: "dialog" });

    await expect.element(page.getByText("Discovered sources")).toBeVisible();
    await expect.element(page.getByText("G7-ABC")).toBeVisible();
    await expect.element(page.getByText("42 readings")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Register as device" })).toBeVisible();
  });

  it("reorders devices by persisting rank = new index", async () => {
    const a = "11111111-1111-1111-1111-111111111111";
    const b = "22222222-2222-2222-2222-222222222222";
    devicesCurrent = [makeDevice(a, { rank: 0 }), makeDevice(b, { rank: 1 })];
    render(PatientDeviceManager, { variant: "dialog" });

    // Move the first device down: [a, b] -> [b, a] -> rank b=0, a=1.
    await page.getByRole("button", { name: "Move device down in priority" }).first().click();

    expect(reorderDevices).toHaveBeenCalledTimes(1);
    expect(reorderDevices).toHaveBeenCalledWith([
      { id: b, rank: 0 },
      { id: a, rank: 1 },
    ]);
  });
});
