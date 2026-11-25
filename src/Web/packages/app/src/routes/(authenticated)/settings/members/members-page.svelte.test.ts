import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { page } from "$app/state";
import {
  MemberReasonKind,
  RoleAccessLevel,
  type LockedOutMember,
  type MembershipRequestDto,
  type TenantMemberDto,
  type TenantRoleDto,
  type TenantTeamAccess,
} from "@nocturne/api-client";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { accessEndInstant } from "#lib/components/members/access-end.js";

const mocks = vi.hoisted(() => ({
  getMembers: vi.fn(),
  getRoles: vi.fn(),
  getPendingRequests: vi.fn(),
  setMemberRoles: vi.fn(),
  setMemberPermissions: vi.fn(),
  setMemberLimitTo24Hours: vi.fn(),
  setMemberAccessEnd: vi.fn(),
  approveRequest: vi.fn(),
  getLockedOutMembers: vi.fn(),
  stopTeamAccess: vi.fn(),
  refreshTeamAccess: vi.fn(),
  approveTeamStaff: vi.fn(),
  declineTeamStaff: vi.fn(),
  setTeamStaffRule: vi.fn(),
  editTeamAccess: vi.fn(),
}));

let members: TenantMemberDto[];
let lockedOut: LockedOutMember[] = [];
let pendingRequests: MembershipRequestDto[] = [];
let teamAccess: TenantTeamAccess[] = [];

const viewer: TenantRoleDto = {
  id: "22222222-2222-2222-2222-222222222222",
  name: "Viewer",
  slug: "viewer",
  permissions: ["glucose.read"],
  accessLevel: RoleAccessLevel.ReadOnly,
  canGrant: true,
};

