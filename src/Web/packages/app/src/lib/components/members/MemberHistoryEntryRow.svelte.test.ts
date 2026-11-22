import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import {
  MemberHistoryActorKind,
  MemberHistoryKind,
  MemberHistoryLimit,
  type MemberHistoryEntry,
  type TenantRoleDto,
} from "@nocturne/api-client";
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

  it.each([
    [MemberHistoryKind.AccessGiven, "Alex gave Sam a role or a permission. The detail is no longer stored."],
    [MemberHistoryKind.AccessTaken, "Alex removed a role or a permission from Sam. The detail is no longer stored."],
    [MemberHistoryKind.HistoryLimitChanged, "Alex changed the history limit of Sam. The detail is no longer stored."],
    [MemberHistoryKind.RolePermissionsChanged, "Alex changed the permissions of a role. The detail is no longer stored."],
  ])("states a change whose detail was cleared: %s", async (kind, text) => {
    const row = await sentence({ kind });
    await expect.element(row).toHaveTextContent(text);
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
