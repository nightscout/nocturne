import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { page } from "$app/state";
import type { TenantMemberDto, TenantRoleDto } from "$api-clients";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

const mocks = vi.hoisted(() => ({
  getMembers: vi.fn(),
  getRoles: vi.fn(),
  getPendingRequests: vi.fn(),
  setMemberRoles: vi.fn(),
  setMemberPermissions: vi.fn(),
  setMemberLimitTo24Hours: vi.fn(),
}));

let members: TenantMemberDto[];

const viewer: TenantRoleDto = {
  id: "22222222-2222-2222-2222-222222222222",
  name: "Viewer",
  slug: "viewer",
  permissions: ["glucose.read"],
};

vi.mock("$lib/api/generated/memberInvites.generated.remote", () => ({
  getMembers: () => {
    mocks.getMembers();
    return remoteQuery(() => members);
  },
  listInvites: () => remoteQuery(() => []),
  revokeInvite: () => Promise.resolve(),
  createInvite: vi.fn(),
  removeMember: () => Promise.resolve(),
  setMemberRoles: mocks.setMemberRoles,
  setMemberPermissions: mocks.setMemberPermissions,
  setMemberLimitTo24Hours: mocks.setMemberLimitTo24Hours,
}));
vi.mock("$lib/api/generated/roles.generated.remote", () => ({
  getRoles: () => {
    mocks.getRoles();
    return remoteQuery(() => [viewer]);
  },
}));
vi.mock("$api/generated/shareLinks.generated.remote", () => ({
  getShareLink: () => remoteQuery(() => null),
}));
vi.mock("$lib/api/generated/membershipRequests.generated.remote", () => ({
  getPendingRequests: () => {
    mocks.getPendingRequests();
    return remoteQuery(() => []);
  },
  approveRequest: () => Promise.resolve(),
  denyRequest: () => Promise.resolve(),
}));
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));
vi.mock("$lib/components/members/PublicAccessCard.svelte", () => ({ default: () => {} }));
vi.mock("$lib/components/members/MembershipRequestsCard.svelte", () => ({ default: () => {} }));
vi.mock("$lib/components/members/GuestLinksSection.svelte", () => ({ default: () => {} }));
vi.mock("$lib/components/members/RolesSection.svelte", () => ({ default: () => {} }));

import MembersPage from "./+page.svelte";

const member: TenantMemberDto = {
  id: "33333333-3333-3333-3333-333333333333",
  subjectId: "44444444-4444-4444-4444-444444444444",
  name: "Alex",
  roles: [{ roleId: viewer.id, slug: viewer.slug, name: viewer.name }],
  directPermissions: [],
  limitTo24Hours: false,
};

async function expandMember() {
  render(MembersPage);
  await browserPage.getByRole("button", { name: "Edit" }).click();
  await expect.element(browserPage.getByText("Only last 24 hours")).toBeVisible();
}

describe("members page saving a member", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["members.manage"];
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
    mocks.setMemberRoles.mockResolvedValue(undefined);
    mocks.setMemberPermissions.mockResolvedValue(undefined);
    mocks.setMemberLimitTo24Hours.mockResolvedValue(undefined);
  });

  it("saves only the 24-hour limit when that is all that changed", async () => {
    await expandMember();
    await browserPage.getByText("Only last 24 hours").click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberLimitTo24Hours).toHaveBeenCalledWith({
      id: member.id,
      request: { limitTo24Hours: true },
    });
    expect(mocks.setMemberRoles).not.toHaveBeenCalled();
    expect(mocks.setMemberPermissions).not.toHaveBeenCalled();
  });

  it("saves roles and permissions together when the access changed", async () => {
    await expandMember();
    await browserPage.getByText("Viewer", { exact: true }).last().click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberRoles).toHaveBeenCalledWith({
      id: member.id,
      request: { roleIds: [] },
    });
    expect(mocks.setMemberPermissions).toHaveBeenCalledWith({
      id: member.id,
      request: { directPermissions: [] },
    });
    expect(mocks.setMemberLimitTo24Hours).not.toHaveBeenCalled();
  });

  it("reports a failed limit save on the card instead of claiming success", async () => {
    mocks.setMemberLimitTo24Hours.mockImplementation(async () =>
      error(400, "The limit could not be applied."),
    );

    await expandMember();
    await browserPage.getByText("Only last 24 hours").click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect
      .element(browserPage.getByRole("alert"))
      .toHaveTextContent("The limit could not be applied.");
    expect(browserPage.getByText("Member updated successfully.").elements()).toHaveLength(0);
    await expect.element(browserPage.getByRole("button", { name: "Save" })).toBeVisible();
  });

  it("drops a failed save's message when the card is closed and reopened", async () => {
    mocks.setMemberRoles.mockImplementation(async () => error(400, "Refused."));

    await expandMember();
    await browserPage.getByText("Viewer", { exact: true }).last().click();
    await browserPage.getByRole("button", { name: "Save" }).click();
    await expect.element(browserPage.getByRole("alert")).toHaveTextContent("Refused.");

    await browserPage.getByRole("button", { name: "Close" }).click();
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByText("Viewer", { exact: true }).last().click();

    await expect.element(browserPage.getByRole("button", { name: "Save" })).toBeVisible();
    expect(browserPage.getByRole("alert").elements()).toHaveLength(0);
  });
});

describe("members page listing members", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
  });

  it("does not request or count members for a caller without a members or sharing scope", async () => {
    page.data.effectivePermissions = ["sharing.guest"];
    render(MembersPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Sharing & Privacy" }))
      .toBeVisible();
    expect(mocks.getMembers).not.toHaveBeenCalled();
    expect(browserPage.getByText(/\d+ members?$/).elements()).toHaveLength(0);
  });

  it("requests and counts members for a caller with members.manage", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(mocks.getMembers).toHaveBeenCalled();
  });
});

describe("members page loading roles and pending requests", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
  });

  it("requests roles for a caller with members.invite and no roles.manage", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    await browserPage.getByRole("button", { name: "Create Invite Link" }).click();

    await expect.element(browserPage.getByText("Viewer", { exact: true }).last()).toBeVisible();
    expect(mocks.getRoles).toHaveBeenCalled();
    expect(mocks.getPendingRequests).not.toHaveBeenCalled();
  });

  it("requests neither roles nor pending requests for a caller with only sharing.manage", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(MembersPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Sharing & Privacy" }))
      .toBeVisible();
    expect(mocks.getRoles).not.toHaveBeenCalled();
    expect(mocks.getPendingRequests).not.toHaveBeenCalled();
  });

  it("requests pending requests for a caller with members.manage", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(mocks.getPendingRequests).toHaveBeenCalled();
    expect(mocks.getRoles).toHaveBeenCalled();
  });
});