const owner: TenantRoleDto = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Owner",
  slug: "owner",
  permissions: ["*"],
  accessLevel: RoleAccessLevel.Manage,
  canGrant: true,
};
let roles: TenantRoleDto[] = [viewer];

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  getMembers: () => {
    mocks.getMembers();
    return remoteQuery(() => members);
  },
  listInvites: () => remoteQuery(() => []),
  getTeamAccess: () => {
    const query = remoteQuery(() => teamAccess);
    const refresh = query.refresh.bind(query);
    query.refresh = () => {
      mocks.refreshTeamAccess();
      return refresh();
    };
    return query;
  },
  stopTeamAccess: (teamId: string) => mocks.stopTeamAccess(teamId),
  approveTeamStaff: mocks.approveTeamStaff,
  declineTeamStaff: mocks.declineTeamStaff,
  setTeamStaffRule: mocks.setTeamStaffRule,
  editTeamAccess: mocks.editTeamAccess,
  revokeInvite: () => Promise.resolve(),
  createInvite: vi.fn(),
  removeMember: () => Promise.resolve(),
  setMemberRoles: mocks.setMemberRoles,
  setMemberPermissions: mocks.setMemberPermissions,
  setMemberLimitTo24Hours: mocks.setMemberLimitTo24Hours,
  setMemberAccessEnd: mocks.setMemberAccessEnd,
}));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => {
    mocks.getRoles();
    return remoteQuery(() => roles);
  },
}));
vi.mock("#lib/api/generated/shareLinks.generated.remote.js", () => ({
  getShareLink: () => remoteQuery(() => null),
}));
vi.mock("#lib/api/generated/membershipRequests.generated.remote.js", () => ({
  getPendingRequests: () => {
    mocks.getPendingRequests();
    return remoteQuery(() => pendingRequests);
  },
  approveRequest: mocks.approveRequest,
  denyRequest: () => Promise.resolve(),
}));
vi.mock("#lib/api/generated/memberRecoveries.generated.remote.js", () => ({
  getLockedOutMembers: () => {
    mocks.getLockedOutMembers();
    return remoteQuery(() => lockedOut);
  },
  issueRecoveryCode: vi.fn(),
}));
vi.mock("$app/state", async () => {
  const stub = await vi.importActual<typeof import("$app/state")>("$app/state");
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  return { ...stub, page: reactiveFields({ ...stub.page }) };
});
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));
vi.mock("#lib/components/members/PublicAccessCard.svelte", () => ({ default: () => {} }));
vi.mock("#lib/components/members/MembershipRequestsCard.svelte", () => ({ default: () => {} }));
vi.mock("#lib/components/members/GuestLinksSection.svelte", () => ({ default: () => {} }));
vi.mock("#lib/components/members/RolesSection.svelte", () => ({ default: () => {} }));

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
    mocks.setMemberAccessEnd.mockResolvedValue(undefined);
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

  it("sets the end through the access end command and nothing else when only the end changed", async () => {
    render(MembersPage);
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberAccessEnd).toHaveBeenCalledWith({
      id: member.id,
      request: { accessEndsAt: accessEndInstant("2099-07-14") },
    });
    expect(mocks.setMemberRoles).not.toHaveBeenCalled();
    expect(mocks.setMemberLimitTo24Hours).not.toHaveBeenCalled();
  });

  it("removes the end by sending null", async () => {
    members = [{ ...member, accessEndsAt: accessEndInstant("2099-07-14") }];
    render(MembersPage);
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByRole("button", { name: "Remove the end date" }).click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberAccessEnd).toHaveBeenCalledWith({
      id: member.id,
      request: { accessEndsAt: null },
    });
  });

  it("reports an end the server refused on the card instead of claiming success", async () => {
    mocks.setMemberAccessEnd.mockImplementation(async () =>
      error(403, "Your access ends on a date, so you cannot give anyone access that lasts longer than yours."),
    );

    render(MembersPage);
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByRole("alert")).toBeVisible();
    expect(browserPage.getByText("Member updated successfully.").elements()).toHaveLength(0);
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

  it("tells a caller who has access only through a team that they cannot give access", async () => {
    mocks.setMemberLimitTo24Hours.mockImplementation(async () =>
      error(409, "Refused.", { code: "caller_access_from_team" }),
    );

    await expandMember();
    await browserPage.getByText("Only last 24 hours").click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect
      .element(browserPage.getByRole("alert"))
      .toHaveTextContent("You have access to this site only through a team, so you cannot give access to anyone.");
    expect(browserPage.getByText(/This member has access only through a team/).elements()).toHaveLength(0);
  });

  it("tells a caller that the member they change has access only through a team", async () => {
    mocks.setMemberLimitTo24Hours.mockImplementation(async () =>
      error(409, "Refused.", { code: "member_access_from_team" }),
    );

    await expandMember();
    await browserPage.getByText("Only last 24 hours").click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect
      .element(browserPage.getByRole("alert"))
      .toHaveTextContent("This member has access only through a team. To change it, stop the team's access to this site.");
    expect(browserPage.getByText(/You have access to this site only through a team/).elements()).toHaveLength(0);
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

describe("members page ordering the commands of a save", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["members.manage"];
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    roles = [viewer, owner];
    mocks.setMemberRoles.mockResolvedValue(undefined);
    mocks.setMemberPermissions.mockResolvedValue(undefined);
    mocks.setMemberLimitTo24Hours.mockResolvedValue(undefined);
    mocks.setMemberAccessEnd.mockResolvedValue(undefined);
  });

  afterEach(() => {
    roles = [viewer];
  });

  it("removes the end before it makes the member an owner", async () => {
    members = [{ ...member, accessEndsAt: accessEndInstant("2099-07-14") }];
    render(MembersPage);
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByRole("button", { name: "Remove the end date" }).click();
    await browserPage.getByRole("checkbox", { name: "Owner" }).click();
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberAccessEnd).toHaveBeenCalledWith({ id: member.id, request: { accessEndsAt: null } });
    expect(mocks.setMemberAccessEnd.mock.invocationCallOrder[0]).toBeLessThan(
      mocks.setMemberRoles.mock.invocationCallOrder[0],
    );
  });

  it("changes the roles before it sets an end on a member who stops being an owner", async () => {
    members = [
      {
        ...member,
        roles: [{ roleId: owner.id, slug: owner.slug, name: owner.name }],
      },
    ];
    render(MembersPage);
    await browserPage.getByRole("button", { name: "Edit" }).click();
    await browserPage.getByRole("checkbox", { name: "Owner" }).click();
    await browserPage.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await browserPage.getByRole("button", { name: "Save" }).click();

    await expect.element(browserPage.getByText("Member updated successfully.")).toBeVisible();
    expect(mocks.setMemberAccessEnd).toHaveBeenCalledWith({
      id: member.id,
      request: { accessEndsAt: accessEndInstant("2099-07-14") },
    });
    expect(mocks.setMemberRoles.mock.invocationCallOrder[0]).toBeLessThan(
      mocks.setMemberAccessEnd.mock.invocationCallOrder[0],
    );
  });
});

