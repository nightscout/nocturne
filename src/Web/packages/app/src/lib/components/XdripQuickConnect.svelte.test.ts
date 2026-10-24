import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  lookup: vi.fn(),
  deviceApprove: vi.fn(),
}));

vi.mock("$api/generated/services.generated.remote", () => ({
  getActiveDataSources: () => ({ current: [], refresh: () => Promise.resolve() }),
}));

vi.mock("$routes/(authenticated)/oauth/oauth.remote", () => ({
  getDeviceInfo: (args: unknown) => ({ run: () => mocks.lookup(args) }),
}));

vi.mock("$api/generated/oAuths.generated.remote", () => ({
  deviceApprove: (...args: unknown[]) => mocks.deviceApprove(...args),
}));

import XdripQuickConnect from "./XdripQuickConnect.svelte";

describe("XdripQuickConnect", () => {
  beforeEach(() => {
    mocks.lookup.mockReset();
    mocks.lookup.mockResolvedValue({
      userCode: "ABCD-EF",
      clientId: "xdrip-client",
      clientDisplayName: "xDrip+",
      scopes: ["entries.read"],
    });
    mocks.deviceApprove.mockReset();
  });

  it("keeps the consent step and says why when the approval fails", async () => {
    mocks.deviceApprove.mockRejectedValue(new Error("network down"));
    render(XdripQuickConnect, { props: { instanceUrl: "https://demo.example/" } });

    await page.getByPlaceholder("XXXX-YY").fill("ABCD-EF");
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Approve" }).click();

    await expect
      .element(page.getByText("Could not approve the device. The code possibly expired."))
      .toBeVisible();
    await expect.element(page.getByRole("button", { name: "Approve" })).toBeEnabled();
    expect(mocks.deviceApprove).toHaveBeenCalledWith({ user_code: "ABCD-EF", approved: true });
  });
});
