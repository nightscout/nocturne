import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

// Mock the generated remote functions before importing the component.
// The component calls list() as a reactive query (reading `.current`) and
// revoke() as a command function.
const revoke = vi.fn().mockResolvedValue({ success: true });

let appsCurrent: unknown[] = [];

vi.mock("$lib/api/generated/connectedApps.generated.remote", () => ({
  list: () => remoteQuery(() => appsCurrent),
  revoke: (...args: unknown[]) => revoke(...args),
}));

import ConnectedApps from "./ConnectedApps.svelte";

function makeApp(overrides: Record<string, unknown> = {}) {
  return {
    grantId: "grant-1",
    clientId: "prelude",
    clientName: "Prelude",
    isVerified: true,
    scopes: ["glucose.read"],
    createdAt: new Date().toISOString(),
    deviceCount: 0,
    ...overrides,
  };
}

describe("ConnectedApps", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    appsCurrent = [];
  });

  it("names how many paired devices a revoke will remove", async () => {
    appsCurrent = [makeApp({ deviceCount: 3 })];
    render(ConnectedApps);

    await page.getByRole("button", { name: "Revoke" }).click();

    await expect
      .element(
        page.getByText(
          "Its 3 paired devices will also be removed and stop receiving alerts."
        )
      )
      .toBeVisible();
  });

  it("uses the singular for a single paired device", async () => {
    appsCurrent = [makeApp({ deviceCount: 1 })];
    render(ConnectedApps);

    await page.getByRole("button", { name: "Revoke" }).click();

    await expect
      .element(
        page.getByText(
          "Its 1 paired device will also be removed and stop receiving alerts."
        )
      )
      .toBeVisible();
  });

  it("does not mention devices when the app has none", async () => {
    appsCurrent = [makeApp({ deviceCount: 0 })];
    render(ConnectedApps);

    await page.getByRole("button", { name: "Revoke" }).click();

    await expect
      .element(
        page.getByText("will also be removed and stop receiving alerts.", {
          exact: false,
        })
      )
      .not.toBeInTheDocument();
  });
});
