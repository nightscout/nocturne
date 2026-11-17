import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import type { MembershipRequestDto, TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import PendingRequestsList from "./PendingRequestsList.svelte";

/** `GET /roles` as an Administrator reads it: every role, Owner not grantable. */
const administratorRoles: TenantRoleDto[] = [
  { id: "11111111-1111-1111-1111-111111111111", name: "Owner", slug: "owner", permissions: ["*"], canGrant: false },
  {
    id: "22222222-2222-2222-2222-222222222222",
    name: "Caretaker",
    slug: "caretaker",
    permissions: ["glucose.read"],
    canGrant: true,
  },
];

const requests: MembershipRequestDto[] = [
  { id: "33333333-3333-3333-3333-333333333333", subjectName: "Sam" },
];

describe("PendingRequestsList", () => {
  it("offers only the roles the approver can grant", async () => {
    render(PendingRequestsList, {
      requests,
      roles: administratorRoles,
      onApprove: async () => {},
      onDeny: async () => {},
    });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });
});
