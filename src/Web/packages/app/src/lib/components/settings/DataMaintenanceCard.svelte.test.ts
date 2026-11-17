import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { page } from "$app/state";

// The dialogs each open a remote command on mount; the card under test only owns whether they
// are reachable, so they are stubbed down to nothing renderable.
vi.mock("#lib/components/connectors/DeduplicationDialog.svelte", () => ({
  default: () => {},
}));
vi.mock("#lib/components/connectors/DemoDataSection.svelte", () => ({
  default: () => {},
}));

import DataMaintenanceCard from "./DataMaintenanceCard.svelte";

describe("DataMaintenanceCard", () => {
  beforeEach(() => {
    page.data = { effectivePermissions: [], isPlatformAdmin: false };
  });

  it("offers nothing to someone the API would refuse", async () => {
    // Both tools are RequireSuperuser server-side. An ordinary member opens Data Quality to set a
    // sleep schedule, so a Remove Demo Data button that could only 403 is worse than none.
    page.data = {
      effectivePermissions: ["glucose.read"],
      isPlatformAdmin: false,
    };
    render(DataMaintenanceCard);

    expect(
      document.querySelector('[data-testid="deduplicate-records"]')
    ).toBeNull();
    expect(document.body.textContent).not.toContain("Remove demo data");
  });

  it("offers both tools to an owner holding the wildcard scope", async () => {
    page.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(DataMaintenanceCard);

    await expect
      .element(browser.getByTestId("deduplicate-records"))
      .toBeVisible();
    expect(document.body.textContent).toContain("Remove demo data");
  });

  it("offers only demo data deletion to a member holding tenant.settings", async () => {
    // Deduplication is RequireSuperuser; deleting demo data is tenant.settings. A tenant
    // administrator holds the second and not the first.
    page.data = {
      effectivePermissions: ["tenant.settings"],
      isPlatformAdmin: false,
    };
    render(DataMaintenanceCard);

    expect(
      document.querySelector('[data-testid="deduplicate-records"]')
    ).toBeNull();
    expect(document.body.textContent).toContain("Remove demo data");
  });
});
