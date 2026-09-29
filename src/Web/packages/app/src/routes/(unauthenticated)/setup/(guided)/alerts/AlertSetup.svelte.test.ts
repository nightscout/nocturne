import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "$lib/test-stubs/remote-resource";
import type { AlertSetupStatus, AlertSetupTest } from "$api";

const api = vi.hoisted(() => ({
  save: vi.fn(),
  send: vi.fn(),
  confirm: vi.fn(),
  test: undefined as unknown,
  notify: vi.fn(),
  requestPermission: vi.fn(),
}));

vi.mock("$api/generated/setupAlerts.generated.remote", () => ({
  saveAlertSetup: remoteCommand((arg) => api.save(arg)),
  sendSetupTestAlert: remoteCommand(() => api.send()),
  confirmSetupTestAlertReceived: remoteCommand((arg) => api.confirm(arg)),
  setUrgentLowRecipient: remoteCommand(() => undefined),
  getSetupTestAlert: () => remoteQuery(() => api.test),
}));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => remoteQuery(() => ({ items: [] })),
}));
vi.mock("$api/generated/linkedPlatforms.generated.remote", () => ({
  getLinkedPlatforms: () => remoteQuery(() => ({ platforms: [] })),
}));
vi.mock("$api/generated/systems.generated.remote", () => ({
  getChannelStatuses: () =>
    remoteQuery(() => ({ channels: [{ channelType: "telegram_dm", offered: true, requiresDestination: false }] })),
}));
vi.mock("$api/generated/clientDevices.generated.remote", () => ({
  getCapabilityCatalog: () => remoteQuery(() => ({ kinds: [], capabilities: [] })),
}));
vi.mock("$lib/audio/alarm-sounds", () => ({
  getNotificationPermission: () => "default",
  requestNotificationPermission: api.requestPermission,
  showNotification: api.notify,
}));

import { AlertRouting, ChannelType, StarterAlertKind } from "$api";
import AlertSetup from "./AlertSetup.svelte";

function status(overrides: Partial<AlertSetupStatus> = {}): AlertSetupStatus {
  return {
    routing: AlertRouting.ToYou,
    glucoseUnits: "mg/dl",
    saved: false,
    rules: [
      { kind: StarterAlertKind.UrgentLow, isEnabled: true, threshold: 55 },
      { kind: StarterAlertKind.Low, isEnabled: true, threshold: 70 },
      { kind: StarterAlertKind.High, isEnabled: true, threshold: 250 },
      { kind: StarterAlertKind.NoReadings, isEnabled: true },
    ],
    toThisDevice: true,
    channels: [],
    verified: false,
    members: [],
    ...overrides,
  };
}

const sentTest: AlertSetupTest = {
  instanceId: "0199aaaa-0000-7000-8000-000000000001",
  ruleName: "Urgent low",
  deliveries: [{ channelType: ChannelType.InApp, status: "delivered" }],
};

const self = { kind: "self" } as const;
const urgentLowSwitch = () => page.getByRole("switch", { name: "Urgent low" });
const note = () => page.getByText("You won't be alerted to urgent lows");

