import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { TenantRoleDto, TenantTeamAccess } from "@nocturne/api-client";
import { SetTeamStaffRuleRequestSchema } from "@nocturne/api-client/schemas";

const mocks = vi.hoisted(() => ({
  approveTeamStaff: vi.fn(),
  declineTeamStaff: vi.fn(),
  setTeamStaffRule: vi.fn(),
  editTeamAccess: vi.fn(),
}));

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => mocks);

import TeamAccessCard from "./TeamAccessCard.svelte";

const teamId = "55555555-5555-5555-5555-555555555555";
const viewer: TenantRoleDto = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Viewer",
  slug: "viewer",
  permissions: ["glucose.read"],
  canGrant: true,
  canGiveToTeam: true,
};
const waiting = { subjectId: "22222222-2222-2222-2222-222222222222", displayName: "Wren", status: "pending" };
const approved = { subjectId: "33333333-3333-3333-3333-333333333333", displayName: "Ari", status: "approved" };

const team: TenantTeamAccess = {
  teamId,
  teamName: "Clinic",
  roles: [{ id: viewer.id, name: "Viewer", slug: "viewer" }],
  limitTo24Hours: false,
  hasEnded: false,
  givenAt: "2026-11-01T00:00:00Z",
  staffRule: "team_decides",
  staff: [waiting, approved],
};

function show(
  canDecide = true,
  roles: { roles?: TenantRoleDto[]; rolesFailed?: boolean } = { roles: [viewer] },
  shown: TenantTeamAccess = team,
) {
  const onStale = vi.fn();
  const onSuccess = vi.fn();
  const onRetryRoles = vi.fn();
  render(TeamAccessCard, {
    team: shown,
    canStop: true,
    canDecide,
    roles: roles.roles,
    rolesFailed: roles.rolesFailed ?? false,
    onRetryRoles,
    limitForced: false,
    stoppingId: null,
    onStop: () => {},
    onStale,
    onSuccess,
  });
  return { onStale, onSuccess, onRetryRoles };
}

const refusal = (status: number, code: string) => ({ status, body: { message: "English detail", code } });

beforeEach(() => {
  vi.clearAllMocks();
  mocks.approveTeamStaff.mockResolvedValue(undefined);
  mocks.declineTeamStaff.mockResolvedValue(undefined);
  mocks.setTeamStaffRule.mockResolvedValue(undefined);
  mocks.editTeamAccess.mockResolvedValue(undefined);
});

