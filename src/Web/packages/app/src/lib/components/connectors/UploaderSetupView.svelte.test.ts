import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { UploaderPlatform, type UploaderApp, type DataSourceInfo } from "$api-clients";

const mocks = vi.hoisted(() => ({
  sources: [] as unknown[],
  createGrant: vi.fn(),
  lookup: vi.fn(),
  deviceApprove: vi.fn(),
}));

vi.mock("$api/generated/services.generated.remote", () => ({
  getActiveDataSources: () => ({
    get current() {
      return mocks.sources;
    },
    refresh: () => Promise.resolve(),
  }),
}));

vi.mock("$lib/api/generated/directGrants.generated.remote", () => ({
  create: (...args: unknown[]) => mocks.createGrant(...args),
}));

vi.mock("$routes/(authenticated)/oauth/oauth.remote", () => ({
  getDeviceInfo: (args: unknown) => ({ run: () => mocks.lookup(args) }),
}));

vi.mock("$api/generated/oAuths.generated.remote", () => ({
  deviceApprove: (...args: unknown[]) => mocks.deviceApprove(...args),
}));

import UploaderSetupView from "./UploaderSetupView.svelte";

const XDRIP: UploaderApp = { id: "xdrip", platform: UploaderPlatform.Android };
const AAPS: UploaderApp = { id: "aaps", platform: UploaderPlatform.Android };

const OAUTH_SETUP = { connectUrl: "https://demo.example/connect/xdrip" };
const MANUAL_SETUP = { baseUrl: "https://demo.example" };

function renderView(app: UploaderApp | null, setupResponse: object | null) {
  const onConnected = vi.fn();
  render(UploaderSetupView, {
    props: { app, setupResponse, onBack: vi.fn(), onConnected },
  });
  return onConnected;
}

async function enterCode(code: string) {
  await page.getByPlaceholder("XXXX-YYYY").fill(code);
  await page.getByRole("button", { name: "Continue" }).click();
}

describe("UploaderSetupView", () => {
  beforeEach(() => {
    mocks.sources = [];
    mocks.createGrant.mockReset();
    mocks.lookup.mockReset();
    mocks.lookup.mockResolvedValue({
      userCode: "ABCD-EFGH",
      clientId: "xdrip-client",
      clientDisplayName: "xDrip+",
      scopes: ["entries.read"],
    });
    mocks.deviceApprove.mockReset();
    mocks.deviceApprove.mockResolvedValue({});
  });

  describe("device authorization", () => {
    it("asks the user to scan the QR code and enter the code it shows", async () => {
      renderView(XDRIP, OAUTH_SETUP);

      await expect.element(page.getByText("Scan to connect")).toBeVisible();
      await expect.element(page.getByText("Enter authorization code")).toBeVisible();
      await expect
        .element(page.getByText(/xDrip\+ will show an 8-character code/))
        .toBeVisible();
    });

    it("shows the consent step once the code resolves to a device", async () => {
      renderView(XDRIP, OAUTH_SETUP);

      await enterCode("ABCD-EFGH");

      await expect.element(page.getByText("Authorize application")).toBeVisible();
      await expect
        .element(page.getByText("A device wants to access your Nocturne data."))
        .toBeVisible();
      expect(mocks.lookup).toHaveBeenCalledWith({ userCode: "ABCD-EFGH" });
    });

    it("keeps the consent step and says why when the approval fails", async () => {
      mocks.deviceApprove.mockRejectedValue(new Error("network down"));
      renderView(XDRIP, OAUTH_SETUP);

      await enterCode("ABCD-EFGH");
      await page.getByRole("button", { name: "Approve" }).click();

      await expect
        .element(page.getByText("Could not approve the device. The code possibly expired."))
        .toBeVisible();
      await expect.element(page.getByText("Authorize application")).toBeVisible();
    });

    it("offers the dashboard once the approved device sends data", async () => {
      mocks.sources = [{ sourceType: "xdrip", entriesLast24h: 12 } satisfies DataSourceInfo];
      const onConnected = renderView(XDRIP, OAUTH_SETUP);

      await enterCode("ABCD-EFGH");
      await page.getByRole("button", { name: "Approve" }).click();

      await expect.element(page.getByText("Device authorized")).toBeVisible();
      await expect.element(page.getByText(/12 entries in the last 24 hours/)).toBeVisible();
      await page.getByRole("button", { name: "Continue to dashboard" }).click();
      expect(onConnected).toHaveBeenCalledTimes(1);
      expect(mocks.deviceApprove).toHaveBeenCalledWith({
        user_code: "ABCD-EFGH",
        approved: true,
      });
    });
  });

  // The key request is held pending, so the view stays on its generating state.
  describe("manual setup", () => {
    beforeEach(() => {
      mocks.createGrant.mockReturnValue(new Promise(() => {}));
    });

    it("shows the connection details while the API key is generated", async () => {
      renderView(AAPS, MANUAL_SETUP);

      await expect.element(page.getByText("Connection details")).toBeVisible();
      await expect
        .element(page.getByText("Enter these values in AAPS's Nightscout settings."))
        .toBeVisible();
      await expect.element(page.getByText("https://demo.example")).toBeVisible();
      await expect.element(page.getByText("Generating API key...")).toBeVisible();
      expect(mocks.createGrant).toHaveBeenCalledWith(
        expect.objectContaining({ label: "AAPS", scopes: ["health.readwrite"] }),
      );
    });

    it("offers the dashboard once the app sends data", async () => {
      mocks.sources = [{ sourceType: "aaps", entriesLast24h: 5 } satisfies DataSourceInfo];
      const onConnected = renderView(AAPS, MANUAL_SETUP);

      await expect.element(page.getByText("Receiving data")).toBeVisible();
      await page.getByRole("button", { name: "Continue to dashboard" }).click();
      expect(onConnected).toHaveBeenCalledTimes(1);
    });
  });

  it("says the setup details could not load when there is no setup response", async () => {
    renderView(AAPS, null);

    await expect.element(page.getByText("Failed to load setup instructions")).toBeVisible();
    await expect
      .element(page.getByText("Could not load setup details for AAPS. Try again."))
      .toBeVisible();
  });
});

