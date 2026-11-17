import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  lookup: vi.fn(),
  deviceApprove: vi.fn(),
}));

vi.mock("#routes/(authenticated)/oauth/oauth.remote.js", () => ({
  getDeviceInfo: (args: unknown) => createQueryResource(() => mocks.lookup(args)),
}));

vi.mock("#lib/api/generated/oAuths.generated.remote.js", () => ({
  deviceApprove: (...args: unknown[]) => mocks.deviceApprove(...args),
}));

import { getOAuthScopeDescription } from "#lib/constants/oauth-scopes.js";
import DeviceCodeApproval from "./DeviceCodeApproval.svelte";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

const DEVICE = {
  userCode: "ABCD-EFGH",
  clientId: "device-client",
  clientDisplayName: "Trio",
  scopes: ["glucose.read"],
  deletionAccess: "None",
};

function renderApproval() {
  const onApproved = vi.fn();
  render(DeviceCodeApproval, { props: { onApproved } });
  return onApproved;
}

async function reachConsent() {
  await page.getByPlaceholder("XXXX-YYYY").fill("ABCD-EFGH");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.element(page.getByText("A device wants access")).toBeVisible();
}

describe("DeviceCodeApproval", () => {
  beforeEach(() => {
    mocks.lookup.mockReset();
    mocks.lookup.mockResolvedValue(DEVICE);
    mocks.deviceApprove.mockReset();
    mocks.deviceApprove.mockResolvedValue({});
  });

  it("looks the code up and asks for consent naming the claimed app and its permissions", async () => {
    renderApproval();

    await reachConsent();

    expect(mocks.lookup).toHaveBeenCalledWith({ userCode: "ABCD-EFGH" });
    await expect.element(page.getByRole("note")).toHaveTextContent("This request says it is from Trio");
    await expect.element(page.getByText("This application is requesting permission to:")).toBeVisible();
    await expect.element(page.getByText(getOAuthScopeDescription("glucose.read"))).toBeVisible();
  });

  it("flags a sensitive permission and discloses what the app can delete", async () => {
    mocks.lookup.mockResolvedValue({
      ...DEVICE,
      scopes: ["glucose.read", "device.actuate"],
      deletionAccess: "ReadWrite",
    });
    renderApproval();

    await reachConsent();

    await expect
      .element(page.getByText(getOAuthScopeDescription("device.actuate")))
      .toHaveClass(/text-destructive/);
    await expect
      .element(page.getByText(getOAuthScopeDescription("glucose.read")))
      .not.toHaveClass(/text-destructive/);
    await expect
      .element(page.getByText("This app can change and delete the data in the categories above."))
      .toBeVisible();
  });

  it("says the code is invalid and stays on the entry step when the lookup fails", async () => {
    mocks.lookup.mockRejectedValue(new Error("network down"));
    renderApproval();

    await page.getByPlaceholder("XXXX-YYYY").fill("ZZZZ-ZZZZ");
    await page.getByRole("button", { name: "Continue" }).click();

    await expect
      .element(page.getByText("Invalid or expired device code. Check the code and try again."))
      .toBeVisible();
    await expect.element(page.getByPlaceholder("XXXX-YYYY")).toBeVisible();
  });

  it("confirms the approval and tells the surface once", async () => {
    const onApproved = renderApproval();
    await reachConsent();

    await page.getByRole("button", { name: "Approve" }).click();

    await expect.element(page.getByText("Device authorized successfully.")).toBeVisible();
    await expect.element(page.getByText("You can return to your device.")).toBeVisible();
    expect(mocks.deviceApprove).toHaveBeenCalledWith({ user_code: "ABCD-EFGH", approved: true });
    expect(onApproved).toHaveBeenCalledTimes(1);
  });

  it("keeps the consent step and says why when the approval fails", async () => {
    mocks.deviceApprove.mockRejectedValue(new Error("network down"));
    const onApproved = renderApproval();
    await reachConsent();

    await page.getByRole("button", { name: "Approve" }).click();

    await expect
      .element(page.getByText("Could not approve the device. The code possibly expired."))
      .toBeVisible();
    await expect.element(page.getByRole("button", { name: "Approve" })).toBeEnabled();
    expect(onApproved).not.toHaveBeenCalled();
  });

  it("records a denial without telling the surface it was approved", async () => {
    const onApproved = renderApproval();
    await reachConsent();

    await page.getByRole("button", { name: "Deny" }).click();

    await expect
      .element(page.getByText("Authorization denied. The device will not be granted access."))
      .toBeVisible();
    expect(mocks.deviceApprove).toHaveBeenCalledWith({ user_code: "ABCD-EFGH", approved: false });
    expect(onApproved).not.toHaveBeenCalled();
  });

  it("keeps the consent step and says why when the denial fails", async () => {
    mocks.deviceApprove.mockRejectedValue(new Error("network down"));
    renderApproval();
    await reachConsent();

    await page.getByRole("button", { name: "Deny" }).click();

    await expect.element(page.getByText("Failed to deny the request.")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Deny" })).toBeEnabled();
  });
});
