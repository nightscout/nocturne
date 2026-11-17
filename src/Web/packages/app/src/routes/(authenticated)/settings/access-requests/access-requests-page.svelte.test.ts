import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { error } from "@sveltejs/kit";
import type { TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({ approve: vi.fn() }));

const viewer: TenantRoleDto = {
  id: "22222222-2222-2222-2222-222222222222",
  name: "Viewer",
  slug: "viewer",
  permissions: ["glucose.read"],
  canGrant: true,
};

/** `GET /roles` as an Administrator reads it: Owner is listed but not grantable. */
const owner: TenantRoleDto = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Owner",
  slug: "owner",
  permissions: ["*"],
  canGrant: false,
};

const requests = [
  { subjectId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", name: "Sam" },
  { subjectId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", name: "Jo" },
];

vi.mock("#lib/api/generated/accessRequests.generated.remote.js", () => ({
  getPendingRequests: () => remoteQuery(() => requests),
  approve: mocks.approve,
  deny: () => Promise.resolve(),
}));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => remoteQuery(() => [owner, viewer]),
}));

import AccessRequestsPage from "./+page.svelte";

async function approveSamAsViewer() {
  render(AccessRequestsPage);
  await page.getByText("Viewer", { exact: true }).first().click();
  await page.getByRole("button", { name: "Approve" }).first().click();
}

describe("access requests approval", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.approve.mockResolvedValue(undefined);
  });

  it("offers only the roles the approver can grant", async () => {
    render(AccessRequestsPage);

    await expect.element(page.getByRole("checkbox", { name: "Viewer" }).first()).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });

  it("approves with the chosen role and reports success", async () => {
    await approveSamAsViewer();

    await expect.element(page.getByText("Access request approved.")).toBeVisible();
    expect(mocks.approve).toHaveBeenCalledWith({
      subjectId: requests[0].subjectId,
      request: { roleIds: [viewer.id], directPermissions: undefined, limitTo24Hours: false },
    });
  });

  it("shows a refused approval once, on the request it belongs to", async () => {
    mocks.approve.mockImplementation(async () =>
      error(400, "'statistics.read' is not a known permission."),
    );

    await approveSamAsViewer();

    const alerts = page.getByRole("alert");
    await expect
      .element(alerts)
      .toHaveTextContent("'statistics.read' is not a known permission.");
    expect(alerts.elements()).toHaveLength(1);

    const alert = alerts.element();
    const follows = (a: Element, b: Element) =>
      (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
    expect(follows(page.getByText("Sam", { exact: true }).element(), alert)).toBe(true);
    expect(follows(alert, page.getByText("Jo", { exact: true }).element())).toBe(true);
    expect(page.getByText("Access request approved.").elements()).toHaveLength(0);
  });
});
