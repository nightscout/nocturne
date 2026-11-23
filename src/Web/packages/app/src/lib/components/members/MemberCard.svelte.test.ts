import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { getLocalTimeZone, today } from "@internationalized/date";
import { MemberReasonKind, type TenantMemberDto, type TenantRoleDto } from "@nocturne/api-client";
import { formatMediumDate, formatShortDate } from "#lib/utils/formatting.js";
import { accessEndDate, accessEndInstant } from "./access-end.js";
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

describe("MemberCard history link", () => {
  const base = {
    member: ownerMember,
    roles: administratorRoles,
    canEditRoles: true,
    canManage: true,
    isExpanded: false,
    isSaving: false,
    onToggleExpand: () => {},
    onSave: () => {},
    onRemove: () => {},
  };

  it("links to the history of that member when it is offered", async () => {
    render(MemberCard, { ...base, canViewHistory: true });

    await expect.element(page.getByText("View history")).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "View history for Alex" }))
      .toHaveAttribute("href", expect.stringContaining(`/settings/members/history?member=${ownerMember.subjectId}`));
  });

  it("offers no link when it is not offered", async () => {
    render(MemberCard, base);

    await expect.element(page.getByRole("button", { name: "Edit" })).toBeVisible();
    expect(page.getByRole("link", { name: "View history for Alex" }).elements()).toHaveLength(0);
  });
});

describe("MemberCard recovery email badge", () => {
  const props = {
    roles: administratorRoles,
    canEditRoles: false,
    canManage: false,
    isExpanded: false,
    isSaving: false,
    onToggleExpand: () => {},
    onSave: () => {},
    onRemove: () => {},
  };

  it("marks a member with no confirmed recovery email to a viewer who manages members", async () => {
    render(MemberCard, {
      ...props,
      member: { ...ownerMember, hasConfirmedRecoveryEmail: false },
      canSeeRecoveryEmail: true,
    });

    await expect.element(page.getByText("No recovery email")).toBeVisible();
  });

  it.each([
    ["has one", { hasConfirmedRecoveryEmail: true }, true],
    ["is a system or demo subject", { hasConfirmedRecoveryEmail: undefined }, true],
    ["is not reported on", {}, true],
    ["lacks one but the viewer cannot manage members", { hasConfirmedRecoveryEmail: false }, false],
  ])("shows no badge when the member %s", async (_, extra, canSee) => {
    render(MemberCard, { ...props, member: { ...ownerMember, ...extra }, canSeeRecoveryEmail: canSee });

    await expect.element(page.getByText("Alex")).toBeVisible();
    expect(page.getByText("No recovery email").elements()).toHaveLength(0);
  });
});

