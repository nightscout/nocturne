import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import {
  MemberHistoryActorKind,
  MemberHistoryKind,
  MemberHistoryLimit,
  MembershipEndHow,
  MembershipJoinHow,
  type MemberHistoryEntry,
  type TenantRoleDto,
} from "@nocturne/api-client";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { accessEndInstant } from "./access-end.js";
import MemberHistoryEntryRow from "./MemberHistoryEntryRow.svelte";

const person = { kind: MemberHistoryActorKind.Person, subjectId: "a1", name: "Alex" };
const member = { subjectId: "b1", name: "Sam" };

function entry(overrides: Partial<MemberHistoryEntry>): MemberHistoryEntry {
  return {
    id: "e1",
    at: "2026-03-01T09:00:00Z",
    kind: MemberHistoryKind.RoleGiven,
    actor: person,
    member,
    viaInvite: false,
    ...overrides,
  };
}

async function sentence(overrides: Partial<MemberHistoryEntry>, roles: TenantRoleDto[] = []) {
  render(MemberHistoryEntryRow, { entry: entry(overrides), roles });
  return page.getByRole("listitem");
}

describe("MemberHistoryEntryRow sentences", () => {
  it("states a role given, naming the role by its display name", async () => {
    const row = await sentence(
      { role: "viewer" },
      [{ id: "r1", slug: "viewer", name: "Viewer" } as TenantRoleDto],
    );
    await expect.element(row).toHaveTextContent("Alex gave Sam the role Viewer.");
  });

  it("states a role taken, showing the recorded slug when no role matches", async () => {
    const row = await sentence({ kind: MemberHistoryKind.RoleTaken, role: "viewer" });
    await expect.element(row).toHaveTextContent("Alex removed the role viewer from Sam.");
  });

  it("states a permission given with its label", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.PermissionGiven,
      permission: "glucose.read",
    });
    await expect.element(row).toHaveTextContent("Alex gave Sam the permission View glucose readings.");
  });

  it("states a permission taken", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.PermissionTaken,
      permission: "glucose.read",
    });
    await expect
      .element(row)
      .toHaveTextContent("Alex removed the permission View glucose readings from Sam.");
  });

  it("states a limit to 24 hours", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.HistoryLimitChanged,
      historyLimit: MemberHistoryLimit.TwentyFourHours,
    });
    await expect.element(row).toHaveTextContent("Alex limited Sam to the last 24 hours.");
  });

  it("states full history", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.HistoryLimitChanged,
      historyLimit: MemberHistoryLimit.Full,
    });
    await expect.element(row).toHaveTextContent("Alex gave Sam full history.");
  });

  it("states a role's permission change and lists what was added and removed", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.RolePermissionsChanged,
      member: undefined,
      role: "Caretaker",
      gained: ["glucose.read", "custom.thing"],
      lost: ["treatments.read"],
    });
    await expect.element(row).toHaveTextContent("Alex changed the permissions of the role Caretaker.");
    await expect
      .element(row)
      .toHaveTextContent("Permissions added: View glucose readings, custom.thing");
    await expect.element(row).toHaveTextContent("Permissions removed: View treatments");
  });

  it("states an end date set as the day the access still holds, without a time", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.AccessEndSet,
      accessEndsAt: accessEndInstant("2026-07-14"),
    });
    await expect
      .element(row)
      .toHaveTextContent(`Alex set Sam's access to end on ${formatMediumDate(new Date(2026, 6, 14))}.`);
  });

  it("states an end date removed", async () => {
    const row = await sentence({ kind: MemberHistoryKind.AccessEndCleared });
    await expect.element(row).toHaveTextContent("Alex removed the end date of Sam's access.");
  });

  it.each([
    [MemberHistoryKind.AccessGiven, "Alex gave Sam a role or a permission. The detail is no longer stored."],
    [MemberHistoryKind.AccessTaken, "Alex removed a role or a permission from Sam. The detail is no longer stored."],
    [MemberHistoryKind.HistoryLimitChanged, "Alex changed the history limit of Sam. The detail is no longer stored."],
    [MemberHistoryKind.AccessEndChanged, "Alex changed the end date of Sam's access. The detail is no longer stored."],
    [MemberHistoryKind.RolePermissionsChanged, "Alex changed the permissions of a role. The detail is no longer stored."],
  ])("states a change whose detail was cleared: %s", async (kind, text) => {
    const row = await sentence({ kind });
    await expect.element(row).toHaveTextContent(text);
  });
});