describe("alerts guided page", () => {
  beforeEach(() => {
    api.save.mockReset().mockImplementation(() => status({ saved: true }));
    api.send.mockReset().mockResolvedValue(sentTest);
    api.confirm.mockReset().mockImplementation(() => status({ saved: true, verified: true }));
    api.notify.mockReset();
    api.requestPermission.mockReset().mockResolvedValue("granted");
    api.test = sentTest;
  });

  it("offers the four starter rules at the common starting points, in the owner's units", async () => {
    render(AlertSetup, { status: status({ glucoseUnits: "mmol", rules: [
      { kind: StarterAlertKind.UrgentLow, isEnabled: true, threshold: 3.1 },
      { kind: StarterAlertKind.Low, isEnabled: true, threshold: 3.9 },
      { kind: StarterAlertKind.High, isEnabled: true, threshold: 13.9 },
      { kind: StarterAlertKind.NoReadings, isEnabled: true },
    ] }), voice: self });

    await expect
      .element(page.getByText("These are common starting points. Set them to what you and your care team agreed."))
      .toBeVisible();
    await expect.element(page.getByRole("spinbutton", { name: "Urgent low threshold" })).toHaveValue(3.1);
    await expect.element(page.getByRole("spinbutton", { name: "High threshold" })).toHaveValue(13.9);
    await expect.element(page.getByRole("switch", { name: "No readings for 20 minutes" })).toBeChecked();
  });

  it("notes, without blocking, that urgent lows go unalerted once urgent low is off", async () => {
    render(AlertSetup, { status: status(), voice: self });
    await expect.element(note()).not.toBeInTheDocument();

    await urgentLowSwitch().click();
    await expect.element(note()).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Send a test alert" })).toBeEnabled();

    await urgentLowSwitch().click();
    await expect.element(note()).not.toBeInTheDocument();
  });

  it("saves, sends a test to this device, and is done only when the owner says it arrived", async () => {
    render(AlertSetup, { status: status(), voice: self });

    await page.getByRole("button", { name: "Send a test alert" }).click();

    await expect.element(page.getByText("Did it arrive?")).toBeVisible();
    expect(api.requestPermission).toHaveBeenCalled();
    expect(api.save).toHaveBeenCalledWith(expect.objectContaining({ channels: undefined }));
    expect(api.save.mock.calls[0][0].rules).toHaveLength(4);
    await vi.waitFor(() => expect(api.notify).toHaveBeenCalledOnce());
    await expect.element(page.getByTestId("alerts-verified")).not.toBeInTheDocument();

    await page.getByRole("button", { name: "Yes" }).click();

    expect(api.confirm).toHaveBeenCalledWith(sentTest.instanceId);
    await expect.element(page.getByTestId("alerts-verified")).toBeVisible();
  });

  it("troubleshoots a test that did not arrive: permission, Do Not Disturb and what Nocturne sent", async () => {
    api.requestPermission.mockResolvedValue("denied");
    api.send.mockResolvedValue({
      ...sentTest,
      deliveries: [{ channelType: ChannelType.InApp, status: "failed", lastError: "No destination" }],
    });
    api.test = { ...sentTest, deliveries: [{ channelType: ChannelType.InApp, status: "failed", lastError: "No destination" }] };
    render(AlertSetup, { status: status(), voice: self });

    await page.getByRole("button", { name: "Send a test alert" }).click();
    await page.getByRole("button", { name: "No" }).click();

    await expect.element(page.getByTestId("troubleshoot-permission")).toHaveTextContent(/blocked/);
    await expect.element(page.getByText(/Do Not Disturb/)).toBeVisible();
    await expect.element(page.getByTestId("troubleshoot-delivery")).toHaveTextContent(/couldn't send it \(No destination\)/);
    expect(api.confirm).not.toHaveBeenCalled();
    await expect.element(page.getByRole("button", { name: "Send another test" })).toBeVisible();
  });

  it("puts other channels behind Send somewhere else", async () => {
    render(AlertSetup, { status: status(), voice: self });
    await expect.element(page.getByTestId("this-device")).toBeVisible();

    await page.getByRole("button", { name: "Send somewhere else" }).click();

    await expect.element(page.getByRole("button", { name: "Add channel" })).toBeVisible();
    await expect.element(page.getByTestId("this-device")).not.toBeInTheDocument();
  });

  it("tells a caregiver the alerts about the patient come to them overnight too", async () => {
    render(AlertSetup, {
      status: status({ routing: AlertRouting.ToYouAsCaregiver }),
      voice: { kind: "named", name: "Sam" },
    });

    await expect
      .element(page.getByTestId("alerts-caregiver-note"))
      .toHaveTextContent("Alerts about Sam's glucose come to you, including overnight.");
  });

  it("leaves a helper's alerts to the person they hand over to", async () => {
    render(AlertSetup, { status: status({ routing: AlertRouting.LeftForRecipient }), voice: self });

    await expect
      .element(page.getByText("The person you hand this over to will choose where alerts go"))
      .toBeVisible();
    await expect.element(urgentLowSwitch()).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Send a test alert" })).not.toBeInTheDocument();
  });

  it("offers a patient to alert someone else to urgent lows once the rules are saved", async () => {
    render(AlertSetup, { status: status({ saved: true }), voice: self });

    await expect.element(page.getByTestId("alerts-someone-else")).toBeVisible();
    await expect.element(page.getByRole("link", { name: "Go to Sharing" })).toBeVisible();
  });
});
