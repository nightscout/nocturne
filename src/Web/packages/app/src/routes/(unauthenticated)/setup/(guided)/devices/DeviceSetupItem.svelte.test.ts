import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteForm, remoteQuery } from "$lib/test-stubs/remote-resource";
import type { DeviceSetup } from "$api";

const remote = vi.hoisted(() => ({
  setup: undefined as unknown,
  relationship: {} as { relationship?: string; patientName?: string },
  confirmSetupDevice: vi.fn(),
  addSetupInsulin: vi.fn(),
  setTakesNoInsulin: vi.fn(),
  addSetupTracker: vi.fn(),
  deleteInsulin: vi.fn(),
  deleteDefinition: vi.fn(),
}));

vi.mock("$api/generated/setupDevices.generated.remote", () => ({
  getDeviceSetup: () => remoteQuery(() => remote.setup),
  confirmSetupDevice: (arg: unknown) => remote.confirmSetupDevice(arg),
  addSetupInsulin: (arg: unknown) => remote.addSetupInsulin(arg),
  setTakesNoInsulin: (arg: unknown) => remote.setTakesNoInsulin(arg),
  addSetupTracker: (arg: unknown) => remote.addSetupTracker(arg),
}));
vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getDevices: () => remoteQuery(() => []),
  getDiscoveredSources: () => remoteQuery(() => []),
  getInsulins: () => remoteQuery(() => []),
  createDevice: remoteForm(),
  updateDevice: remoteForm(),
  createInsulin: remoteForm(),
  updateInsulin: remoteForm(),
  deleteDevice: vi.fn(),
  reorderDevices: vi.fn(),
  deleteInsulin: (id: unknown) => remote.deleteInsulin(id),
}));
vi.mock("$api/generated/insulinCatalogs.generated.remote", () => ({
  getCatalog: () => remoteQuery(() => []),
}));
vi.mock("$api/generated/trackers.generated.remote", () => ({
  deleteDefinition: (id: unknown) => remote.deleteDefinition(id),
}));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => remoteQuery(() => undefined),
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => remote.relationship),
}));

import {
  AidAlgorithm,
  DeviceCategory,
  DeviceEvidenceSource,
  InsulinCategory,
  InsulinGroup,
  PatientRelationship,
  TrackerOfferKind,
} from "$api";
import DeviceSetupItem from "./DeviceSetupItem.svelte";

const g7 = { id: "dexcom-g7", name: "Dexcom G7", manufacturer: "Dexcom", category: DeviceCategory.CGM };
const g6 = { id: "dexcom-g6", name: "Dexcom G6", manufacturer: "Dexcom", category: DeviceCategory.CGM };
const dash = { id: "omnipod-dash", name: "Omnipod DASH", manufacturer: "Insulet", category: DeviceCategory.InsulinPump };
const novorapid = { id: "novorapid", name: "NovoRapid (Insulin Aspart)", category: InsulinCategory.RapidActing };
const tresiba = { id: "tresiba", name: "Tresiba (Insulin Degludec)", category: InsulinCategory.UltraLongActing };

function guessed(over: Partial<DeviceSetup> = {}): DeviceSetup {
  return {
    devices: [
      {
        category: DeviceCategory.CGM,
        guess: g7,
        modelKnown: false,
        evidence: [{ source: DeviceEvidenceSource.Connector, detail: "dexcom" }],
        choices: [g7, g6],
      },
      {
        category: DeviceCategory.InsulinPump,
        guess: dash,
        modelKnown: true,
        evidence: [{ source: DeviceEvidenceSource.PumpStatus, detail: "Insulet Dash" }],
        choices: [dash],
      },
    ],
    algorithm: {
      algorithm: AidAlgorithm.Loop,
      evidence: [{ source: DeviceEvidenceSource.AlgorithmStatus, detail: "Loop" }],
    },
    insulinChoices: [
      { group: InsulinGroup.RapidActing, formulations: [novorapid] },
      { group: InsulinGroup.LongActing, formulations: [tresiba] },
    ],
    insulins: [],
    takesNoInsulin: false,
    trackers: [],
    ...over,
  };
}

beforeEach(() => {
  remote.relationship = { relationship: PatientRelationship.Self };
  for (const fn of [
    remote.confirmSetupDevice,
    remote.addSetupInsulin,
    remote.setTakesNoInsulin,
    remote.addSetupTracker,
    remote.deleteInsulin,
    remote.deleteDefinition,
  ])
    fn.mockReset().mockResolvedValue({});
});