describe("MemberHistoryEntryRow joining by invite", () => {
  const self = { kind: MemberHistoryActorKind.Person, subjectId: "b1", name: "Sam" };
  const joining = { actor: self, viaInvite: true, inviteLabel: "camp nurse" };

  it("says the person joined with the role, not that they gave it to themself", async () => {
    const row = await sentence({ ...joining, role: "viewer" });
    await expect.element(row).toHaveTextContent("Sam joined with the role viewer.");
    await expect.element(row).toHaveTextContent("Through the invite camp nurse.");
    await expect.element(row).not.toHaveTextContent("gave");
  });

  it("says the person joined with access that ends on the day", async () => {
    const row = await sentence({
      ...joining,
      kind: MemberHistoryKind.AccessEndSet,
      accessEndsAt: accessEndInstant("2026-11-28"),
    });
    const day = formatMediumDate(new Date(2026, 10, 28));
    await expect.element(row).toHaveTextContent(`Sam joined with access that ends on ${day}.`);
  });

  it("says the person joined with the last 24 hours only", async () => {
    const row = await sentence({
      ...joining,
      kind: MemberHistoryKind.HistoryLimitChanged,
      historyLimit: MemberHistoryLimit.TwentyFourHours,
    });
    await expect.element(row).toHaveTextContent("Sam joined with access to the last 24 hours only.");
  });

  it("keeps the giver's sentence when someone else used an invite's id", async () => {
    const row = await sentence({ viaInvite: true, role: "viewer" });
    await expect.element(row).toHaveTextContent("Alex gave Sam the role viewer.");
  });
});

describe("MemberHistoryEntryRow actors and members", () => {
  it.each([
    [MemberHistoryActorKind.System, "The system"],
    [MemberHistoryActorKind.Anonymous, "A caller who was not signed in"],
    [MemberHistoryActorKind.ApiKey, "An API key"],
    [MemberHistoryActorKind.AccessToken, "An access token"],
    [MemberHistoryActorKind.InstanceKey, "An instance key"],
    [MemberHistoryActorKind.GuestLink, "A guest link"],
    [MemberHistoryActorKind.PlatformAccess, "A platform administrator"],
    [MemberHistoryActorKind.Unknown, "Someone unknown"],
  ])("names a %s actor", async (kind, text) => {
    const row = await sentence({ role: "viewer", actor: { kind } });
    await expect.element(row).toHaveTextContent(`${text} gave Sam the role viewer.`);
  });

  it("names a deleted actor and a deleted member", async () => {
    const row = await sentence({
      role: "viewer",
      actor: { kind: MemberHistoryActorKind.Person, subjectId: "a1" },
      member: { subjectId: "b1" },
    });
    await expect
      .element(row)
      .toHaveTextContent("A deleted member gave a deleted member the role viewer.");
  });
});

describe("MemberHistoryEntryRow invites", () => {
  it("names the invite by its label", async () => {
    const row = await sentence({ role: "viewer", viaInvite: true, inviteLabel: "Family" });
    await expect.element(row).toHaveTextContent("Through the invite Family.");
  });

  it("says an invite caused the change when it has no label", async () => {
    const row = await sentence({ role: "viewer", viaInvite: true });
    await expect.element(row).toHaveTextContent("Through an invite.");
  });

  it("says nothing of an invite when none caused the change", async () => {
    const row = await sentence({ role: "viewer" });
    await expect.element(row).toHaveTextContent("Alex gave Sam the role viewer.");
    expect(row.element().textContent).not.toContain("invite");
  });
});

