import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  start: vi.fn(),
  complete: vi.fn(),
  desktopToken: vi.fn(),
}));

vi.mock("#lib/api/generated/careLinkConnects.generated.remote.js", () => ({
  start: (...args: unknown[]) => mocks.start(...args),
  complete: (...args: unknown[]) => mocks.complete(...args),
  desktopToken: (...args: unknown[]) => mocks.desktopToken(...args),
}));

import CareLinkConnectPanel from "./CareLinkConnectPanel.svelte";

describe("CareLinkConnectPanel", () => {
  beforeEach(() => {
    mocks.start.mockReset();
    mocks.complete.mockReset();
    mocks.desktopToken.mockReset();
    mocks.start.mockResolvedValue({
      authorizeUrl: "https://carelink.example/authorize",
      state: "flow-state",
    });
    vi.spyOn(window, "open").mockReturnValue(null);
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("starts sign-in for the region the user picked", async () => {
    render(CareLinkConnectPanel);

    await page.getByRole("button", { name: "US", exact: true }).click();
    await page.getByRole("button", { name: "EU / outside-US" }).click();
    await page.getByRole("button", { name: "Connect CareLink" }).click();

    await expect.poll(() => mocks.start.mock.calls.length).toBe(1);
    expect(mocks.start).toHaveBeenCalledWith({ server: "EU" });
    await expect
      .element(page.getByLabelText("Paste the copied link (or just the code)"))
      .toBeVisible();
  });

  it("stays on the region step when the server returns no sign-in URL", async () => {
    mocks.start.mockResolvedValue({ authorizeUrl: null, state: null });
    render(CareLinkConnectPanel);

    await page.getByRole("button", { name: "Connect CareLink" }).click();

    await expect
      .element(page.getByText("Could not start CareLink sign-in. Try again."))
      .toBeVisible();
    await expect.element(page.getByRole("button", { name: "Connect CareLink" })).toBeVisible();
    expect(window.open).not.toHaveBeenCalled();
  });

  it("keeps the pasted code when the server cannot complete the sign-in", async () => {
    mocks.complete.mockResolvedValue({ success: false });
    const onConnected = vi.fn();
    render(CareLinkConnectPanel, { props: { onConnected } });

    await page.getByRole("button", { name: "Connect CareLink" }).click();
    await page
      .getByLabelText("Paste the copied link (or just the code)")
      .fill("com.medtronic.carepartner:/sso?code=abc123&state=flow-state");
    await page.getByRole("button", { name: "Finish connecting" }).click();

    await expect
      .element(page.getByText("Sign-in could not be completed. Try again."))
      .toBeVisible();
    expect(mocks.complete).toHaveBeenCalledWith({ code: "abc123", state: "flow-state" });
    expect(onConnected).not.toHaveBeenCalled();
    await expect.element(page.getByRole("button", { name: "Finish connecting" })).toBeVisible();
  });
});