describe("MemberCard access end", () => {
  const caretakerMember: TenantMemberDto = {
    id: "66666666-6666-6666-6666-666666666666",
    subjectId: "77777777-7777-7777-7777-777777777777",
    name: "Sam",
    roles: [{ roleId: caretakerRoleId, name: "Caretaker", slug: "caretaker", permissions: ["glucose.read"] }],
    directPermissions: [],
  };
  const grantableOwnerRoles = administratorRoles.map((r) =>
    r.id === ownerRoleId ? { ...r, canGrant: true } : r,
  );
  const accessEndLabel = "Access ends (optional)";

  function dayFromToday(days: number): string {
    return today(getLocalTimeZone()).add({ days }).toString();
  }

  function shortEnd(day: string): string {
    return formatShortDate(accessEndDate(accessEndInstant(day)), true);
  }

  function propsFor(member: TenantMemberDto, extra: Record<string, unknown> = {}) {
    return {
      member,
      roles: administratorRoles,
      canEditRoles: true,
      canManage: true,
      isExpanded: false,
      isSaving: false,
      onToggleExpand: () => {},
      onSave: (_changes: MemberChanges) => {},
      onRemove: () => {},
      ...extra,
    };
  }

  async function edit(member: TenantMemberDto, extra: Record<string, unknown> = {}) {
    const props = propsFor(member, extra);
    const screen = render(MemberCard, props);
    await page.getByRole("button", { name: "Edit" }).click();
    await screen.rerender({ ...props, isExpanded: true });
  }

  it("shows the last day of a membership that has an end and has not ended", async () => {
    const day = dayFromToday(10);
    render(
      MemberCard,
      propsFor({ ...caretakerMember, accessEndsAt: accessEndInstant(day), hasEnded: false }),
    );

    await expect.element(page.getByText(`Until ${shortEnd(day)}`)).toBeVisible();
    await expect.element(page.getByText("Caretaker")).toHaveClass("bg-secondary");
    expect(page.getByText("Ended", { exact: false }).elements()).toHaveLength(0);
  });

  it("shows a member whose access has ended as ended, and still offers to extend or remove it", async () => {
    const day = dayFromToday(-3);
    render(
      MemberCard,
      propsFor({ ...caretakerMember, accessEndsAt: accessEndInstant(day), hasEnded: true }),
    );

    await expect.element(page.getByText(`Ended ${shortEnd(day)}`)).toBeVisible();
    expect(page.getByText("Until", { exact: false }).elements()).toHaveLength(0);
    await expect.element(page.getByText("Caretaker")).not.toHaveClass("bg-secondary");
    await expect.element(page.getByRole("button", { name: "Edit" })).toBeEnabled();
    await expect.element(page.getByRole("button", { name: "Remove member" })).toBeEnabled();
  });

  it("saves the instant the picked day ends", async () => {
    const onSave = vi.fn();
    await edit({ ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) }, { onSave });

    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ accessEndsAt: accessEndInstant("2099-07-14") }),
    );
  });

  it("saves an end for a member who has none", async () => {
    const onSave = vi.fn();
    await edit(caretakerMember, { onSave });

    await expect.element(page.getByLabelText(accessEndLabel)).toHaveValue("");
    await page.getByLabelText(accessEndLabel).fill("2099-07-14");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ accessEndsAt: accessEndInstant("2099-07-14") }),
    );
  });

  it("shows the day a member keeps access through in the field", async () => {
    const day = dayFromToday(10);
    await edit({ ...caretakerMember, accessEndsAt: accessEndInstant(day) });

    await expect.element(page.getByLabelText(accessEndLabel)).toHaveValue(day);
  });

  it("saves null when the end date is removed", async () => {
    const onSave = vi.fn();
    await edit({ ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) }, { onSave });

    await page.getByRole("button", { name: "Remove the end date" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ accessEndsAt: null }));
  });

  it("sends no end when the end did not change", async () => {
    const onSave = vi.fn();
    await edit({ ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) }, { onSave });

    await page.getByRole("checkbox", { name: "Only last 24 hours" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave.mock.calls[0][0].accessEndsAt).toBeUndefined();
  });

  it("offers no way to remove an end date that is not set", async () => {
    await edit(caretakerMember);

    await expect.element(page.getByLabelText(accessEndLabel)).toBeVisible();
    expect(page.getByRole("button", { name: "Remove the end date" }).elements()).toHaveLength(0);
  });

  it("limits the day to the editor's own last day, and cannot remove the end, when the editor's access ends", async () => {
    const last = dayFromToday(30);
    await edit(
      { ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) },
      { callerAccessEndsAt: accessEndInstant(last) },
    );

    await expect.element(page.getByLabelText("Access ends")).toHaveAttribute("max", last);
    await expect
      .element(
        page.getByText(
          `You cannot remove the end date. Your own access ends on ${formatMediumDate(accessEndDate(accessEndInstant(last)))}.`,
        ),
      )
      .toBeVisible();
    expect(page.getByRole("button", { name: "Remove the end date" }).elements()).toHaveLength(0);
  });

  it("keeps a stored end when the field is cleared under a ceiling, and does not save", async () => {
    const onSave = vi.fn();
    await edit(
      { ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) },
      { callerAccessEndsAt: accessEndInstant(dayFromToday(30)), onSave },
    );

    const field = page.getByLabelText("Access ends");
    await field.fill("");

    await expect.element(field).toHaveValue("");
    await expect.element(page.getByRole("button", { name: "Save changes" })).toBeDisabled();
    expect(onSave).not.toHaveBeenCalled();
    await expect.element(field).toHaveAttribute("aria-describedby", expect.stringMatching(/-help$/));
  });

  it("never overwrites a day typed under a ceiling", async () => {
    await edit(caretakerMember, { callerAccessEndsAt: accessEndInstant(dayFromToday(30)) });

    const field = page.getByLabelText(accessEndLabel);
    await field.fill(dayFromToday(5));
    await expect.element(field).toHaveValue(dayFromToday(5));
    await field.fill("");

    await expect.element(field).toHaveValue("");
    await expect.element(page.getByRole("button", { name: "Save changes" })).not.toBeInTheDocument();
  });

  it("says a member with no end cannot be given a later one than the editor's", async () => {
    const last = dayFromToday(30);
    await edit(caretakerMember, { callerAccessEndsAt: accessEndInstant(last) });

    await expect
      .element(
        page.getByText(
          `Your own access ends on ${formatMediumDate(accessEndDate(accessEndInstant(last)))}. You cannot give access that lasts longer.`,
        ),
      )
      .toBeVisible();
  });

  it("does not save a day past the editor's own", async () => {
    await edit(caretakerMember, { callerAccessEndsAt: accessEndInstant(dayFromToday(30)) });

    await page.getByLabelText(accessEndLabel).fill(dayFromToday(31));

    await expect.element(page.getByRole("button", { name: "Save changes" })).toBeDisabled();
  });

  it("offers no end date to an owner and says why", async () => {
    await edit(ownerMember);

    await expect.element(page.getByText("An owner's access does not end.")).toBeVisible();
    expect(page.getByLabelText(accessEndLabel).elements()).toHaveLength(0);
  });

  it("removes the field when the owner role is chosen for a member with no end", async () => {
    await edit(caretakerMember, { roles: grantableOwnerRoles });

    await expect.element(page.getByLabelText(accessEndLabel)).toBeVisible();
    await page.getByRole("checkbox", { name: "Owner" }).click();

    await expect.element(page.getByText("An owner's access does not end.")).toBeVisible();
    expect(page.getByLabelText(accessEndLabel).elements()).toHaveLength(0);
  });

  it("does not offer the owner role to a member who has an end date, until the date is removed", async () => {
    await edit(
      { ...caretakerMember, accessEndsAt: accessEndInstant(dayFromToday(10)) },
      { roles: grantableOwnerRoles },
    );

    await expect.element(page.getByRole("checkbox", { name: "Owner" })).toBeDisabled();
    await expect
      .element(page.getByText("Clear the access end date before you make this member an owner."))
      .toBeVisible();
    await expect
      .element(page.getByRole("checkbox", { name: "Owner" }))
      .toHaveAttribute("aria-describedby", `member-owner-reason-${caretakerMember.subjectId}`);
    await expect
      .element(page.getByText("Clear the access end date before you make this member an owner."))
      .toHaveAttribute("id", `member-owner-reason-${caretakerMember.subjectId}`);

    await page.getByRole("button", { name: "Remove the end date" }).click();

    await expect.element(page.getByRole("checkbox", { name: "Owner" })).toBeEnabled();
    expect(
      page.getByText("Clear the access end date before you make this member an owner.").elements(),
    ).toHaveLength(0);
  });

  it("shows the end on your own row and offers no edit of it", async () => {
    const day = dayFromToday(10);
    const own = { ...caretakerMember, accessEndsAt: accessEndInstant(day) };
    await edit(own, { currentSubjectId: own.subjectId });

    await expect
      .element(page.getByText("You cannot change your own roles or permissions.", { exact: false }))
      .toBeVisible();
    expect(page.getByLabelText(accessEndLabel).elements()).toHaveLength(0);
    await expect.element(page.getByText(`Until ${shortEnd(day)}`)).toBeVisible();
  });

  describe("a member whose access lasts longer than the caller's", () => {
    const reason = "This member's access lasts longer than yours.";
    const props = {
      roles: administratorRoles,
      canEditRoles: true,
      canManage: true,
      isExpanded: false,
      isSaving: false,
      onToggleExpand: () => {},
      onSave: () => {},
      onRemove: () => {},
    };

    it("offers no remove or edit control, only a read-only view, and says why", async () => {
      render(MemberCard, { ...props, member: { ...ownerMember, outlastsCaller: true } });

      await expect.element(page.getByText(reason)).toBeVisible();
      expect(page.getByRole("button", { name: "Edit" }).elements()).toHaveLength(0);
      expect(page.getByRole("button", { name: "Remove member" }).elements()).toHaveLength(0);
      await expect.element(page.getByRole("button", { name: "View", exact: true })).toBeVisible();
    });

    it("lists the member's permissions when expanded and offers no save", async () => {
      render(MemberCard, {
        ...props,
        isExpanded: true,
        member: { ...ownerMember, outlastsCaller: true, directPermissions: ["glucose.read"] },
      });

      await expect.element(page.getByText("Permissions from roles")).toBeVisible();
      await expect.element(page.getByText("Direct permissions")).toBeVisible();
      expect(page.getByRole("button", { name: "Save changes" }).elements()).toHaveLength(0);
      expect(page.getByRole("checkbox", { name: "Caretaker" }).elements()).toHaveLength(0);
    });

    it("ties the reason to the row for a screen reader", async () => {
      render(MemberCard, { ...props, member: { ...ownerMember, outlastsCaller: true } });

      await expect
        .element(page.getByRole("group", { name: "Alex" }))
        .toHaveAccessibleDescription(reason);
    });

    it("offers edit and remove and no reason when the member is within reach", async () => {
      render(MemberCard, { ...props, member: { ...ownerMember, outlastsCaller: false } });

      await expect.element(page.getByRole("button", { name: "Edit" })).toBeVisible();
      await expect.element(page.getByRole("button", { name: "Remove member" })).toBeVisible();
      expect(page.getByText(reason).elements()).toHaveLength(0);
    });
  });
});