describe("MemberHistoryEntryRow joining and leaving", () => {
  const self = { kind: MemberHistoryActorKind.Person, subjectId: "b1", name: "Sam" };
  const maker = { subjectId: "c1", name: "Jo" };
  const joinedBy = (joinedHow: MembershipJoinHow, overrides: Partial<MemberHistoryEntry> = {}) =>
    sentence({ kind: MemberHistoryKind.Joined, joinedHow, ...overrides });
  const removedBy = (removedHow: MembershipEndHow, overrides: Partial<MemberHistoryEntry> = {}) =>
    sentence({ kind: MemberHistoryKind.Removed, removedHow, ...overrides });

  it("names the invite and its maker, and adds no separate invite line", async () => {
    const row = await joinedBy(MembershipJoinHow.Invite, {
      actor: self, viaInvite: true, inviteLabel: "camp nurse", by: maker,
    });
    await expect.element(row).toHaveTextContent("Sam joined through the invite camp nurse, made by Jo.");
    expect(row.element().textContent).not.toContain("Through the invite");
  });

  it("names the maker of an invite with no label", async () => {
    const row = await joinedBy(MembershipJoinHow.Invite, { actor: self, viaInvite: true, by: maker });
    await expect.element(row).toHaveTextContent("Sam joined through an invite made by Jo.");
  });

  it("says only that an invite was used when no maker is known", async () => {
    const row = await joinedBy(MembershipJoinHow.Invite, { actor: self, viaInvite: true });
    await expect.element(row).toHaveTextContent("Sam joined through an invite.");
  });

  it("names the approver of a request", async () => {
    const row = await joinedBy(MembershipJoinHow.Request, { by: maker });
    await expect.element(row).toHaveTextContent("Jo approved Sam's request to join.");
  });

  it.each([
    [MembershipJoinHow.Direct, "Alex added Sam."],
    [MembershipJoinHow.Setup, "Sam set up this site."],
    [MembershipJoinHow.System, "The system added Sam."],
  ])("states a join by %s", async (how, text) => {
    const row = await joinedBy(how);
    await expect.element(row).toHaveTextContent(text);
  });

  it.each([
    [MembershipEndHow.Removed, "Alex removed Sam."],
    [MembershipEndHow.Left, "Sam left."],
    [MembershipEndHow.AccountDeleted, "Sam's account was deleted."],
    [MembershipEndHow.Replaced, "Sam joined again. The earlier membership was replaced."],
    [MembershipEndHow.System, "The system removed Sam."],
  ])("states a removal by %s", async (how, text) => {
    const row = await removedBy(how);
    await expect.element(row).toHaveTextContent(text);
  });

  it("states a join whose detail was cleared", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Joined });
    await expect.element(row).toHaveTextContent("Sam joined. The detail is no longer stored.");
  });

  it("states a removal whose detail was cleared, for a member who no longer exists", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Removed, member: { subjectId: "b1" } });
    await expect
      .element(row)
      .toHaveTextContent("A deleted member's membership ended. The detail is no longer stored.");
  });

  it("does not repeat the invite under a role given at a recorded join", async () => {
    const row = await sentence({
      actor: self, viaInvite: true, inviteLabel: "camp nurse", role: "viewer", inRecordedJoin: true,
    });
    await expect.element(row).toHaveTextContent("Sam joined with the role viewer.");
    expect(row.element().textContent).not.toContain("Through the invite");
  });
});

