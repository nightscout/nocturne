import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page } from "$app/state";

// The dialogs each open a remote command on mount; the card under test only owns whether they
// are reachable, so they are stubbed down to nothing renderable.
vi.mock("$lib/components/connectors/DeduplicationDialog.svelte", () => ({
  default: () => {},
}));
vi.mock("$lib/components/connectors/DemoDataSection.svelte", () => ({
  default: () => {},
}));

import DataMaintenanceCard from "./DataMaintenanceCard.svelte";

describe("DataMaintenanceCard", () => {
  beforeEach(() => {
    page.data = { effectivePermissions: [], isPlatformAdmin: false };
  });

  it("offers nothing to someone the API would refuse", async () => {
    // Both tools are RequireAdmin server-side. An ordinary member opens Data Quality to set a
    // sleep schedule, so a Remove Demo Data button that could only 403 is worse than none.
    page.data = {
      effectivePermissions: ["glucose.read"],
      isPlatformAdmin: false,
    };
    render(DataMaintenanceCard);

    expect(
      document.querySelector('[data-testid="deduplicate-records"]')
    ).toBeNull();
    expect(document.body.textContent).not.toContain("Remove Demo Data");
  });

  it("offers both tools to an admin", async () => {
    page.data = { effectivePermissions: ["admin"], isPlatformAdmin: false };
    render(DataMaintenanceCard);

    await expect
      .element(browser.getByTestId("deduplicate-records"))
      .toBeVisible();
    expect(document.body.textContent).toContain("Remove Demo Data");
  });

  it("reads a wildcard grant as admin", async () => {
    page.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(DataMaintenanceCard);

    await expect
      .element(browser.getByTestId("deduplicate-records"))
      .toBeVisible();
  });

  it("keeps connector cursors to platform admins, not tenant admins", async () => {
    // Resetting a cursor re-syncs a connector for the whole instance, so it answers to
    // isPlatformAdmin rather than to the tenant's own admin permission.
    page.data = { effectivePermissions: ["admin"], isPlatformAdmin: false };
    render(DataMaintenanceCard);

    expect(document.body.textContent).not.toContain("Reset Connector Cursors");
  });

  it("offers connector cursors to a platform admin", async () => {
    page.data = { effectivePermissions: ["admin"], isPlatformAdmin: true };
    render(DataMaintenanceCard);

    expect(document.body.textContent).toContain("Reset Connector Cursors");
  });
});