describe("DeviceSetupItem", () => {
  it("presents each guess with the evidence behind it, in the patient's voice", async () => {
    remote.relationship = { relationship: PatientRelationship.Caregiver, patientName: "Sam" };
    remote.setup = guessed();
    render(DeviceSetupItem);

    await expect
      .element(page.getByText("We looked at what's connected and think Sam uses these. Is that right?"))
      .toBeVisible();
    const cgm = page.getByTestId("device-slot-cgm");
    await expect.element(cgm.getByText("Dexcom G7", { exact: true }).first()).toBeVisible();
    await expect.element(cgm.getByText("We could only tell the brand")).toBeVisible();
    await expect.element(cgm.getByText("Connected account: dexcom")).toBeVisible();

    const pump = page.getByTestId("device-slot-pump");
    await expect.element(pump.getByText("Omnipod DASH with Loop")).toBeVisible();
    await expect.element(pump.getByText("Pump status: Insulet Dash")).toBeVisible();
    await expect.element(pump.getByText("App status: Loop")).toBeVisible();
  });

  it("confirms a guess, recording the pump with its algorithm", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    await page.getByTestId("device-slot-pump").getByRole("button", { name: "That's right" }).click();

    await vi.waitFor(() =>
      expect(remote.confirmSetupDevice).toHaveBeenCalledWith({
        catalogId: "omnipod-dash",
        aidAlgorithm: AidAlgorithm.Loop,
      })
    );
  });

  it("swaps a guess for another catalogue model before recording it", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    const cgm = page.getByTestId("device-slot-cgm");
    await cgm.getByLabelText("Or pick another model").click();
    await page.getByRole("option", { name: "Dexcom G6" }).click();
    await cgm.getByRole("button", { name: "Use this one" }).click();

    await vi.waitFor(() =>
      expect(remote.confirmSetupDevice).toHaveBeenCalledWith({ catalogId: "dexcom-g6", aidAlgorithm: undefined })
    );
  });

  it("stops guessing a device once one is on record", async () => {
    const setup = guessed();
    setup.devices![0] = { ...setup.devices![0], guess: undefined, recorded: { id: "d1", model: "Dexcom G7" } };
    remote.setup = setup;
    render(DeviceSetupItem);

    await expect.element(page.getByTestId("device-slot-pump")).toBeVisible();
    await expect.element(page.getByTestId("device-slot-cgm")).not.toBeInTheDocument();
  });

  it("groups insulins rapid and long acting, and picks or unpicks one per press", async () => {
    remote.setup = guessed({ insulins: [{ id: "i1", formulationId: "tresiba" }] });
    render(DeviceSetupItem);

    const rapid = page.getByTestId(`insulin-group-${InsulinGroup.RapidActing}`);
    const long = page.getByTestId(`insulin-group-${InsulinGroup.LongActing}`);
    await expect.element(rapid.getByRole("button", { name: "NovoRapid" })).toHaveAttribute("aria-pressed", "false");
    await expect.element(long.getByRole("button", { name: "Tresiba" })).toHaveAttribute("aria-pressed", "true");

    await rapid.getByRole("button", { name: "NovoRapid" }).click();
    await vi.waitFor(() => expect(remote.addSetupInsulin).toHaveBeenCalledWith({ formulationId: "novorapid" }));

    await long.getByRole("button", { name: "Tresiba" }).click();
    await vi.waitFor(() => expect(remote.deleteInsulin).toHaveBeenCalledWith("i1"));
  });

  it("takes none as an insulin answer", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    await page.getByRole("switch", { name: "I don't take insulin" }).click();
    await vi.waitFor(() => expect(remote.setTakesNoInsulin).toHaveBeenCalledWith({ takesNoInsulin: true }));
  });

  it("disables none while an insulin is on record", async () => {
    remote.setup = guessed({ insulins: [{ id: "i1", formulationId: "novorapid" }] });
    render(DeviceSetupItem);

    await expect.element(page.getByRole("switch", { name: "I don't take insulin" })).toBeDisabled();
  });

  it("offers trackers with the catalogue wear time, turning them on and off", async () => {
    remote.setup = guessed({
      trackers: [
        { kind: TrackerOfferKind.Sensor, deviceName: "Dexcom G7", lifespanHours: 240 },
        { kind: TrackerOfferKind.Pod, deviceName: "Omnipod DASH", lifespanHours: 72, definitionId: "t1" },
      ],
    });
    render(DeviceSetupItem);

    await expect.element(page.getByText("Dexcom G7: rated for 10 days")).toBeVisible();
    await expect.element(page.getByText("Omnipod DASH: rated for 3 days")).toBeVisible();

    await page.getByRole("switch", { name: "Sensor changes" }).click();
    await vi.waitFor(() =>
      expect(remote.addSetupTracker).toHaveBeenCalledWith({ kind: TrackerOfferKind.Sensor, name: "Dexcom G7 sensor" })
    );

    const pod = page.getByRole("switch", { name: "Pod changes" });
    await expect.element(pod).toBeChecked();
    await pod.click();
    await vi.waitFor(() => expect(remote.deleteDefinition).toHaveBeenCalledWith("t1"));
  });
});