describe("MemberHistoryEntryRow team access sentences", () => {
  const team = { teamId: "t1", name: "Clinic" };
  const END = accessEndInstant("2026-12-31");
  const roleList = [
    { id: "r1", slug: "viewer", name: "Viewer" } as TenantRoleDto,
    { id: "r2", slug: "caretaker", name: "Caretaker" } as TenantRoleDto,
  ];
  const given = (overrides: Partial<MemberHistoryEntry>) =>
    sentence({ kind: MemberHistoryKind.TeamAccessGiven, member: undefined, team, ...overrides }, roleList);

  it.each([
    ["one role", { roles: ["viewer"] }, "Alex gave the team Clinic access with the role Viewer."],
    [
      "one role and the 24-hour limit",
      { roles: ["viewer"], historyLimit: MemberHistoryLimit.TwentyFourHours },
      "Alex gave the team Clinic access with the role Viewer to the last 24 hours only.",
    ],
    [
      "one role and an end",
      { roles: ["viewer"], accessEndsAt: END },
      `Alex gave the team Clinic access with the role Viewer until ${formatMediumDate(new Date(2026, 11, 31))}.`,
    ],
    [
      "one role, the limit and an end",
      { roles: ["viewer"], historyLimit: MemberHistoryLimit.TwentyFourHours, accessEndsAt: END },
      `Alex gave the team Clinic access with the role Viewer to the last 24 hours only, until ${formatMediumDate(new Date(2026, 11, 31))}.`,
    ],
    ["two roles", { roles: ["viewer", "caretaker"] }, "Alex gave the team Clinic access with the roles Viewer, Caretaker."],
    [
      "two roles and the 24-hour limit",
      { roles: ["viewer", "caretaker"], historyLimit: MemberHistoryLimit.TwentyFourHours },
      "Alex gave the team Clinic access with the roles Viewer, Caretaker to the last 24 hours only.",
    ],
    [
      "two roles and an end",
      { roles: ["viewer", "caretaker"], accessEndsAt: END },
      `Alex gave the team Clinic access with the roles Viewer, Caretaker until ${formatMediumDate(new Date(2026, 11, 31))}.`,
    ],
    [
      "two roles, the limit and an end",
      { roles: ["viewer", "caretaker"], historyLimit: MemberHistoryLimit.TwentyFourHours, accessEndsAt: END },
      `Alex gave the team Clinic access with the roles Viewer, Caretaker to the last 24 hours only, until ${formatMediumDate(new Date(2026, 11, 31))}.`,
    ],
  ])("states access given with %s", async (_case, overrides, text) => {
    const row = await given(overrides as Partial<MemberHistoryEntry>);
    await expect.element(row).toHaveTextContent(text);
  });

  it("shows a recorded role slug no role of the site matches", async () => {
    const row = await given({ roles: ["gone"] });
    await expect.element(row).toHaveTextContent("Alex gave the team Clinic access with the role gone.");
  });

  it("says the detail is not stored when the row names no role", async () => {
    const row = await given({ roles: [] });
    await expect
      .element(row)
      .toHaveTextContent("Alex gave the team Clinic access. The detail is no longer stored.");
  });

  it("states access stopped, with no member in the sentence", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamAccessStopped, member: undefined, team });
    await expect.element(row).toHaveTextContent("Alex stopped the access of the team Clinic.");
  });

  it("names a team that was deleted by its recorded name", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.TeamAccessStopped,
      member: undefined,
      team: { teamId: "t2", name: "Old ward" },
    });
    await expect.element(row).toHaveTextContent("Alex stopped the access of the team Old ward.");
  });

  it("never names the system as a person when the actor is not one", async () => {
    const row = await sentence({
      kind: MemberHistoryKind.TeamAccessStopped,
      member: undefined,
      team,
      actor: { kind: MemberHistoryActorKind.System },
    });
    await expect.element(row).toHaveTextContent("The system stopped the access of the team Clinic.");
  });
});


describe("MemberHistoryEntryRow team access with no team name", () => {
  const unnamed = { teamId: "t3" };
  const roleList = [{ id: "r1", slug: "viewer", name: "Viewer" } as TenantRoleDto];

  it("states access stopped for a team that was deleted", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamAccessStopped, member: undefined, team: unnamed });
    await expect.element(row).toHaveTextContent("Alex stopped the access of a team that was deleted.");
  });

  it("states access given to a team that was deleted", async () => {
    const row = await sentence(
      { kind: MemberHistoryKind.TeamAccessGiven, member: undefined, team: unnamed, roles: ["viewer"] },
      roleList,
    );
    await expect.element(row).toHaveTextContent("Alex gave a team that was deleted access with the role Viewer.");
  });

  it("states access given to a team that was deleted, with two roles, the limit and an end", async () => {
    const row = await sentence(
      {
        kind: MemberHistoryKind.TeamAccessGiven,
        member: undefined,
        team: unnamed,
        roles: ["viewer", "other"],
        historyLimit: MemberHistoryLimit.TwentyFourHours,
        accessEndsAt: accessEndInstant("2026-12-31"),
      },
      roleList,
    );
    await expect
      .element(row)
      .toHaveTextContent(
        `Alex gave a team that was deleted access with the roles Viewer, other to the last 24 hours only, until ${formatMediumDate(new Date(2026, 11, 31))}.`,
      );
  });

  it("states no role for a team that was deleted when the row names none", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamAccessGiven, member: undefined, team: unnamed, roles: [] });
    await expect
      .element(row)
      .toHaveTextContent("Alex gave a team that was deleted access. The detail is no longer stored.");
  });
});

