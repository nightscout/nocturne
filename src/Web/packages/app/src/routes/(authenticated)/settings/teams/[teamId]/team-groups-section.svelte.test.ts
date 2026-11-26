import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import { SetTeamStaffReachRequestSchema, TeamNameRequestSchema } from "@nocturne/api-client/schemas";
import type { TeamDetail, TeamGroup, TeamSiteAccess, TeamStaffMember } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  createGroup: vi.fn(),
  renameGroup: vi.fn(),
  deleteGroup: vi.fn(),
  addGroupPerson: vi.fn(),
  removeGroupPerson: vi.fn(),
  addGroupStaff: vi.fn(),
  removeGroupStaff: vi.fn(),
  setStaffReach: vi.fn(),
}));

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getTeam: () => ({ current: mocks.team, error: undefined, loading: false, ready: true }),
  createGroup: (arg: unknown) => remoteCommand(mocks.createGroup)(arg),
  renameGroup: (arg: unknown) => remoteCommand(mocks.renameGroup)(arg),
  deleteGroup: (arg: unknown) => remoteCommand(mocks.deleteGroup)(arg),
  addGroupPerson: (arg: unknown) => remoteCommand(mocks.addGroupPerson)(arg),
  removeGroupPerson: (arg: unknown) => remoteCommand(mocks.removeGroupPerson)(arg),
  addGroupStaff: (arg: unknown) => remoteCommand(mocks.addGroupStaff)(arg),
  removeGroupStaff: (arg: unknown) => remoteCommand(mocks.removeGroupStaff)(arg),
  setStaffReach: (arg: unknown) => remoteCommand(mocks.setStaffReach)(arg),
  updateStaff: vi.fn(),
  removeStaff: vi.fn(),
  createStaffInvite: vi.fn(),
  revokeStaffInvite: vi.fn(),
  createAccessLink: vi.fn(),
  revokeAccessLink: vi.fn(),
  renameTeam: vi.fn(),
  removeManager: vi.fn(),
  createManagerInvite: vi.fn(),
  revokeManagerInvite: vi.fn(),
  deleteTeam: vi.fn(),
}));

vi.mock("$app/navigation", () => ({ goto: vi.fn() }));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const me = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const jo = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
const kim = "dddddddd-dddd-dddd-dddd-dddddddddddd";
const pat = "ffffffff-ffff-ffff-ffff-ffffffffffff";
const groupId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
const samSite = "99999999-9999-9999-9999-999999999999";
const leeSite = "88888888-8888-8888-8888-888888888888";

function staffMember(subjectId: string, displayName: string, overrides: Partial<TeamStaffMember> = {}): TeamStaffMember {
  return {
    subjectId,
    displayName,
    narrowing: { limitTo24Hours: false },
    hasEnded: false,
    sitesApproved: 0,
    sitesPending: 0,
    sitesBlocked: 0,
    inNoGroup: false,
    ...overrides,
  };
}

function site(tenantId: string, displayName: string): TeamSiteAccess {
  return {
    tenantId,
    displayName,
    roleNames: ["Viewer"],
    limitTo24Hours: false,
    givenAt: "2026-11-01T00:00:00Z",
    staffRule: "team_decides",
    pendingStaffCount: 0,
    declinedStaffCount: 0,
    hasEnded: false,
  };
}

function group(overrides: Partial<TeamGroup> = {}): TeamGroup {
  return {
    id: groupId,
    name: "Birch cabin",
    people: [{ tenantId: samSite, displayName: "Sam's site", hasEnded: false }],
    staff: [{ subjectId: jo, displayName: "Jo", hasEnded: false }],
    hasNobodyCurrent: false,
    ...overrides,
  };
}

const manager: TeamDetail = {
  id: teamId,
  name: "Clinic",
  managers: [{ subjectId: me, displayName: "Alex" }],
  managerNames: ["Alex"],
  invites: [],
  staff: [staffMember(jo, "Jo"), staffMember(kim, "Kim", { inNoGroup: true })],
  staffInvites: [],
  accessLinks: [],
  sites: [site(samSite, "Sam's site"), site(leeSite, "Lee's site")],
  suggestableRoles: [{ slug: "viewer", name: "Viewer", permissions: ["glucose.read"] }],
  isLastManager: true,
  canDelete: false,
  callerKind: "manager",
  staffReach: "every_person",
  groups: [group()],
  accessesOutsideGroups: 3,
};

