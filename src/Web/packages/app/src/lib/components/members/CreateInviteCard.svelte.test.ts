import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import CreateInviteCard from "./CreateInviteCard.svelte";

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  createInvite: vi.fn(),
}));

const ownerRoleId = "11111111-1111-1111-1111-111111111111";
const caretakerRoleId = "22222222-2222-2222-2222-222222222222";

/** `GET /roles` as an Administrator reads it: every role, Owner not grantable. */
const administratorRoles: TenantRoleDto[] = [
  { id: ownerRoleId, name: "Owner", slug: "owner", permissions: ["*"], canGrant: false },
  {
    id: caretakerRoleId,
    name: "Caretaker",
    slug: "caretaker",
    permissions: ["glucose.read"],
    canGrant: true,
  },
];

describe("CreateInviteCard", () => {
  it("offers only the roles the inviter can grant", async () => {
    render(CreateInviteCard, { roles: administratorRoles, onCancel: () => {} });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });

  it("offers only the access levels whose role the inviter can grant", async () => {
    render(CreateInviteCard, {
      roles: administratorRoles,
      roleChoices: [
        { roleId: ownerRoleId, label: "Can do everything", description: "Owner" },
        { roleId: caretakerRoleId, label: "Can see readings", description: "Caretaker" },
      ],
      onCancel: () => {},
    });

    await expect.element(page.getByText("Can see readings")).toBeVisible();
    expect(page.getByText("Can do everything").elements()).toHaveLength(0);
  });
});
