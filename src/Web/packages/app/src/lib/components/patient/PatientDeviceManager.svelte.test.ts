import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { remoteForm, remoteQuery } from "$lib/test-stubs/remote-resource";

// Mock the generated remote functions before importing the component. DeviceListState reads
// getDevices()/getDiscoveredSources() as reactive queries (`.current`) and calls reorderDevices()
// as a command. The spies are hoisted with the `vi.mock` call that reaches them: the factory runs
// while the component is imported, which is before a module-level `const` is initialised.
const { reorderDevices, deleteDevice } = vi.hoisted(() => ({
  reorderDevices: vi.fn().mockResolvedValue([]),
  deleteDevice: vi.fn().mockResolvedValue(undefined),
}));

let devicesCurrent: unknown[] = [];
let discoveredCurrent: unknown[] = [];

vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getDevices: () => remoteQuery(() => devicesCurrent),
  getDiscoveredSources: () => remoteQuery(() => discoveredCurrent),
  createDevice: remoteForm(),
  updateDevice: remoteForm(),
  deleteDevice: (...args: unknown[]) => deleteDevice(...args),
  reorderDevices: (...args: unknown[]) => reorderDevices(...args),
}));

let catalogCurrent: unknown[] = [];

vi.mock("$api/generated/deviceCatalogs.generated.remote", () => ({
  getCatalog: () => remoteQuery(() => catalogCurrent),
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
    catalogCurrent = [];
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

  it("fills the whole device from a catalog name picked in the combobox", async () => {
    catalogCurrent = [
      { id: "omnipod-dash", name: "Omnipod DASH", manufacturer: "Insulet", category: "InsulinPump", pump: { isPatch: true, siteDurationHours: 72 } },
      { id: "custom-pump", name: "Custom Pump", manufacturer: "Custom", category: "InsulinPump" },
      { id: "dexcom-g7", name: "Dexcom G7", manufacturer: "Dexcom", category: "CGM", cgm: { hasSeparateTransmitter: false } },
    ];
    render(PatientDeviceManager, { variant: "dialog" });

    await page.getByRole("button", { name: "Add device" }).click();
    await page.getByRole("combobox", { name: "Device" }).click();
    await page.getByPlaceholder("Search or type a device name...").fill("g7");

    await expect.element(page.getByRole("option", { name: /Omnipod DASH/ })).not.toBeInTheDocument();
    await page.getByRole("option", { name: /Dexcom G7/ }).click();

    await expect.element(page.getByText("Also adds a sensor tracker for this model.")).toBeVisible();
    await expect.element(page.getByLabelText("Manufacturer")).toHaveValue("Dexcom");
    const form = new FormData(document.querySelector("form")!);
    expect(form.get("catalogId")).toBe("dexcom-g7");
    expect(form.get("model")).toBe("Dexcom G7");
    expect(form.get("deviceCategory")).toBe("CGM");
  });

  it("hides the catalog's placeholder entries and takes a name it does not list", async () => {
    catalogCurrent = [
      { id: "custom-pump", name: "Custom Pump", manufacturer: "Custom", category: "InsulinPump" },
    ];
    render(PatientDeviceManager, { variant: "dialog" });

    await page.getByRole("button", { name: "Add device" }).click();
    await page.getByRole("combobox", { name: "Device" }).click();
    await expect.element(page.getByRole("option", { name: /Custom Pump/ })).not.toBeInTheDocument();

    await page.getByPlaceholder("Search or type a device name...").fill("Kaleido");
    await page.getByRole("option", { name: 'Use "Kaleido"' }).click();

    const form = new FormData(document.querySelector("form")!);
    expect(form.get("model")).toBe("Kaleido");
    expect(form.get("catalogId")).toBeNull();
  });

  describe("inline variant", () => {
    it("names a device with no manufacturer or model as unknown", async () => {
      devicesCurrent = [
        makeDevice("11111111-1111-1111-1111-111111111111"),
        makeDevice("22222222-2222-2222-2222-222222222222", { manufacturer: null, model: null }),
      ];
      render(PatientDeviceManager, { variant: "inline" });

      await expect.element(page.getByText("Dexcom G7")).toBeVisible();
      await expect.element(page.getByText("Unknown device")).toBeVisible();
    });

    it("opens the add form with the add button disabled until it is complete", async () => {
      render(PatientDeviceManager, { variant: "inline" });

      await page.getByRole("button", { name: "Add device" }).click();

      await expect.element(page.getByText("Add a device")).toBeVisible();
      await expect.element(page.getByText("Device category")).toBeVisible();
      await expect.element(page.getByText("AID algorithm")).not.toBeInTheDocument();
      await expect.element(page.getByRole("button", { name: "Add device" })).toBeDisabled();
    });

    it("asks for the AID algorithm once the category is an insulin pump", async () => {
      render(PatientDeviceManager, { variant: "inline" });

      await page.getByRole("button", { name: "Add device" }).click();
      await page.getByText("Select category").click();
      await page.getByRole("option", { name: "Insulin pump" }).click();

      await expect.element(page.getByText("AID algorithm")).toBeVisible();
      await expect.element(page.getByText("Select algorithm")).toBeVisible();
    });

    it("enables add once category, manufacturer and model are set", async () => {
      catalogCurrent = [
        { id: "dexcom-g7", name: "Dexcom G7", manufacturer: "Dexcom", category: "CGM", cgm: { hasSeparateTransmitter: false } },
      ];
      render(PatientDeviceManager, { variant: "inline" });

      await page.getByRole("button", { name: "Add device" }).click();
      await page.getByRole("combobox", { name: "Device" }).click();
      await page.getByRole("option", { name: /Dexcom G7/ }).click();

      await expect.element(page.getByLabelText("Manufacturer")).toHaveValue("Dexcom");
      await expect.element(page.getByRole("button", { name: "Add device" })).toBeEnabled();
    });
  });
});