describe("members page approving a request", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["members.manage"];
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
    pendingRequests = [{ id: "77777777-7777-7777-7777-777777777777", subjectName: "Sam" }];
    mocks.approveRequest.mockResolvedValue(undefined);
  });

  afterEach(() => {
    pendingRequests = [];
  });

  it("sends the instant the picked day ends with the approval", async () => {
    render(MembersPage);
    await browserPage.getByRole("checkbox", { name: "Viewer" }).click();
    await browserPage.getByLabelText("Access ends (optional)").fill("2099-07-14");
    await browserPage.getByRole("button", { name: "Approve" }).click();

    await expect.element(browserPage.getByText("Membership request approved.")).toBeVisible();
    expect(mocks.approveRequest).toHaveBeenCalledWith({
      id: pendingRequests[0].id,
      request: { roleIds: [viewer.id], accessEndsAt: accessEndInstant("2099-07-14") },
    });
  });
});

describe("members page when the caller's own access ends", () => {
  const ownSubjectId = "55555555-5555-5555-5555-555555555555";
  const last = "2099-07-14";

  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["members.manage", "members.invite"];
    page.data.user = { subjectId: ownSubjectId };
    members = [
      member,
      {
        ...member,
        id: "66666666-6666-6666-6666-666666666666",
        subjectId: ownSubjectId,
        name: "Casey",
        accessEndsAt: accessEndInstant(last),
      },
    ];
  });

  it("limits another member's end to the caller's own last day", async () => {
    render(MembersPage);
    await expect.element(browserPage.getByText("Casey")).toBeVisible();
    await browserPage.getByRole("button", { name: "Edit" }).first().click();

    await expect.element(browserPage.getByLabelText("Access ends (optional)")).toHaveAttribute("max", last);
  });

  it("reads the ceiling from the caller's own row, not another member's", async () => {
    members = [
      { ...member, accessEndsAt: accessEndInstant("2098-01-01") },
      { ...members[1], accessEndsAt: undefined },
    ];
    render(MembersPage);
    await expect.element(browserPage.getByText("Casey")).toBeVisible();
    await browserPage.getByRole("button", { name: "Edit" }).first().click();

    const field = browserPage.getByLabelText("Access ends (optional)");
    await expect.element(field).toBeVisible();
    await expect.element(field).not.toHaveAttribute("max");
    await expect.element(browserPage.getByText("Your own access ends", { exact: false })).not.toBeInTheDocument();
  });

  it("limits a new invite to the caller's own last day", async () => {
    render(MembersPage);
    await expect.element(browserPage.getByText("Casey")).toBeVisible();
    await browserPage.getByRole("button", { name: "Create invite link" }).click();

    await expect.element(browserPage.getByLabelText("Access ends (optional)")).toHaveAttribute("max", last);
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
      .element(browserPage.getByRole("heading", { name: "Sharing & privacy" }))
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
    await browserPage.getByRole("button", { name: "Create invite link" }).click();

    await expect.element(browserPage.getByText("Viewer", { exact: true }).last()).toBeVisible();
    expect(mocks.getRoles).toHaveBeenCalled();
    expect(mocks.getPendingRequests).not.toHaveBeenCalled();
  });

  it("requests neither roles nor pending requests for a caller with only sharing.manage", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(MembersPage);

    await expect
      .element(browserPage.getByRole("heading", { name: "Sharing & privacy" }))
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

describe("members page listing members who cannot sign in", () => {
  const stuck: LockedOutMember = {
    subjectId: "66666666-6666-6666-6666-666666666666",
    name: "Sam",
    username: "sam",
    hasRecoveryCodes: false,
  };

  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
    lockedOut = [stuck];
    page.data.isHandedOffSession = false;
  });

  it("lists them on a handed-off session and offers a sign-in in place of the recovery link", async () => {
    page.data.effectivePermissions = ["members.manage"];
    page.data.isHandedOffSession = true;
    render(MembersPage);

    await expect.element(browserPage.getByText("Sam", { exact: true })).toBeVisible();
    await expect.element(browserPage.getByText("Sign in again to make a recovery link.")).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Create link" }).elements()).toHaveLength(0);
    page.data.isHandedOffSession = false;
  });

  it("shows them to a caller with members.manage", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Members who cannot sign in")).toBeVisible();
    await expect.element(browserPage.getByText("Sam", { exact: true })).toBeVisible();
  });

  it("shows nothing to a caller with members.manage when nobody is stuck", async () => {
    page.data.effectivePermissions = ["members.manage"];
    lockedOut = [];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(mocks.getLockedOutMembers).toHaveBeenCalled();
    expect(browserPage.getByText("Members who cannot sign in").elements()).toHaveLength(0);
  });

  it("neither requests nor shows them for a caller who can only invite", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(mocks.getLockedOutMembers).not.toHaveBeenCalled();
    expect(browserPage.getByText("Members who cannot sign in").elements()).toHaveLength(0);
  });
});