const groupRow = () => page.getByRole("listitem", { name: "Birch cabin" });
const dialog = () => page.getByRole("alertdialog");

describe("team page groups", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: me } };
    mocks.team = manager;
    for (const mock of Object.values(mocks)) if (typeof mock === "function") mock.mockReset();
    mocks.createGroup.mockResolvedValue({ id: groupId });
    for (const command of [
      mocks.renameGroup,
      mocks.deleteGroup,
      mocks.addGroupPerson,
      mocks.removeGroupPerson,
      mocks.addGroupStaff,
      mocks.removeGroupStaff,
      mocks.setStaffReach,
    ])
      command.mockResolvedValue(undefined);
  });

  it("says the team has no groups", async () => {
    mocks.team = { ...manager, groups: [] };
    render(TeamPage);

    await expect.element(page.getByText("This team has no groups.")).toBeVisible();
  });

  it("adds a group with the trimmed name, a body the generated schema takes", async () => {
    render(TeamPage);

    await page.getByLabelText("Group name").fill("  Thursday clinic ");
    await page.getByRole("button", { name: "Add group" }).click();

    await vi.waitFor(() =>
      expect(mocks.createGroup).toHaveBeenCalledWith({ teamId, request: { name: "Thursday clinic" } }),
    );
    expect(TeamNameRequestSchema.safeParse(mocks.createGroup.mock.calls[0][0].request).success).toBe(true);
  });

  it("shows the sentence for a name another group has", async () => {
    mocks.createGroup.mockRejectedValue({ status: 409, body: { code: "team_group_name_taken" } });
    render(TeamPage);

    await page.getByLabelText("Group name").fill("birch cabin");
    await page.getByRole("button", { name: "Add group" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Another group of this team has this name. Use a different name.");
  });

  it("renames a group, a body the generated schema takes", async () => {
    render(TeamPage);

    await groupRow().getByRole("button", { name: "Rename the group Birch cabin" }).click();
    const form = page.getByRole("form", { name: "Rename the group Birch cabin" });
    await form.getByLabelText("Group name").fill("Oak cabin");
    await form.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() =>
      expect(mocks.renameGroup).toHaveBeenCalledWith({ teamId, groupId, request: { name: "Oak cabin" } }),
    );
    expect(TeamNameRequestSchema.safeParse(mocks.renameGroup.mock.calls[0][0].request).success).toBe(true);
  });

  it("removes a group only after a confirmation that says no access changes while staff see every person", async () => {
    render(TeamPage);

    await groupRow().getByRole("button", { name: "Remove the group Birch cabin" }).click();
    await expect
      .element(dialog())
      .toHaveTextContent("Remove the group Birch cabin? No access changes now, because staff see every person the team follows.");
    expect(mocks.deleteGroup).not.toHaveBeenCalled();
    await dialog().getByRole("button", { name: "Remove group" }).click();

    await vi.waitFor(() => expect(mocks.deleteGroup).toHaveBeenCalledWith({ teamId, groupId }));
  });

  it("says what a removal takes while staff see only the people in their groups", async () => {
    mocks.team = { ...manager, staffReach: "their_groups" };
    render(TeamPage);

    await groupRow().getByRole("button", { name: "Remove the group Birch cabin" }).click();

    await expect
      .element(dialog())
      .toHaveTextContent(
        "Remove the group Birch cabin? Its staff stop seeing its people through the team, unless they are in another group together.",
      );
  });

  it("offers the removal of a group with nobody current and removes nothing on its own", async () => {
    mocks.team = {
      ...manager,
      groups: [
        group({
          people: [{ tenantId: samSite, displayName: "Sam's site", hasEnded: true }],
          staff: [],
          hasNobodyCurrent: true,
        }),
      ],
    };
    render(TeamPage);

    await expect
      .element(groupRow().getByText("No person or staff member in this group has current access. You can remove the group."))
      .toBeVisible();
    await expect.element(groupRow().getByRole("button", { name: "Remove the group Birch cabin" })).toBeEnabled();
    expect(groupRow().getByText("No one is in this group.").elements()).toHaveLength(0);
    expect(mocks.deleteGroup).not.toHaveBeenCalled();
  });

  it("says an empty group has no one in it, and offers no removal for it as a group with nobody current", async () => {
    mocks.team = { ...manager, groups: [group({ people: [], staff: [], hasNobodyCurrent: true })] };
    render(TeamPage);

    await expect.element(groupRow().getByText("No one is in this group.")).toBeVisible();
    expect(groupRow().getByText("You can remove the group.", { exact: false }).elements()).toHaveLength(0);
    expect(groupRow().getByText("No person is in this group.").elements()).toHaveLength(0);
    expect(groupRow().getByText("No staff member is in this group.").elements()).toHaveLength(0);
    await expect.element(groupRow().getByRole("button", { name: "Remove the group Birch cabin" })).toBeEnabled();
  });

  it("adds and removes a person and a staff member, offering only who is not in the group", async () => {
    render(TeamPage);

    await groupRow().getByTestId("group-person-picker").click();
    await expect.element(page.getByRole("option", { name: "Lee's site" })).toBeVisible();
    expect(page.getByRole("option", { name: "Sam's site" }).elements()).toHaveLength(0);
    await page.getByRole("option", { name: "Lee's site" }).click();
    await groupRow().getByRole("button", { name: "Add person" }).click();
    await vi.waitFor(() =>
      expect(mocks.addGroupPerson).toHaveBeenCalledWith({ teamId, groupId, tenantId: leeSite }),
    );

    await groupRow().getByTestId("group-staff-picker").click();
    await page.getByRole("option", { name: "Kim" }).click();
    await groupRow().getByRole("button", { name: "Add staff member" }).click();
    await vi.waitFor(() => expect(mocks.addGroupStaff).toHaveBeenCalledWith({ teamId, groupId, subjectId: kim }));

    await groupRow().getByRole("button", { name: "Remove Sam's site from Birch cabin" }).click();
    await vi.waitFor(() =>
      expect(mocks.removeGroupPerson).toHaveBeenCalledWith({ teamId, groupId, tenantId: samSite }),
    );
    await groupRow().getByRole("button", { name: "Remove Jo from Birch cabin" }).click();
    await vi.waitFor(() => expect(mocks.removeGroupStaff).toHaveBeenCalledWith({ teamId, groupId, subjectId: jo }));
  });

  it("says why nobody can be added when every person and staff member is in the group", async () => {
    mocks.team = {
      ...manager,
      staff: [staffMember(jo, "Jo")],
      sites: [site(samSite, "Sam's site")],
    };
    render(TeamPage);

    await expect.element(groupRow().getByText("Every person that the team follows is in this group.")).toBeVisible();
    await expect.element(groupRow().getByText("Every current staff member is in this group.")).toBeVisible();
    expect(groupRow().getByRole("button", { name: "Add person" }).elements()).toHaveLength(0);
  });

  it("confirms a switch to groups with the server's count, then sends a body the generated schema takes", async () => {
    render(TeamPage);

    await page.getByRole("radio", { name: /Staff see only the people in their groups/ }).click();
    expect(mocks.setStaffReach).not.toHaveBeenCalled();
    await page.getByRole("button", { name: "Change setting" }).click();
    await expect
      .element(dialog())
      .toHaveTextContent("Staff lose 3 accesses through the team to people that they are not in a group with.");
    await expect
      .element(dialog())
      .toHaveTextContent("A new staff member sees no one through the team until you add them to a group.");
    await dialog().getByRole("button", { name: "Change setting" }).click();

    await vi.waitFor(() =>
      expect(mocks.setStaffReach).toHaveBeenCalledWith({ teamId, request: { staffReach: "their_groups" } }),
    );
    expect(SetTeamStaffReachRequestSchema.safeParse(mocks.setStaffReach.mock.calls[0][0].request).success).toBe(true);
  });

  it("states one access and no access as the server counts them", async () => {
    mocks.team = { ...manager, accessesOutsideGroups: 1 };
    render(TeamPage);

    await page.getByRole("radio", { name: /Staff see only the people in their groups/ }).click();
    await page.getByRole("button", { name: "Change setting" }).click();
    await expect
      .element(dialog())
      .toHaveTextContent("Staff lose 1 access through the team to a person that they are not in a group with.");
  });

  it("says that no staff member loses access through the team when the server counts none", async () => {
    mocks.team = { ...manager, accessesOutsideGroups: 0 };
    render(TeamPage);

    await page.getByRole("radio", { name: /Staff see only the people in their groups/ }).click();
    await page.getByRole("button", { name: "Change setting" }).click();
    await expect.element(dialog()).toHaveTextContent("No staff member loses access through the team now.");
  });

  it("confirms a switch back to every person", async () => {
    mocks.team = { ...manager, staffReach: "their_groups", accessesOutsideGroups: 0 };
    render(TeamPage);

    await page.getByRole("radio", { name: /Staff see every person the team follows/ }).click();
    await page.getByRole("button", { name: "Change setting" }).click();
    await expect
      .element(dialog())
      .toHaveTextContent("Staff see every person that the team follows again, as each site allows.");
    await dialog().getByRole("button", { name: "Change setting" }).click();

    await vi.waitFor(() =>
      expect(mocks.setStaffReach).toHaveBeenCalledWith({ teamId, request: { staffReach: "every_person" } }),
    );
  });

  it("keeps the setting when the manager changes their mind", async () => {
    render(TeamPage);

    await page.getByRole("radio", { name: /Staff see only the people in their groups/ }).click();
    await page.getByRole("button", { name: "Keep the setting" }).click();

    await expect.element(page.getByRole("radio", { name: /Staff see every person the team follows/ })).toBeChecked();
    expect(mocks.setStaffReach).not.toHaveBeenCalled();
  });

  it("says a staff member in no group sees no one, only while staff see only their groups", async () => {
    mocks.team = { ...manager, staffReach: "their_groups" };
    const { unmount } = render(TeamPage);

    const kimRow = page.getByRole("listitem").filter({ hasText: "Kim" }).first();
    await expect.element(kimRow.getByText("In no group. Sees no one through the team.")).toBeVisible();
    expect(
      page.getByRole("listitem").filter({ hasText: "Jo" }).first().getByText("In no group.").elements(),
    ).toHaveLength(0);
    unmount();

    mocks.team = manager;
    render(TeamPage);
    await expect.element(page.getByLabelText("Group name")).toBeVisible();
    expect(page.getByText("In no group. Sees no one through the team.").elements()).toHaveLength(0);
  });

  it("words a staff member's sites as whom they see through the team only while staff see only their groups", async () => {
    const staff = [
      staffMember(jo, "Jo", { sitesApproved: 2, sitesPending: 1 }),
      staffMember(kim, "Kim", { sitesBlocked: 1 }),
      staffMember(pat, "Pat", { sitesApproved: 1 }),
    ];
    mocks.team = { ...manager, staff, staffReach: "their_groups" };
    const { unmount } = render(TeamPage);

    const row = (name: string) => page.getByRole("listitem").filter({ hasText: name }).first();
    await expect.element(row("Jo")).toHaveTextContent("Sees 2 people through the team.");
    await expect.element(row("Jo")).toHaveTextContent("Waits for approval at 1 site.");
    await expect.element(row("Kim")).toHaveTextContent("Sees no one through the team.");
    await expect.element(row("Kim")).toHaveTextContent("Stopped by 1 site.");
    await expect.element(row("Pat")).toHaveTextContent("Sees 1 person through the team.");
    await expect.element(row("Jo")).not.toHaveTextContent("Approved at");
    await expect.element(row("Kim")).not.toHaveTextContent("Not approved at any site.");
    unmount();

    mocks.team = { ...manager, staff };
    render(TeamPage);
    await expect.element(row("Jo")).toHaveTextContent("Approved at 2 sites.");
    await expect.element(row("Kim")).toHaveTextContent("Stopped by 1 site.");
    await expect.element(row("Jo")).not.toHaveTextContent("through the team");
    await expect.element(row("Kim")).not.toHaveTextContent("through the team");
  });
});
