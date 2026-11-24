import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { tick } from "svelte";
import { page } from "$app/state";

// The dialogs each open a remote command on mount; the card under test only owns whether they
// are reachable, so they are stubbed down to nothing renderable.
vi.mock("#lib/components/connectors/DeduplicationDialog.svelte", () => ({
  default: () => {},
}));
vi.mock("#lib/components/connectors/DemoDataSection.svelte", () => ({
  default: () => {},
}));

type Presence = { current: { hasDemoData: boolean } | undefined };
const presence = vi.hoisted(() => ({ query: undefined as unknown as Presence }));
vi.mock("#lib/api/generated/services.generated.remote.js", async () => {
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  presence.query = reactiveFields<Presence>({ current: undefined });
  return { getDemoDataPresence: () => presence.query };
});

import DataMaintenanceCard from "./DataMaintenanceCard.svelte";

describe("DataMaintenanceCard", () => {
  beforeEach(() => {
    page.data = { effectivePermissions: [], isPlatformAdmin: false };
    presence.query.current = { hasDemoData: true };
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

  it("offers no demo data deletion when the tenant holds no demo data", async () => {
    presence.query.current = { hasDemoData: false };
    page.data = { effectivePermissions: ["*"], isPlatformAdmin: false };
    render(DataMaintenanceCard);

    await expect
      .element(browser.getByTestId("deduplicate-records"))
      .toBeVisible();
    expect(document.body.textContent).not.toContain("Remove demo data");
  });

  it("shows no card to a tenant administrator when there is no demo data", async () => {
    presence.query.current = undefined;
    page.data = {
      effectivePermissions: ["tenant.settings"],
      isPlatformAdmin: false,
    };
    render(DataMaintenanceCard);

    presence.query.current = { hasDemoData: false };
    await tick();
    expect(document.body.textContent).not.toContain("Data maintenance");

    // The card follows the answer, so the absence above is the settled state, not a first frame.
    presence.query.current = { hasDemoData: true };
    await expect.element(browser.getByText("Data maintenance")).toBeVisible();
  });
});