describe("members page linking to the member history", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
    lockedOut = [];
  });

  it("links the page and each member to the history for a caller with members.manage", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    const historyLink = browserPage.getByRole("link", { name: /Member history/ });
    await expect.element(historyLink).toHaveAttribute("href", "/settings/members/history");
    await expect
      .element(browserPage.getByRole("link", { name: "View history for Alex" }))
      .toHaveAttribute("href", expect.stringContaining(`/settings/members/history?member=${member.subjectId}`));
  });

  it("offers neither link without members.manage", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("link", { name: /Member history/ }).elements()).toHaveLength(0);
    expect(browserPage.getByRole("link", { name: "View history for Alex" }).elements()).toHaveLength(0);
  });
});

describe("members page recovery email badge", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [{ ...member, hasConfirmedRecoveryEmail: false }];
    lockedOut = [];
  });

  it("marks a member with no confirmed recovery email for a caller with members.manage", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("No recovery email")).toBeVisible();
  });

  it("shows nothing for a caller who can only share, even if the list carried the flag", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Alex", { exact: true })).toBeVisible();
    expect(browserPage.getByText("No recovery email").elements()).toHaveLength(0);
  });

  it("shows nothing for a member the list does not report on", async () => {
    page.data.effectivePermissions = ["members.manage"];
    members = [{ ...member, hasConfirmedRecoveryEmail: undefined }];
    render(MembersPage);

    await expect.element(browserPage.getByText("Alex", { exact: true })).toBeVisible();
    expect(browserPage.getByText("No recovery email").elements()).toHaveLength(0);
  });
});