describe("MemberHistoryEntryRow a team's access changed", () => {
  const team = { teamId: "t1", name: "Clinic" };
  const unnamed = { teamId: "t3" };
  const END = accessEndInstant("2026-12-31");
  const endText = () => formatMediumDate(new Date(2026, 11, 31));
  const roleList = [
    { id: "r1", slug: "viewer", name: "Viewer" } as TenantRoleDto,
    { id: "r2", slug: "caretaker", name: "Caretaker" } as TenantRoleDto,
  ];
  const changed = (overrides: Partial<MemberHistoryEntry>) =>
    sentence({ kind: MemberHistoryKind.TeamAccessChanged, member: undefined, team, ...overrides }, roleList);

  it.each([
    ["one role", { roles: ["viewer"] }, () => "After this change, the team had access with the role Viewer."],
    [
      "one role and the 24-hour limit",
      { roles: ["viewer"], historyLimit: MemberHistoryLimit.TwentyFourHours },
      () => "After this change, the team had access with the role Viewer to the last 24 hours only.",
    ],
    [
      "one role and an end",
      { roles: ["viewer"], accessEndsAt: END },
      () => `After this change, the team had access with the role Viewer until ${endText()}.`,
    ],
    [
      "one role, the limit and an end",
      { roles: ["viewer"], historyLimit: MemberHistoryLimit.TwentyFourHours, accessEndsAt: END },
      () => `After this change, the team had access with the role Viewer to the last 24 hours only, until ${endText()}.`,
    ],
    ["two roles", { roles: ["viewer", "caretaker"] }, () => "After this change, the team had access with the roles Viewer, Caretaker."],
    [
      "two roles and the 24-hour limit",
      { roles: ["viewer", "caretaker"], historyLimit: MemberHistoryLimit.TwentyFourHours },
      () => "After this change, the team had access with the roles Viewer, Caretaker to the last 24 hours only.",
    ],
    [
      "two roles and an end",
      { roles: ["viewer", "caretaker"], accessEndsAt: END },
      () => `After this change, the team had access with the roles Viewer, Caretaker until ${endText()}.`,
    ],
    [
      "two roles, the limit and an end",
      { roles: ["viewer", "caretaker"], historyLimit: MemberHistoryLimit.TwentyFourHours, accessEndsAt: END },
      () => `After this change, the team had access with the roles Viewer, Caretaker to the last 24 hours only, until ${endText()}.`,
    ],
  ])("states the change and the access it now gives with %s", async (_case, overrides, text) => {
    const row = await changed(overrides as Partial<MemberHistoryEntry>);
    await expect
      .element(row)
      .toHaveTextContent(`Alex changed what the team Clinic can do on this site. ${text()}`);
  });

  it("says the detail is not stored when the row names no role", async () => {
    const row = await changed({ roles: [] });
    await expect
      .element(row)
      .toHaveTextContent("Alex changed what the team Clinic can do on this site. The detail is no longer stored.");
  });

  it("states the change for a team that was deleted, with what it then gave", async () => {
    const row = await changed({ team: unnamed, roles: ["viewer"], historyLimit: MemberHistoryLimit.TwentyFourHours });
    await expect
      .element(row)
      .toHaveTextContent(
        "Alex changed what a team that was deleted can do on this site. After this change, the team had access with the role Viewer to the last 24 hours only.",
      );
  });

  it("says the detail is not stored for a team that was deleted when the row names no role", async () => {
    const row = await changed({ team: undefined, roles: [] });
    await expect
      .element(row)
      .toHaveTextContent("Alex changed what a team that was deleted can do on this site. The detail is no longer stored.");
  });

  it("names the system when the actor is not a person", async () => {
    const row = await changed({ roles: ["viewer"], actor: { kind: MemberHistoryActorKind.System } });
    await expect.element(row).toHaveTextContent("The system changed what the team Clinic can do on this site.");
  });
});