describe("TeamAccessCard roles of the site", () => {
  it("offers Change access only once the roles have loaded, and states no reason while they load", async () => {
    show(true, { roles: undefined });

    await expect.element(page.getByRole("button", { name: "Approve Wren" })).toBeVisible();
    expect(page.getByRole("button", { name: "Change the access of Clinic" }).elements()).toHaveLength(0);
    expect(page.getByRole("alert").elements()).toHaveLength(0);
    expect(page.getByText("You cannot give a team", { exact: false }).elements()).toHaveLength(0);
  });

  it("says the roles did not load when their query failed, and loads them again on Try again", async () => {
    const { onRetryRoles } = show(true, { roles: undefined, rolesFailed: true });

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("The roles of this site did not load, so you cannot change the access of this team.");
    expect(page.getByRole("button", { name: "Change the access of Clinic" }).elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Try again" }).click();

    expect(onRetryRoles).toHaveBeenCalled();
  });

  it("offers Change access once the roles have loaded", async () => {
    show();

    await expect.element(page.getByRole("button", { name: "Change the access of Clinic" })).toBeVisible();
  });
});

describe("TeamAccessCard commands", () => {
  it("approves a staff member with the team and their subject", async () => {
    const { onSuccess } = show();

    await page.getByRole("button", { name: "Approve Wren" }).click();

    await vi.waitFor(() => expect(onSuccess).toHaveBeenCalledWith("Staff member approved."));
    expect(mocks.approveTeamStaff).toHaveBeenCalledWith({ teamId, subjectId: waiting.subjectId });
  });

  it("declines a staff member with the team and their subject, after the confirmation", async () => {
    show();

    await page.getByRole("button", { name: "Decline Ari" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Decline" }).click();

    await vi.waitFor(() =>
      expect(mocks.declineTeamStaff).toHaveBeenCalledWith({ teamId, subjectId: approved.subjectId }),
    );
  });

  it.each([
    ["I trust the whole team", "team_trusted", "You now trust the whole team."],
    ["I approve each staff member", "owner_approves", "Each new staff member now waits for an owner's approval."],
  ])("sends the rule chosen with %s, after the confirmation, in a body the generated schema accepts", async (label, staffRule, done) => {
    const { onSuccess } = show();

    await page.getByRole("radio", { name: new RegExp(label) }).click();
    await page.getByRole("button", { name: "Change setting" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Change setting" }).click();

    await vi.waitFor(() => expect(onSuccess).toHaveBeenCalledWith(done));
    expect(mocks.setTeamStaffRule).toHaveBeenCalledWith({ teamId, request: { staffRule } });
    const parsed = SetTeamStaffRuleRequestSchema.safeParse(mocks.setTeamStaffRule.mock.calls[0][0].request);
    expect(parsed.success).toBe(true);
    expect(parsed.data).toEqual({ staffRule });
  });

  it("offers no approval or decline while the site trusts the whole team, and says who can see the site", async () => {
    const declined = { subjectId: "77777777-7777-7777-7777-777777777777", displayName: "Kit", status: "declined" };
    show(true, { roles: [viewer] }, { ...team, staffRule: "team_trusted", staff: [waiting, approved, declined] });

    await expect
      .element(
        page.getByText(
          "You trust the whole team, so you do not decide about one staff member. Change the setting above to decide again.",
        ),
      )
      .toBeVisible();
    const list = page.getByRole("list", { name: "Staff of Clinic" });
    await expect.element(list.getByText("Not approved")).toBeVisible();
    await expect.element(list.getByText("Declined", { exact: true })).toBeVisible();
    expect(list.getByText("Can see this site while you trust the whole team.").elements()).toHaveLength(2);
    expect(page.getByRole("button", { name: /^(Approve|Decline) / }).elements()).toHaveLength(0);
  });

  it("tells a reader who is not an owner that the declined can see the site while it trusts the whole team", async () => {
    const declined = { subjectId: "77777777-7777-7777-7777-777777777777", displayName: "Kit", status: "declined" };
    show(false, { roles: [viewer] }, { ...team, staffRule: "team_trusted", staff: [declined] });

    await expect.element(page.getByText("Can see this site while the site trusts the whole team.")).toBeVisible();
    expect(page.getByText("You trust the whole team", { exact: false }).elements()).toHaveLength(0);
  });

  it("sends the whole edit with the team and closes the form once it is saved", async () => {
    const { onSuccess } = show();

    await page.getByRole("button", { name: "Change the access of Clinic" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(onSuccess).toHaveBeenCalledWith("Access of the team changed."));
    expect(mocks.editTeamAccess).toHaveBeenCalledWith({
      teamId,
      request: { roleIds: [viewer.id], limitTo24Hours: false, accessEndsAt: null },
    });
    await expect.element(page.getByRole("button", { name: "Save" })).not.toBeInTheDocument();
  });

  it("offers no change of access, staff setting or decision to a reader who is not an owner", async () => {
    show(false);

    await expect.element(page.getByText("Wren", { exact: true })).toBeVisible();
    expect(page.getByRole("button", { name: "Change the access of Clinic" }).elements()).toHaveLength(0);
    expect(page.getByRole("radio").elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: /^(Approve|Decline) / }).elements()).toHaveLength(0);
  });

  it("disables the other controls while a command runs", async () => {
    let finish: () => void = () => {};
    mocks.approveTeamStaff.mockReturnValue(new Promise<void>((resolve) => (finish = resolve)));
    show();

    await page.getByRole("button", { name: "Approve Wren" }).click();

    await expect.element(page.getByRole("button", { name: "Decline Ari" })).toBeDisabled();
    await expect.element(page.getByRole("radio", { name: /I trust the whole team/ })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Change the access of Clinic" })).toBeDisabled();
    finish();
    await expect.element(page.getByRole("button", { name: "Decline Ari" })).toBeEnabled();
  });
});

describe("TeamAccessCard refusals of a staff decision", () => {
  it.each([
    [403, "team_staff_decision_not_owner", "Only an owner of this site can decide which staff of a team can see this site."],
    [
      409,
      "team_staff_team_trusted",
      "You trust the whole team at this site, so you cannot approve or decline one staff member. Change the setting to decide again.",
    ],
    [
      409,
      "caller_access_from_team",
      "You have access to this site only through a team, so you cannot change the access of a team or its staff.",
    ],
  ])("words a %i %s on the card", async (status, code, sentence) => {
    mocks.approveTeamStaff.mockRejectedValue(refusal(status, code));
    show();

    await page.getByRole("button", { name: "Approve Wren" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent(sentence);
    expect(page.getByText("English detail").elements()).toHaveLength(0);
  });

  it("says the staff member or the team is gone, and reads the list again, on a 404", async () => {
    mocks.declineTeamStaff.mockRejectedValue({ status: 404, body: { message: "Not found" } });
    const { onStale } = show();

    await page.getByRole("button", { name: "Decline Ari" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Decline" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("This team no longer has access, or this person is no longer on its staff.");
    expect(onStale).toHaveBeenCalled();
  });

  it("words a refusal of the staff setting", async () => {
    mocks.setTeamStaffRule.mockRejectedValue(refusal(403, "team_staff_decision_not_owner"));
    show();

    await page.getByRole("radio", { name: /I trust the whole team/ }).click();
    await page.getByRole("button", { name: "Change setting" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Change setting" }).click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Only an owner of this site can decide which staff of a team can see this site.");
  });
});

describe("TeamAccessCard refusals of an edit", () => {
  it.each([
    [403, "team_access_not_owner", "Only an owner of this site can change the access of a team."],
    [
      409,
      "caller_access_from_team",
      "You have access to this site only through a team, so you cannot change the access of a team or its staff.",
    ],
    [400, "team_access_roles_required", "Choose at least one role."],
    [
      400,
      "team_access_role_not_in_site",
      "A role that you chose is not a role of this site. Reload the page and try again.",
    ],
    [400, "team_access_role_administrative", "A team can have only roles for data. Choose other roles."],
    [403, "team_access_role_wider_than_approver", "A role that you chose gives more than you hold. Choose other roles."],
    [400, "team_access_limit_required", "This access must be limited to the last 24 hours."],
    [
      403,
      "team_access_end_outlasts_approver",
      "The access cannot last longer than your own access. Choose an earlier end date.",
    ],
    [400, "team_access_end_invalid", "The end date must be in the future. Choose a later date."],
  ])("words a %i %s on the card and keeps the form open", async (status, code, sentence) => {
    mocks.editTeamAccess.mockRejectedValue(refusal(status, code));
    show();

    await page.getByRole("button", { name: "Change the access of Clinic" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent(sentence);
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    await expect.element(page.getByRole("button", { name: "Save" })).toBeVisible();
  });

  it("says the team no longer has access, and reads the list again, on a 404", async () => {
    mocks.editTeamAccess.mockRejectedValue({ status: 404, body: { message: "Not found" } });
    const { onStale } = show();

    await page.getByRole("button", { name: "Change the access of Clinic" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent("This team no longer has access.");
    expect(onStale).toHaveBeenCalled();
  });
});
