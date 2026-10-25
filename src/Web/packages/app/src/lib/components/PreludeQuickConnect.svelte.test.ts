import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  lookup: vi.fn(),
  deviceApprove: vi.fn(),
}));

vi.mock("$routes/(authenticated)/oauth/oauth.remote", () => ({
  getDeviceInfo: (args: unknown) => ({ run: () => mocks.lookup(args) }),
}));

vi.mock("$api/generated/oAuths.generated.remote", () => ({
  deviceApprove: (...args: unknown[]) => mocks.deviceApprove(...args),
}));

import PreludeQuickConnect from "./PreludeQuickConnect.svelte";

async function reachConsent() {
  render(PreludeQuickConnect, { props: { instanceUrl: "https://demo.example/" } });
  await page.getByPlaceholder("XXXX-YYYY").fill("ABCD-EFGH");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.element(page.getByText("A device wants access")).toBeVisible();
}

describe("PreludeQuickConnect", () => {
  beforeEach(() => {
    mocks.lookup.mockReset();
    mocks.lookup.mockResolvedValue({
      userCode: "ABCD-EFGH",
      clientId: "prelude-client",
      clientDisplayName: "Prelude",
      scopes: ["entries.read"],
    });
    mocks.deviceApprove.mockReset();
  });

  it("confirms the connection once the device is approved", async () => {
    mocks.deviceApprove.mockResolvedValue({});
    await reachConsent();

    await page.getByRole("button", { name: "Approve" }).click();

    await expect
      .element(page.getByText("Device authorized successfully. Prelude is now connected."))
      .toBeVisible();
    expect(mocks.deviceApprove).toHaveBeenCalledWith({ user_code: "ABCD-EFGH", approved: true });
  });

  it("keeps the consent step and says why when the approval fails", async () => {
    mocks.deviceApprove.mockRejectedValue(new Error("network down"));
    await reachConsent();

    await page.getByRole("button", { name: "Approve" }).click();

    await expect
      .element(page.getByText("Could not approve the device. The code possibly expired."))
      .toBeVisible();
    await expect.element(page.getByRole("button", { name: "Approve" })).toBeEnabled();
  });
});
