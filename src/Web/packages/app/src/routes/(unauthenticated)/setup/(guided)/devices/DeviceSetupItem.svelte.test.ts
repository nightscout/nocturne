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
  removeSetupInsulin: vi.fn(),
  setTakesNoInsulin: vi.fn(),
  addSetupTracker: vi.fn(),
  removeSetupTracker: vi.fn(),
}));

vi.mock("$api/generated/setupDevices.generated.remote", () => ({
  getDeviceSetup: () => remoteQuery(() => remote.setup),
  confirmSetupDevice: (arg: unknown) => remote.confirmSetupDevice(arg),
  addSetupInsulin: (arg: unknown) => remote.addSetupInsulin(arg),
  removeSetupInsulin: (arg: unknown) => remote.removeSetupInsulin(arg),
  setTakesNoInsulin: (arg: unknown) => remote.setTakesNoInsulin(arg),
  addSetupTracker: (arg: unknown) => remote.addSetupTracker(arg),
  removeSetupTracker: (arg: unknown) => remote.removeSetupTracker(arg),
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
  deleteInsulin: vi.fn(),
}));
vi.mock("$api/generated/insulinCatalogs.generated.remote", () => ({
  getCatalog: () => remoteQuery(() => []),
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
  InsulinRole,
  PatientRelationship,
  TrackerOfferKind,
  TrackerOfferState,
} from "$api";
import DeviceSetupItem from "./DeviceSetupItem.svelte";

const g7 = { id: "dexcom-g7", name: "Dexcom G7", manufacturer: "Dexcom", category: DeviceCategory.CGM };
const g6 = { id: "dexcom-g6", name: "Dexcom G6", manufacturer: "Dexcom", category: DeviceCategory.CGM };
const dash = { id: "omnipod-dash", name: "Omnipod DASH", manufacturer: "Insulet", category: DeviceCategory.InsulinPump };
const op5 = { id: "omnipod-5", name: "Omnipod 5", manufacturer: "Insulet", category: DeviceCategory.InsulinPump };
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
        choices: [dash, op5],
      },
    ],
    algorithm: {
      algorithm: AidAlgorithm.Loop,
      evidence: [{ source: DeviceEvidenceSource.AlgorithmStatus, detail: "Loop" }],
    },
    insulinChoices: [
      { group: InsulinGroup.RapidActing, choices: [{ formulation: novorapid, addedHere: false }] },
      { group: InsulinGroup.LongActing, choices: [{ formulation: tresiba, addedHere: false }] },
    ],
    insulins: [],
    otherInsulins: [],
    takesNoInsulin: false,
    trackers: [],
    ...over,
  };
}

async function pick(label: string, option: string, within = page.getByTestId("device-slot-pump")) {
  await within.getByLabelText(label).click();
  await page.getByRole("option", { name: option }).click();
}