describe("MemberHistoryEntryRow access through a team", () => {
  const self = { kind: MemberHistoryActorKind.Person, subjectId: "b1", name: "Sam" };
  const team = { teamId: "t1", name: "Clinic" };

  it("names the team a member now has access through", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Joined, joinedHow: MembershipJoinHow.Team, actor: self, team });
    await expect.element(row).toHaveTextContent("Sam now has access through the team Clinic.");
  });

  it("says the detail is gone when the team's name is not known", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Joined, joinedHow: MembershipJoinHow.Team, actor: self });
    await expect.element(row).toHaveTextContent("Sam now has access through a team. The detail is no longer stored.");
  });

  it("names the team a member no longer has access through, and never says they left", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Removed, removedHow: MembershipEndHow.Team, actor: self, team });
    await expect.element(row).toHaveTextContent("Sam no longer has access through the team Clinic.");
    expect(row.element().textContent).not.toContain("left");
  });

  it("says the detail is gone when the team a member's access ended through is not known", async () => {
    const row = await sentence({ kind: MemberHistoryKind.Removed, removedHow: MembershipEndHow.Team, actor: self });
    await expect.element(row).toHaveTextContent("Sam no longer has access through a team. The detail is no longer stored.");
  });
});

describe("MemberHistoryEntryRow an owner's decisions about a team's staff", () => {
  const team = { teamId: "t1", name: "Clinic" };

  it("states a staff member approved", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffApproved, team });
    await expect.element(row).toHaveTextContent("Alex approved Sam as staff of the team Clinic.");
  });

  it("states a staff member approved for a team that was deleted", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffApproved });
    await expect.element(row).toHaveTextContent("Alex approved Sam as staff of a team that was deleted.");
  });

  it("states a staff member declined", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffDeclined, team });
    await expect.element(row).toHaveTextContent("Alex declined Sam as staff of the team Clinic.");
  });

  it("states a staff member declined for a team that was deleted", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffDeclined });
    await expect.element(row).toHaveTextContent("Alex declined Sam as staff of a team that was deleted.");
  });

  it("states the switch turned off, and that new staff now wait", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined, team, teamManagesStaff: false });
    await expect
      .element(row)
      .toHaveTextContent(
        `Alex turned off "the team manages its own staff" for the team Clinic. New staff now wait for an owner's approval.`,
      );
  });

  it("states the switch turned on", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined, team, teamManagesStaff: true });
    await expect.element(row).toHaveTextContent("Alex let the team Clinic approve its own staff.");
  });

  it("states the switch turned off for a team that was deleted", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined, teamManagesStaff: false });
    await expect
      .element(row)
      .toHaveTextContent('Alex turned off "the team manages its own staff" for a team that was deleted.');
  });

  it("states the switch turned on for a team that was deleted", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined, teamManagesStaff: true });
    await expect.element(row).toHaveTextContent("Alex let a team that was deleted approve its own staff.");
  });

  it("says the detail is gone when the switch's value is not known", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined, team });
    await expect
      .element(row)
      .toHaveTextContent("Alex changed who approves the staff of the team Clinic. The detail is no longer stored.");
  });

  it("says the detail is gone when neither the team nor the switch's value is known", async () => {
    const row = await sentence({ kind: MemberHistoryKind.TeamStaffRuleChanged, member: undefined });
    await expect
      .element(row)
      .toHaveTextContent("Alex changed who approves the staff of a team that was deleted. The detail is no longer stored.");
  });
});