describe("MemberCard reasons", () => {
  const viewerRoleId = "88888888-8888-8888-8888-888888888888";
  const viewer = { roleId: viewerRoleId, name: "Viewer", slug: "viewer", permissions: ["glucose.read"] };
  const caretaker = { roleId: caretakerRoleId, name: "Caretaker", slug: "caretaker", permissions: ["glucose.read"] };
  const rolesWithViewer: TenantRoleDto[] = [
    ...administratorRoles,
    role(viewerRoleId, "Viewer", ["glucose.read"], true),
  ];
  const teamReason = {
    kind: MemberReasonKind.Team,
    teamId: "99999999-9999-9999-9999-999999999999",
    teamName: "Clinic",
    roles: [caretaker],
    limitTo24Hours: true,
    hasEnded: false,
  };
  const directReason = { kind: MemberReasonKind.Direct, roles: [viewer], directPermissions: [], limitTo24Hours: false, hasEnded: false };
  const base: TenantMemberDto = {
    id: "66666666-6666-6666-6666-666666666666",
    subjectId: "77777777-7777-7777-7777-777777777777",
    name: "Sam",
    roles: [viewer, caretaker],
    directPermissions: [],
  };
  const teamOnly: TenantMemberDto = { ...base, roles: [caretaker], limitTo24Hours: true, hasDirect: false, reasons: [teamReason] };
  const mixed: TenantMemberDto = {
    ...base,
    hasDirect: true,
    reasons: [directReason, teamReason],
    directLiftsTeamLimit: true,
  };
  const fromTeam = "This member has access only through a team. To change it, stop the team's access to this site.";
  const lifted = "This member's direct access has no 24-hour limit, so the 24-hour limit of the team does not apply.";

  function propsFor(member: TenantMemberDto, extra: Record<string, unknown> = {}) {
    return {
      member,
      roles: rolesWithViewer,
      canEditRoles: true,
      canManage: true,
      isExpanded: false,
      isSaving: false,
      onToggleExpand: () => {},
      onSave: (_changes: MemberChanges) => {},
      onRemove: () => {},
      ...extra,
    };
  }

  it("shows no reason lines for a member who holds only direct access", async () => {
    render(MemberCard, propsFor({
      ...base, roles: [viewer], hasDirect: true, reasons: [directReason], directLiftsTeamLimit: false,
    }));

    await expect.element(page.getByRole("button", { name: "Edit" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Remove member" })).toBeVisible();
    expect(page.getByRole("list", { name: "Reasons for access" }).elements()).toHaveLength(0);
    expect(page.getByText(fromTeam).elements()).toHaveLength(0);
    expect(page.getByText(lifted).elements()).toHaveLength(0);
  });

  it("names the team of a member who has access only through it, and offers no edit or removal, with the reason", async () => {
    render(MemberCard, propsFor(teamOnly));

    const reasons = page.getByRole("list", { name: "Reasons for access" });
    await expect.element(reasons).toHaveTextContent("Staff of the team Clinic");
    await expect.element(reasons).toHaveTextContent("Caretaker");
    await expect.element(reasons).toHaveTextContent("Last 24 hours only");
    expect(reasons.getByText("Direct access").elements()).toHaveLength(0);
    await expect.element(page.getByText(fromTeam)).toBeVisible();
    await expect.element(page.getByRole("group", { name: "Sam" })).toHaveAccessibleDescription(fromTeam);
    expect(page.getByRole("button", { name: "Edit" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Remove member" }).elements()).toHaveLength(0);
  });

  it("is described by both sentences when the member outlasts the viewer and has access only through a team", async () => {
    render(MemberCard, propsFor({ ...teamOnly, outlastsCaller: true }));

    await expect.element(page.getByRole("group", { name: "Sam" }))
      .toHaveAccessibleDescription(`This member's access lasts longer than yours. ${fromTeam}`);
  });

  it("shows no reason for the missing actions to a viewer who cannot manage members", async () => {
    render(MemberCard, propsFor(teamOnly, { canEditRoles: false, canManage: false }));

    await expect.element(page.getByText("Staff of the team Clinic")).toBeVisible();
    expect(page.getByText(fromTeam).elements()).toHaveLength(0);
  });

  it("shows a line for each reason of a mixed member, and says the direct access lifts the team's limit", async () => {
    render(MemberCard, propsFor(mixed));

    const lines = page.getByRole("list", { name: "Reasons for access" }).getByRole("listitem");
    expect(lines.elements()).toHaveLength(2);
    await expect.element(lines.nth(0)).toHaveTextContent("Direct access");
    await expect.element(lines.nth(0)).toHaveTextContent("(Viewer)");
    expect(lines.nth(0).element().textContent).not.toContain("Last 24 hours only");
    await expect.element(lines.nth(1)).toHaveTextContent("Staff of the team Clinic");
    await expect.element(lines.nth(1)).toHaveTextContent("(Caretaker)");
    await expect.element(lines.nth(1)).toHaveTextContent("Last 24 hours only");
    await expect.element(page.getByText(lifted)).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Remove member" })).toBeVisible();
    expect(page.getByText(fromTeam).elements()).toHaveLength(0);
  });

  it("starts the editor of a mixed member from the direct access only", async () => {
    const onSave = vi.fn();
    const props = propsFor(mixed, { onSave });
    const screen = render(MemberCard, props);
    await page.getByRole("button", { name: "Edit" }).click();
    await screen.rerender({ ...props, isExpanded: true });

    await expect.element(page.getByRole("checkbox", { name: "Viewer" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).not.toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Only last 24 hours" })).not.toBeChecked();

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      access: { roleIds: [viewerRoleId, caretakerRoleId], permissions: [] },
    }));
  });

  it("shows the end of a reason that has one", async () => {
    const day = today(getLocalTimeZone()).add({ days: 10 }).toString();
    render(MemberCard, propsFor({
      ...mixed,
      reasons: [directReason, { ...teamReason, accessEndsAt: accessEndInstant(day) }],
    }));

    await expect.element(page.getByRole("list", { name: "Reasons for access" }).getByRole("listitem").nth(1))
      .toHaveTextContent(`Until ${formatShortDate(accessEndDate(accessEndInstant(day)), true)}`);
  });
});