describe("members page listing the teams with access", () => {
  const clinicId = "66666666-6666-6666-6666-666666666666";
  const clinic: TenantTeamAccess = {
    teamId: clinicId,
    teamName: "Clinic",
    roles: [
      { id: viewer.id, name: "Viewer", slug: "viewer" },
      { id: "77777777-7777-7777-7777-777777777777", name: "Caretaker", slug: "caretaker" },
    ],
    limitTo24Hours: true,
    accessEndsAt: "2099-01-01T00:00:00Z",
    hasEnded: false,
    approvedByName: "Sam",
    givenAt: "2026-11-01T00:00:00Z",
  };

  beforeEach(() => {
    vi.clearAllMocks();
    page.data.user = { subjectId: "55555555-5555-5555-5555-555555555555" };
    members = [member];
    lockedOut = [];
    teamAccess = [clinic];
    mocks.stopTeamAccess.mockResolvedValue(undefined);
  });

  afterEach(() => {
    teamAccess = [];
  });

  it("lists a team as one row in its own section, apart from the people", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByRole("heading", { name: "Teams" })).toBeVisible();
    await expect.element(browserPage.getByText("Clinic", { exact: true })).toBeVisible();
    await expect.element(browserPage.getByText("Roles: Viewer, Caretaker")).toBeVisible();
    await expect.element(browserPage.getByText("Only the last 24 hours", { exact: false })).toBeVisible();
    await expect.element(browserPage.getByText("Access ends", { exact: false })).toBeVisible();
    await expect.element(browserPage.getByText("Approved by Sam", { exact: false })).toBeVisible();
    await expect.element(browserPage.getByText("1 member", { exact: true })).toBeVisible();
    expect(browserPage.getByText("Clinic", { exact: true }).elements()).toHaveLength(1);
  });

  it("says a team's access has ended when the API says so", async () => {
    teamAccess = [{ ...clinic, hasEnded: true }];
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Access ended", { exact: false })).toBeVisible();
    expect(browserPage.getByText("Access ends", { exact: false }).elements()).toHaveLength(0);
  });

  it("shows no Teams section when no team has access", async () => {
    teamAccess = [];
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Alex", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("heading", { name: "Teams" }).elements()).toHaveLength(0);
  });

  it("stops the access only after a confirmation that names the team and states what follows", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Stop the access of Clinic" }).click();

    const dialog = browserPage.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Stop the access of Clinic?");
    await expect.element(dialog).toHaveTextContent("The staff of the team lose access at once.");
    await expect.element(dialog).toHaveTextContent("A manager of the team must ask again.");
    expect(mocks.stopTeamAccess).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Stop access" }).click();

    await vi.waitFor(() => expect(mocks.stopTeamAccess).toHaveBeenCalledWith(clinicId));
  });

  it("does not stop the access when the confirmation is cancelled", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Stop the access of Clinic" }).click();
    await browserPage.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();

    await expect.element(browserPage.getByRole("alertdialog")).not.toBeInTheDocument();
    expect(mocks.stopTeamAccess).not.toHaveBeenCalled();
  });

  it("shows the team but no Stop access to a caller who may only invite", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Clinic", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: /Stop the access of/ }).elements()).toHaveLength(0);
  });

  it("shows the team but no Stop access to a caller who may only manage sharing", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Clinic", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: /Stop the access of/ }).elements()).toHaveLength(0);
  });

  it("reports a refused stop on the page", async () => {
    mocks.stopTeamAccess.mockRejectedValue(new Error("down"));
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Stop the access of Clinic" }).click();
    await browserPage.getByRole("alertdialog").getByRole("button", { name: "Stop access" }).click();

    await expect
      .element(browserPage.getByText("Failed to stop the access of the team. Try again.", { exact: false }))
      .toBeVisible();
  });

  it("says the team no longer has access, and reads the list again, when the stop finds it gone", async () => {
    mocks.stopTeamAccess.mockRejectedValue({ status: 404, body: { message: "Not found" } });
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Stop the access of Clinic" }).click();
    await browserPage.getByRole("alertdialog").getByRole("button", { name: "Stop access" }).click();

    await expect.element(browserPage.getByText("This team no longer has access.")).toBeVisible();
    expect(mocks.refreshTeamAccess).toHaveBeenCalled();
  });

  it("says the person may not do this, and does not read the list again, when the stop is refused", async () => {
    mocks.stopTeamAccess.mockRejectedValue({
      status: 403,
      body: { message: "English detail", code: "missing_scope" },
    });
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Stop the access of Clinic" }).click();
    await browserPage.getByRole("alertdialog").getByRole("button", { name: "Stop access" }).click();

    await expect.element(browserPage.getByText("You do not have permission to do this.")).toBeVisible();
    expect(browserPage.getByText("English detail").elements()).toHaveLength(0);
    expect(mocks.refreshTeamAccess).not.toHaveBeenCalled();
  });

  it("keeps the Teams section outside the Members & invites block", async () => {
    page.data.effectivePermissions = ["members.manage"];
    render(MembersPage);

    const teams = browserPage.getByRole("heading", { name: "Teams" });
    await expect.element(teams).toBeVisible();
    const block = browserPage.getByRole("heading", { name: "Members & invites" }).element().parentElement!;
    expect(block.contains(teams.element())).toBe(false);
  });
});

