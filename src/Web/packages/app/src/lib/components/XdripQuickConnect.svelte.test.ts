import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  lookup: vi.fn(),
  deviceApprove: vi.fn(),
  refresh: vi.fn(),
}));

vi.mock("#lib/api/generated/services.generated.remote.js", () => ({
  getActiveDataSources: () => ({ current: [], refresh: () => mocks.refresh() }),
}));

vi.mock("#routes/(authenticated)/oauth/oauth.remote.js", () => ({
  getDeviceInfo: (args: unknown) => createQueryResource(() => mocks.lookup(args)),
}));

vi.mock("#lib/api/generated/oAuths.generated.remote.js", () => ({
  deviceApprove: (...args: unknown[]) => mocks.deviceApprove(...args),
}));

import XdripQuickConnect from "./XdripQuickConnect.svelte";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

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
    mocks.deviceApprove.mockResolvedValue({});
    mocks.refresh.mockReset();
    mocks.refresh.mockResolvedValue(undefined);
  });

  it("checks for data again as soon as the device is approved", async () => {
    render(XdripQuickConnect, { props: { instanceUrl: "https://demo.example/" } });
    await expect.poll(() => mocks.refresh.mock.calls.length).toBe(1);

    await page.getByPlaceholder("XXXX-YYYY").fill("ABCD-EF");
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Approve" }).click();

    await expect.poll(() => mocks.refresh.mock.calls.length).toBe(2);
  });
});
