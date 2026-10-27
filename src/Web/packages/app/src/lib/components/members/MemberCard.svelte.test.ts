import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { TenantMemberDto, TenantRoleDto } from "#lib/api/generated/nocturne-api-client.js";
import MemberCard, { type MemberChanges } from "./MemberCard.svelte";

const ownerRoleId = "11111111-1111-1111-1111-111111111111";
const caretakerRoleId = "22222222-2222-2222-2222-222222222222";
const deniedRoleId = "55555555-5555-5555-5555-555555555555";

function role(id: string, name: string, permissions: string[], canGrant: boolean): TenantRoleDto {
  return {
    id,
    name,
    slug: name.toLowerCase(),
    isSystem: true,
    permissions,
    memberCount: 1,
    canGrant,
  };
}

/** `GET /roles` as an Administrator reads it: every role, Owner and Denied not grantable. */
const administratorRoles: TenantRoleDto[] = [
  role(ownerRoleId, "Owner", ["*"], false),
  role(caretakerRoleId, "Caretaker", ["glucose.read"], true),
  role(deniedRoleId, "Denied", ["sharing.manage"], false),
];

const ownerMember: TenantMemberDto = {
  id: "33333333-3333-3333-3333-333333333333",
  subjectId: "44444444-4444-4444-4444-444444444444",
  name: "Alex",
  roles: [
    { roleId: ownerRoleId, name: "Owner", slug: "owner", permissions: ["*"] },
    { roleId: caretakerRoleId, name: "Caretaker", slug: "caretaker", permissions: ["glucose.read"] },
  ],
  directPermissions: [],
};

async function openEditor(onSave: (changes: MemberChanges) => void = () => {}) {
  const props = {
    member: ownerMember,
    roles: administratorRoles,
    canEditRoles: true,
    canManage: true,
    isExpanded: false,
    isSaving: false,
    onToggleExpand: () => {},
    onSave,
    onRemove: () => {},
  };
  const screen = render(MemberCard, props);
  await page.getByRole("button", { name: "Edit" }).click();
  await screen.rerender({ ...props, isExpanded: true });
}

describe("MemberCard", () => {
  it("shows a held role the editor cannot grant as checked and read-only", async () => {
    await openEditor();

    const owner = page.getByRole("checkbox", { name: "Owner" });
    await expect.element(owner).toBeChecked();
    await expect.element(owner).toBeDisabled();
    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeEnabled();
  });

  it("does not offer a role the editor cannot grant and the member does not hold", async () => {
    await openEditor();

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeVisible();
    await expect.element(page.getByRole("checkbox", { name: "Denied" })).not.toBeInTheDocument();
  });

  it("counts the permissions of a held role the editor cannot grant", async () => {
    await openEditor();

    await expect.element(page.getByText("All permissions")).toBeVisible();
  });

  it("keeps a held role the editor cannot grant when it saves a change to another role", async () => {
    const onSave = vi.fn();
    await openEditor(onSave);

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({
        access: { roleIds: [ownerRoleId], permissions: [] },
      }),
    );
  });
});
