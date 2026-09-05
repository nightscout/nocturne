import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { GoogleHealthStatus } from "$lib/api";
import { googleHealthMocks } from "$lib/test-stubs/personal-google-health";
import GoogleHealthPage from "./google-health-page.svelte";

function status(overrides: Partial<GoogleHealthStatus> = {}): GoogleHealthStatus {
  return {
    configured: false,
    connected: false,
    clientId: "",
    callbackUrl: "",
    historyDays: 7,
    selectedTypes: ["steps", "heart-rate", "weight", "sleep"],
    grantedTypes: [],
    previewRequired: false,
    capabilities: [
      { dataType: "steps", supported: true, destination: "step-counts" },
      { dataType: "body-fat", supported: false },
    ],
    ...overrides,
  };
}

describe("Google Health connector page", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    googleHealthMocks.status.mockResolvedValue(status());
    googleHealthMocks.preview.mockResolvedValue({ items: [] });
  });

  it("uses the standard connector presentation and an explicit history date", async () => {
    render(GoogleHealthPage);
    await expect.element(page.getByRole("heading", { name: "Google Health" })).toBeVisible();
    await expect.element(page.getByText("Server connector for health and fitness data")).toBeVisible();
    await expect.element(page.getByLabelText("Import data from")).toBeVisible();
  });

  it("shows the effective legacy history window when no explicit date is saved", async () => {
    const expected = new Date(Date.now() - 7 * 86_400_000)
      .toISOString()
      .slice(0, 10);
    render(GoogleHealthPage);

    await expect.element(page.getByLabelText("Import data from")).toHaveValue(expected);
  });

  it("shows detected, supported, and unsupported data types", async () => {
    googleHealthMocks.status.mockResolvedValue(status({ configured: true, connected: true }));
    googleHealthMocks.preview.mockResolvedValue({ items: [
      { dataType: "steps", granted: true, count: 42, supported: true },
      { dataType: "body-fat", granted: true, count: 3, supported: false },
    ] });
    render(GoogleHealthPage);
    await expect.element(page.getByText("Connected and importing")).toBeVisible();
    await expect.element(page.getByText("Not yet supported by Nocturne")).toBeVisible();
    await expect.element(page.getByText("Step history")).toBeVisible();
  });
});
