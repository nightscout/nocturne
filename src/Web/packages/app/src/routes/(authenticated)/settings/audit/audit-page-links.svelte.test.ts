import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

vi.mock("#lib/api/generated/audits.generated.remote.js", () => ({
  getMutationAuditLog: () => remoteQuery(() => ({ data: [], pagination: { total: 0 } })),
  getReadAccessAuditLog: () => remoteQuery(() => ({ data: [], pagination: { total: 0 } })),
  getAuditConfig: () => remoteQuery(() => ({})),
  updateAuditConfig: vi.fn(),
}));

import AuditPage from "./+page.svelte";

describe("audit page link to the member history", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("links to the member history for a caller with members.manage", async () => {
    page.data.effectivePermissions = ["audit.read", "members.manage"];
    render(AuditPage);

    await expect
      .element(browserPage.getByRole("link", { name: /Member history/ }))
      .toHaveAttribute("href", "/settings/members/history");
  });

  it("offers no link without members.manage", async () => {
    page.data.effectivePermissions = ["audit.read"];
    render(AuditPage);

    await expect.element(browserPage.getByRole("heading", { name: "Audit log" })).toBeVisible();
    expect(browserPage.getByRole("link", { name: /Member history/ }).elements()).toHaveLength(0);
  });
});
