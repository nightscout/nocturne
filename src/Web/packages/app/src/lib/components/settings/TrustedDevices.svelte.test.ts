import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

interface StubDevice {
  id: string;
  deviceDescription: string;
  isCurrent: boolean;
  createdAt: string;
  expiresAt: string;
}

let devices: StubDevice[] = [];
let revokeImpl: (id: string) => Promise<unknown>;
const revokeAll = vi.fn(() => Promise.resolve({}));

vi.mock("#lib/api/generated/trustedDevices.generated.remote.js", () => ({
  list: () => ({
    get current() {
      return devices;
    },
    refresh: () => Promise.resolve(),
  }),
  revoke: (id: string) => revokeImpl(id),
  revokeAll: () => revokeAll(),
}));

import TrustedDevices from "./TrustedDevices.svelte";

const laptop: StubDevice = {
  id: "device-1",
  deviceDescription: "Firefox on Linux",
  isCurrent: true,
  createdAt: "2026-11-01T00:00:00Z",
  expiresAt: "2026-12-01T00:00:00Z",
};
const phone: StubDevice = {
  id: "device-2",
  deviceDescription: "Safari on iPhone",
  isCurrent: false,
  createdAt: "2026-11-05T00:00:00Z",
  expiresAt: "2026-12-05T00:00:00Z",
};

describe("TrustedDevices", () => {
  beforeEach(() => {
    devices = [laptop];
    revokeImpl = () => Promise.resolve({});
    revokeAll.mockClear();
  });

  it("says how a device becomes trusted when none is", async () => {
    devices = [];

    render(TrustedDevices);

    await expect.element(page.getByText("No trusted devices")).toBeVisible();
    await expect.element(page.getByText(/select “Trust this device”/)).toBeVisible();
  });

  it("marks the device the page is open on", async () => {
    devices = [laptop, phone];

    render(TrustedDevices);

    await expect.element(page.getByText("Firefox on Linux")).toBeVisible();
    await expect.element(page.getByText("Safari on iPhone")).toBeVisible();
    await expect.element(page.getByText("This device")).toBeVisible();
  });

  it("removes the device the person confirmed", async () => {
    const revoked: string[] = [];
    revokeImpl = (id) => {
      revoked.push(id);
      return Promise.resolve({});
    };

    render(TrustedDevices);
    await page.getByRole("button", { name: "Remove", exact: true }).click();
    await page.getByRole("button", { name: "Remove", exact: true }).last().click();

    await expect.element(page.getByText("Device removed.")).toBeVisible();
    expect(revoked).toEqual(["device-1"]);
  });

  it("says the removal failed when the device is already gone", async () => {
    // A rejected generated remote function is SvelteKit's `HttpError`: `{ status, body }`. The
    // API answers 404 for a device the account no longer holds.
    revokeImpl = () => Promise.reject({ status: 404, body: { message: "Not Found" } });

    render(TrustedDevices);
    await page.getByRole("button", { name: "Remove", exact: true }).click();
    await page.getByRole("button", { name: "Remove", exact: true }).last().click();

    await expect
      .element(page.getByText("Could not remove the device. Try again."))
      .toBeVisible();
    await expect.element(page.getByText("Device removed.")).not.toBeInTheDocument();
  });

  it("offers to remove all only when there is more than one device", async () => {
    render(TrustedDevices);
    await expect
      .element(page.getByRole("button", { name: "Remove all" }))
      .not.toBeInTheDocument();
  });

  it("removes every device after confirmation", async () => {
    devices = [laptop, phone];

    render(TrustedDevices);
    await page.getByRole("button", { name: "Remove all" }).click();
    await page.getByRole("button", { name: "Remove all" }).last().click();

    await expect.element(page.getByText("All trusted devices removed.")).toBeVisible();
    expect(revokeAll).toHaveBeenCalledOnce();
  });
});