beforeEach(() => {
  remote.relationship = { relationship: PatientRelationship.Self };
  for (const fn of [
    remote.confirmSetupDevice,
    remote.addSetupInsulin,
    remote.removeSetupInsulin,
    remote.setTakesNoInsulin,
    remote.addSetupTracker,
    remote.removeSetupTracker,
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
    await expect.element(cgm.getByTestId("brand-only")).toBeVisible();
    await expect.element(cgm.getByText("Connected account: dexcom")).toBeVisible();

    const pump = page.getByTestId("device-slot-pump");
    await expect.element(pump.getByText("Pump status: Insulet Dash")).toBeVisible();
    await expect.element(pump.getByTestId("brand-only")).not.toBeInTheDocument();
    await expect.element(pump.getByLabelText("Automated insulin delivery (AID) app")).toHaveTextContent("Loop");
    await expect.element(pump.getByText("App status: Loop")).toBeVisible();
  });

  it("confirms a guessed pump with the app shown against it", async () => {
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

  it("drops the guessed app when the pump is swapped, until the owner picks one", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    await pick("Or pick another model", "Omnipod 5");
    const pump = page.getByTestId("device-slot-pump");
    await expect.element(pump.getByLabelText("Automated insulin delivery (AID) app")).toHaveTextContent("Not set");
    await pump.getByRole("button", { name: "Use this one" }).click();
    await vi.waitFor(() =>
      expect(remote.confirmSetupDevice).toHaveBeenLastCalledWith({ catalogId: "omnipod-5", aidAlgorithm: undefined })
    );

    await pick("Automated insulin delivery (AID) app", "None (manual mode)");
    await pump.getByRole("button", { name: "Use this one" }).click();
    await vi.waitFor(() =>
      expect(remote.confirmSetupDevice).toHaveBeenLastCalledWith({
        catalogId: "omnipod-5",
        aidAlgorithm: AidAlgorithm.None,
      })
    );
  });

  it("swaps a sensor guess for another catalogue model before recording it", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    const cgm = page.getByTestId("device-slot-cgm");
    await pick("Or pick another model", "Dexcom G6", cgm);
    await cgm.getByRole("button", { name: "Use this one" }).click();

    await vi.waitFor(() =>
      expect(remote.confirmSetupDevice).toHaveBeenCalledWith({ catalogId: "dexcom-g6", aidAlgorithm: undefined })
    );
  });

  it("stops guessing a device once one is on record", async () => {
    const setup = guessed();
    setup.devices![0] = { ...setup.devices![0], guess: undefined, recorded: { id: "d1", model: "G7" } };
    remote.setup = setup;
    render(DeviceSetupItem);

    await expect.element(page.getByTestId("device-slot-pump")).toBeVisible();
    await expect.element(page.getByTestId("device-slot-cgm")).not.toBeInTheDocument();
  });

  it("picks and unpicks insulins it added, and leaves ones on record before alone", async () => {
    remote.setup = guessed({
      insulinChoices: [
        { group: InsulinGroup.RapidActing, choices: [{ formulation: novorapid, recordedId: "i1", addedHere: true }] },
        { group: InsulinGroup.LongActing, choices: [{ formulation: tresiba, recordedId: "i2", addedHere: false }] },
      ],
      insulins: [{ id: "i1" }, { id: "i2" }],
    });
    render(DeviceSetupItem);

    const rapid = page.getByTestId(`insulin-group-${InsulinGroup.RapidActing}`);
    const long = page.getByTestId(`insulin-group-${InsulinGroup.LongActing}`);
    await expect.element(rapid.getByRole("button", { name: "NovoRapid" })).toHaveAttribute("aria-pressed", "true");
    await expect.element(long.getByRole("button", { name: "Tresiba" })).toHaveAttribute("aria-pressed", "true");
    await expect.element(long.getByRole("button", { name: "Tresiba" })).toBeDisabled();

    await rapid.getByRole("button", { name: "NovoRapid" }).click();
    await vi.waitFor(() => expect(remote.removeSetupInsulin).toHaveBeenCalledWith("novorapid"));
  });

  it("adds an unpicked insulin", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    await page.getByRole("button", { name: "NovoRapid" }).click();
    await vi.waitFor(() => expect(remote.addSetupInsulin).toHaveBeenCalledWith({ formulationId: "novorapid" }));
  });

  it("says whose insulin action time Nocturne will use, and that it doesn't change the pump", async () => {
    remote.relationship = { relationship: PatientRelationship.Caregiver, patientName: "Sam" };
    remote.setup = guessed({
      actionTimeInsulin: { id: "i1", name: "Fiasp (Faster Aspart)", dia: 3.5, role: InsulinRole.Both },
      insulins: [{ id: "i1" }],
    });
    render(DeviceSetupItem);

    const note = page.getByTestId("action-time");
    await expect.element(note).toHaveTextContent(/action time of Fiasp \(Faster Aspart\), 3\.5 hours/);
    await expect.element(note).toHaveTextContent(/in place of the value in Sam's profile/);
    await expect.element(note).toHaveTextContent(/doesn't change Sam's pump or AID app/);
    await expect.element(note.getByRole("link", { name: "patient settings" })).toBeVisible();
  });

  it("takes none as an insulin answer, in the patient's voice", async () => {
    remote.setup = guessed();
    render(DeviceSetupItem);

    await page.getByRole("switch", { name: "You don't take insulin" }).click();
    await vi.waitFor(() => expect(remote.setTakesNoInsulin).toHaveBeenCalledWith({ takesNoInsulin: true }));
  });

  it("disables none while an insulin is on record", async () => {
    remote.setup = guessed({ insulins: [{ id: "i1", formulationId: "novorapid" }] });
    render(DeviceSetupItem);

    await expect.element(page.getByRole("switch", { name: "You don't take insulin" })).toBeDisabled();
  });

  it("offers trackers with typical wear times, toggling only the ones it added", async () => {
    remote.setup = guessed({
      trackers: [
        { kind: TrackerOfferKind.Sensor, deviceName: "Dexcom G7", wearDays: 10, wearHours: 0, state: TrackerOfferState.Off },
        { kind: TrackerOfferKind.InfusionSet, deviceName: "t:slim X2", wearDays: 1, wearHours: 0, state: TrackerOfferState.AddedHere, definitionId: "t1" },
        { kind: TrackerOfferKind.Reservoir, deviceName: "t:slim X2", wearDays: 3, wearHours: 12, state: TrackerOfferState.AlreadyTracked, definitionId: "t2" },
      ],
    });
    render(DeviceSetupItem);

    await expect.element(page.getByText("Dexcom G7: typically worn for 10 days")).toBeVisible();
    await expect.element(page.getByText("t:slim X2: typically worn for 1 day")).toBeVisible();
    await expect.element(page.getByText("t:slim X2: typically worn for 3 days and 12 hours")).toBeVisible();

    await page.getByRole("switch", { name: "Sensor changes" }).click();
    await vi.waitFor(() =>
      expect(remote.addSetupTracker).toHaveBeenCalledWith({ kind: TrackerOfferKind.Sensor, name: "Dexcom G7 sensor" })
    );

    const set = page.getByRole("switch", { name: "Infusion set changes" });
    await expect.element(set).toBeChecked();
    await set.click();
    await vi.waitFor(() => expect(remote.removeSetupTracker).toHaveBeenCalledWith(TrackerOfferKind.InfusionSet));

    const reservoir = page.getByTestId(`tracker-${TrackerOfferKind.Reservoir}`);
    await expect.element(reservoir.getByText("Already tracked")).toBeVisible();
    await expect.element(reservoir.getByRole("switch")).not.toBeInTheDocument();
    await expect.element(reservoir.getByRole("link", { name: "Tracker settings" })).toBeVisible();
  });
});
