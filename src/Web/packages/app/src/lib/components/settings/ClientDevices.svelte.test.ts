import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

// Mock the generated remote functions before importing the component.
// The component calls getDevices()/getCapabilityCatalog() as reactive queries
// (reading `.current`) and rename()/revoke() as command functions.
const rename = vi.fn().mockResolvedValue({});
const revoke = vi.fn().mockResolvedValue({ success: true });

let devicesCurrent: unknown[] = [];
const catalogCurrent = {
  kinds: ["companion", "prelude"],
  capabilities: [
    { key: "notify", label: "Notifications", requiredScope: "device.notify", kinds: ["companion"], isHardware: false },
    { key: "torch", label: "Flashlight", requiredScope: "device.actuate", kinds: ["companion"], isHardware: true },
  ],
};

vi.mock("$lib/api/generated/clientDevices.generated.remote", () => ({
  getDevices: () => ({
    get current() {
      return devicesCurrent;
    },
  }),
  getCapabilityCatalog: () => ({
    get current() {
      return catalogCurrent;
    },
  }),
  rename: (...args: unknown[]) => rename(...args),
  revoke: (...args: unknown[]) => revoke(...args),
}));

import ClientDevices from "./ClientDevices.svelte";

function makeDevice(overrides: Record<string, unknown> = {}) {
  return {
    id: "dev-1",
    installId: "install-1",
    kind: "companion",
    label: "My Laptop",
    capabilities: ["notify", "torch"],
    lastSeenAt: new Date(Date.now() - 5 * 60_000).toISOString(),
    createdAt: new Date().toISOString(),
    ...overrides,
  };
}

describe("ClientDevices", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    devicesCurrent = [];
  });

  it("shows an empty state when there are no devices", async () => {
    devicesCurrent = [];
    render(ClientDevices);

    await expect
      .element(page.getByText("No registered devices.", { exact: false }))
      .toBeVisible();
  });

  it("lists a device with its kind label and capabilities", async () => {
    devicesCurrent = [makeDevice()];
    render(ClientDevices);

    await expect.element(page.getByText("My Laptop")).toBeVisible();
    // Kind badge maps "companion" -> "Companion" (exact avoids the intro copy
    // "...such as the desktop Companion").
    await expect.element(page.getByText("Companion", { exact: true })).toBeVisible();
    // Capabilities use the catalog human labels
    await expect.element(page.getByText("Notifications")).toBeVisible();
    await expect.element(page.getByText("Flashlight")).toBeVisible();
  });

  it("title-cases an unknown device kind in the badge", async () => {
    devicesCurrent = [makeDevice({ kind: "widget", label: undefined, capabilities: [] })];
    render(ClientDevices);

    // Shared deviceKindLabel falls back to the title-cased kind; it appears as
    // both the display name (no label) and the kind badge.
    await expect.element(page.getByText("Widget", { exact: true }).first()).toBeVisible();
  });

  it("renames a device via inline edit -> rename command", async () => {
    devicesCurrent = [makeDevice()];
    render(ClientDevices);

    await page.getByRole("button", { name: "Rename" }).click();

    const input = page.getByRole("textbox", { name: "Device name" });
    await input.fill("Work Desktop");
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => {
      expect(rename).toHaveBeenCalledWith({
        id: "dev-1",
        request: { label: "Work Desktop" },
      });
    });
  });

  it("revokes a device after confirming the dialog", async () => {
    devicesCurrent = [makeDevice()];
    render(ClientDevices);

    await page.getByRole("button", { name: "Revoke" }).first().click();

    // Confirm dialog action (second "Revoke" button — the dialog action)
    await page.getByRole("button", { name: "Revoke" }).last().click();

    await vi.waitFor(() => {
      expect(revoke).toHaveBeenCalledWith("dev-1");
    });
  });
});