describe("members page deciding about a team and its staff", () => {
  const ownSubjectId = "55555555-5555-5555-5555-555555555555";
  const clinicId = "66666666-6666-6666-6666-666666666666";
  const nightsId = "88888888-8888-8888-8888-888888888888";
  const wrenId = "99999999-9999-9999-9999-999999999999";
  const ownerRole = { roleId: owner.id, name: "Owner", slug: "owner", permissions: ["*"] };
  const viewerRole = { roleId: viewer.id, name: "Viewer", slug: "viewer", permissions: ["glucose.read"] };
  const team = (teamId: string, teamName: string): TenantTeamAccess => ({
    teamId,
    teamName,
    roles: [{ id: viewer.id, name: "Viewer", slug: "viewer" }],
    limitTo24Hours: false,
    hasEnded: false,
    approvedByName: "Sam",
    givenAt: "2026-11-01T00:00:00Z",
    teamManagesStaff: false,
    staff: [{ subjectId: wrenId, displayName: "Wren", status: "pending" }],
  });
  const caller = (reasons: TenantMemberDto["reasons"]): TenantMemberDto => ({
    ...member,
    id: "77777777-7777-7777-7777-777777777777",
    subjectId: ownSubjectId,
    name: "Casey",
    roles: [ownerRole],
    hasDirect: true,
    reasons,
  });
  const directOwner = { kind: MemberReasonKind.Direct, roles: [ownerRole], hasEnded: false };

  beforeEach(() => {
    vi.clearAllMocks();
    page.data.effectivePermissions = ["members.manage"];
    page.data.user = { subjectId: ownSubjectId };
    page.data.limitTo24Hours = false;
    lockedOut = [];
    pendingRequests = [];
    roles = [viewer, owner];
    teamAccess = [team(clinicId, "Clinic")];
    members = [member, caller([directOwner])];
  });

  afterEach(() => {
    teamAccess = [];
    roles = [viewer];
    page.url = new URL("http://localhost");
  });

  it("defines staff and lists a team's staff to every reader of the member list", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect
      .element(
        browserPage.getByText(
          "Staff are the people who work for a team. Approved staff can see this site through the team.",
        ),
      )
      .toBeVisible();
    await expect.element(browserPage.getByText("Waits for an owner's approval")).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Approve Wren" }).elements()).toHaveLength(0);
  });

  it("gives an owner by a direct reason the controls for the team and its staff", async () => {
    render(MembersPage);

    await expect.element(browserPage.getByRole("button", { name: "Approve Wren" })).toBeVisible();
    await expect.element(browserPage.getByText("Waits for your approval")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Change the access of Clinic" })).toBeVisible();
    await expect.element(browserPage.getByRole("switch")).toBeVisible();
  });

  it("gives no control to a member who manages members but is not an owner", async () => {
    members = [member, caller([{ kind: MemberReasonKind.Direct, roles: [viewerRole], hasEnded: false }])];
    render(MembersPage);

    await expect.element(browserPage.getByText("Wren", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Approve Wren" }).elements()).toHaveLength(0);
    expect(browserPage.getByRole("button", { name: "Change the access of Clinic" }).elements()).toHaveLength(0);
    expect(browserPage.getByRole("switch").elements()).toHaveLength(0);
  });

  it("gives no control to an owner whose direct reason has ended", async () => {
    members = [member, caller([{ ...directOwner, hasEnded: true }])];
    render(MembersPage);

    await expect.element(browserPage.getByText("Wren", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Approve Wren" }).elements()).toHaveLength(0);
  });

  it("gives no control to a caller whose owner role comes only from a team's reason", async () => {
    members = [member, caller([{ kind: MemberReasonKind.Team, teamId: clinicId, roles: [ownerRole], hasEnded: false }])];
    render(MembersPage);

    await expect.element(browserPage.getByText("Wren", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Approve Wren" }).elements()).toHaveLength(0);
  });

  it("gives no control to an owner without members.manage", async () => {
    page.data.effectivePermissions = ["members.invite"];
    render(MembersPage);

    await expect.element(browserPage.getByText("Wren", { exact: true })).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Approve Wren" }).elements()).toHaveLength(0);
  });

  it("offers the edit only the roles the API lets the caller give a team", async () => {
    roles = [{ ...viewer, canGiveToTeam: true }, { ...owner, canGiveToTeam: false }];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Change the access of Clinic" }).click();

    const form = browserPage.getByRole("form", { name: "Change the access of Clinic" });
    await expect.element(form.getByRole("checkbox", { name: "Viewer" })).toBeChecked();
    expect(form.getByRole("checkbox", { name: "Owner" }).elements()).toHaveLength(0);
  });

  it("keeps the edit's 24-hour limit on for a caller who reads only the last 24 hours", async () => {
    roles = [{ ...viewer, canGiveToTeam: true }];
    page.data.limitTo24Hours = true;
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Change the access of Clinic" }).click();

    const limit = browserPage.getByRole("checkbox", { name: "Only the last 24 hours" });
    await expect.element(limit).toBeChecked();
    await expect.element(limit).toBeDisabled();
  });

  it("scrolls to and focuses the team the page was opened for", async () => {
    const scrolled = vi.spyOn(Element.prototype, "scrollIntoView");
    teamAccess = [team(clinicId, "Clinic"), team(nightsId, "Nights")];
    page.url = new URL(`http://localhost/settings/members?team=${nightsId}`);
    render(MembersPage);

    const linked = browserPage.getByRole("group", { name: "Nights" });
    await expect.element(linked).toHaveFocus();
    expect(scrolled.mock.contexts).toContain(linked.element());
    scrolled.mockRestore();
  });

  it("scrolls to and focuses a second team when a new link opens the same page", async () => {
    teamAccess = [team(clinicId, "Clinic"), team(nightsId, "Nights")];
    page.url = new URL(`http://localhost/settings/members?team=${clinicId}`);
    render(MembersPage);
    await expect.element(browserPage.getByRole("group", { name: "Clinic" })).toHaveFocus();
    const scrolled = vi.spyOn(Element.prototype, "scrollIntoView");

    page.url = new URL(`http://localhost/settings/members?team=${nightsId}`);

    const linked = browserPage.getByRole("group", { name: "Nights" });
    await expect.element(linked).toHaveFocus();
    expect(scrolled.mock.contexts).toContain(linked.element());
    scrolled.mockRestore();
  });

  it("bounds the end of the edit by the caller's direct reason, not by a team reason that ends later", async () => {
    roles = [{ ...viewer, canGiveToTeam: true }];
    members = [
      member,
      {
        ...caller([
          { ...directOwner, accessEndsAt: accessEndInstant("2099-07-14") },
          { kind: MemberReasonKind.Team, teamId: nightsId, roles: [viewerRole], hasEnded: false },
        ]),
        accessEndsAt: undefined,
      },
    ];
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Change the access of Clinic" }).click();

    const form = browserPage.getByRole("form", { name: "Change the access of Clinic" });
    await expect.element(form.getByLabelText("Access ends", { exact: true })).toHaveAttribute("max", "2099-07-14");
    await expect.element(form.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("changes nothing when the page is opened for a team it does not list", async () => {
    const scrolled = vi.spyOn(Element.prototype, "scrollIntoView");
    page.url = new URL("http://localhost/settings/members?team=00000000-0000-0000-0000-000000000000");
    render(MembersPage);

    const listed = browserPage.getByRole("group", { name: "Clinic" });
    await expect.element(listed).toBeVisible();
    await expect.element(listed).not.toHaveFocus();
    expect(scrolled).not.toHaveBeenCalled();
    scrolled.mockRestore();
  });

  it("approves through the page with the team and the staff member", async () => {
    mocks.approveTeamStaff.mockResolvedValue(undefined);
    render(MembersPage);

    await browserPage.getByRole("button", { name: "Approve Wren" }).click();

    await expect.element(browserPage.getByText("Staff member approved.")).toBeVisible();
    expect(mocks.approveTeamStaff).toHaveBeenCalledWith({ teamId: clinicId, subjectId: wrenId });
  });
});