const pollTimers = (spy: { mock: { calls: unknown[][] } }) =>
  spy.mock.calls.filter((call) => call[1] === 15_000);

describe("UploaderSetupView manual setup key and polling", () => {
  let setIntervalSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    mocks.sources = [];
    mocks.createGrant.mockReset();
    setIntervalSpy = vi.spyOn(globalThis, "setInterval");
  });

  afterEach(() => {
    setIntervalSpy.mockRestore();
  });

  it("generates one key and starts polling once", async () => {
    mocks.createGrant.mockResolvedValue({ token: "noc_created" });
    renderView(AAPS, MANUAL_SETUP);

    await expect.element(page.getByText("noc_created")).toBeVisible();

    expect(mocks.createGrant).toHaveBeenCalledTimes(1);
    expect(pollTimers(setIntervalSpy)).toHaveLength(1);
  });

  it("does not start polling when unmounted while the key is pending", async () => {
    let resolveKey: (value: { token: string }) => void = () => {};
    mocks.createGrant.mockReturnValue(
      new Promise((resolve) => {
        resolveKey = resolve;
      })
    );
    const screen = render(UploaderSetupView, {
      props: { app: AAPS, setupResponse: MANUAL_SETUP, onBack: vi.fn(), onConnected: vi.fn() },
    });
    await expect.element(page.getByText("Generating API key...")).toBeVisible();

    await screen.unmount();
    resolveKey({ token: "noc_late" });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(pollTimers(setIntervalSpy)).toHaveLength(0);
  });

  it("shows the error and does not retry when the key request fails", async () => {
    mocks.createGrant.mockRejectedValue({
      status: 400,
      body: { message: "Label already used." },
    });
    renderView(AAPS, MANUAL_SETUP);

    await expect.element(page.getByText("Label already used.")).toBeVisible();
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(mocks.createGrant).toHaveBeenCalledTimes(1);
    expect(pollTimers(setIntervalSpy)).toHaveLength(0);
  });
});
