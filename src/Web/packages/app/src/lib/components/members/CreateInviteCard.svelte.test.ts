import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { RoleAccessLevel, type TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import CreateInviteCard from "./CreateInviteCard.svelte";

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  createInvite: vi.fn(),
}));

const ownerRoleId = "11111111-1111-1111-1111-111111111111";
const caretakerRoleId = "22222222-2222-2222-2222-222222222222";
const deniedRoleId = "55555555-5555-5555-5555-555555555555";

/** `GET /roles` as an Administrator reads it: every role, Owner not grantable. */
const administratorRoles: TenantRoleDto[] = [
  {
    id: ownerRoleId,
    name: "Owner",
    slug: "owner",
    permissions: ["*"],
    accessLevel: RoleAccessLevel.Manage,
    canGrant: false,
  },
  {
    id: caretakerRoleId,
    name: "Caretaker",
    slug: "caretaker",
    permissions: ["glucose.read"],
    accessLevel: RoleAccessLevel.ReadOnly,
    canGrant: true,
  },
];

describe("CreateInviteCard", () => {
  it("offers only the roles the inviter can grant", async () => {
    render(CreateInviteCard, { roles: administratorRoles, onCancel: () => {} });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });

  it("does not offer a role that gives no access", async () => {
    render(CreateInviteCard, {
      roles: [
        ...administratorRoles,
        { id: deniedRoleId, name: "Denied", slug: "denied", permissions: [], canGrant: true },
      ],
      onCancel: () => {},
    });

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    expect(page.getByRole("checkbox", { name: "Denied" }).elements()).toHaveLength(0);
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
