import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { TenantTeamAccess, TenantTeamStaff } from "@nocturne/api-client";
import { formatShortDate } from "#lib/utils/formatting.js";
import { accessEndDate, accessEndInstant } from "./access-end.js";
import TeamStaffList from "./TeamStaffList.svelte";

const waiting: TenantTeamStaff = {
  subjectId: "11111111-1111-1111-1111-111111111111",
  displayName: "Wren",
  status: "pending",
  narrowing: { limitTo24Hours: false },
  hasEnded: false,
};
const approved: TenantTeamStaff = {
  subjectId: "22222222-2222-2222-2222-222222222222",
  displayName: "Ari",
  status: "approved",
  narrowing: { limitTo24Hours: false },
  hasEnded: false,
};
const declined: TenantTeamStaff = {
  subjectId: "33333333-3333-3333-3333-333333333333",
  displayName: "Dee",
  status: "declined",
  narrowing: { limitTo24Hours: false },
  hasEnded: false,
};

function teamWith(staff: TenantTeamStaff[]): TenantTeamAccess {
  return { teamId: "44444444-4444-4444-4444-444444444444", teamName: "Clinic", staff };
}

function show(
  staff: TenantTeamStaff[],
  extra: Partial<{ canDecide: boolean; currentSubjectId: string; busy: string | null; team: TenantTeamAccess }> = {},
) {
  const onApprove = vi.fn();
  const onDecline = vi.fn();
  const onBlock = vi.fn();
  const onUnblock = vi.fn();
  render(TeamStaffList, {
    team: teamWith(staff),
    canDecide: true,
    busy: null,
    onApprove,
    onDecline,
    onBlock,
    onUnblock,
    ...extra,
  });
  return { onApprove, onDecline, onBlock, onUnblock };
}

const row = (name: string) => page.getByRole("listitem").filter({ hasText: name }).first();

describe("TeamStaffList statuses", () => {
  it("states each status in plain words for an owner", async () => {
    show([waiting, approved, declined]);

    await expect.element(row("Wren")).toHaveTextContent("Waits for your approval");
    await expect.element(row("Ari")).toHaveTextContent("Approved");
    await expect.element(row("Dee")).toHaveTextContent("Declined");
  });

  it("says Approved, not that they see the site, for a staff member whose own time ended", async () => {
    show([{ ...approved, narrowing: { limitTo24Hours: false, endsAt: accessEndInstant("2020-01-01") }, hasEnded: true }]);

    await expect.element(row("Ari")).toHaveTextContent("Approved");
    expect(page.getByText("Can see this site").elements()).toHaveLength(0);
  });

  it("says Approved, not that they see the site, when the team's access has ended", async () => {
    show([approved], {
      team: { ...teamWith([approved]), accessEndsAt: accessEndInstant("2020-01-01"), hasEnded: true },
    });

    await expect.element(row("Ari")).toHaveTextContent("Approved");
    expect(page.getByText("Can see this site").elements()).toHaveLength(0);
  });

  it("says a waiting staff member waits for an owner when the reader is not one", async () => {
    show([waiting], { canDecide: false });

    await expect.element(row("Wren")).toHaveTextContent("Waits for an owner's approval");
    expect(page.getByText("Waits for your approval").elements()).toHaveLength(0);
  });

  it("keeps the order the API gives, which puts waiting staff first", async () => {
    show([waiting, approved, declined]);

    await expect.element(row("Wren")).toBeVisible();
    const names = page
      .getByRole("list", { name: "Staff of Clinic" })
      .element()
      .querySelectorAll(":scope > li");
    expect([...names].map((li) => li.textContent?.match(/Wren|Ari|Dee/)?.[0])).toEqual(["Wren", "Ari", "Dee"]);
  });

  it("says the team has no staff when it has none", async () => {
    show([]);

    await expect.element(page.getByText("This team has no staff.")).toBeVisible();
  });
});

describe("TeamStaffList narrowing", () => {
  const end = accessEndInstant("2099-07-14");

  it("names the role ceiling, the 24-hour limit and the end of a staff member", async () => {
    show([
      {
        ...approved,
        narrowing: { roleCeilingSlug: "clinician", limitTo24Hours: true, endsAt: end },
        roleCeilingName: "Clinician",
      },
    ]);

    const limits = page.getByRole("list", { name: "What limits Ari" });
    await expect.element(limits).toHaveTextContent("Up to the role Clinician");
    await expect.element(limits).toHaveTextContent("Last 24 hours only");
    await expect
      .element(limits)
      .toHaveTextContent(`On the team until ${formatShortDate(accessEndDate(end), true)}`);
  });

  it("says when a staff member's time on the team has ended", async () => {
    show([{ ...approved, narrowing: { limitTo24Hours: false, endsAt: end }, hasEnded: true }]);

    await expect
      .element(page.getByRole("list", { name: "What limits Ari" }))
      .toHaveTextContent(`Time on the team ended ${formatShortDate(accessEndDate(end), true)}`);
    expect(page.getByText("On the team until", { exact: false }).elements()).toHaveLength(0);
  });

  it("shows nothing that limits a staff member with no ceiling, limit or end", async () => {
    show([approved]);

    await expect.element(row("Ari")).toBeVisible();
    expect(page.getByRole("list", { name: "What limits Ari" }).elements()).toHaveLength(0);
  });
});

describe("TeamStaffList owner controls", () => {
  it("offers Approve to waiting and declined staff, and Decline to waiting and approved staff", async () => {
    show([waiting, approved, declined]);

    await expect.element(page.getByRole("button", { name: "Approve Wren" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Decline Wren" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Decline Ari" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Approve Dee" })).toBeVisible();
    expect(page.getByRole("button", { name: "Approve Ari" }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Decline Dee" }).elements()).toHaveLength(0);
  });

  it("approves with the staff member's subject", async () => {
    const { onApprove } = show([waiting]);

    await page.getByRole("button", { name: "Approve Wren" }).click();

    expect(onApprove).toHaveBeenCalledWith(waiting.subjectId);
  });

  it("declines only after a confirmation that says what follows", async () => {
    const { onDecline } = show([approved]);

    await page.getByRole("button", { name: "Decline Ari" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Decline Ari?");
    await expect.element(dialog).toHaveTextContent("They do not see this site's data through the team.");
    await expect
      .element(dialog)
      .toHaveTextContent(
        "They stay declined if they leave the team and join again, until you approve them or stop the team's access.",
      );
    expect(onDecline).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Decline" }).click();

    expect(onDecline).toHaveBeenCalledWith(approved.subjectId);
  });

  it("does not decline when the confirmation is cancelled", async () => {
    const { onDecline } = show([approved]);

    await page.getByRole("button", { name: "Decline Ari" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();

    await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
    expect(onDecline).not.toHaveBeenCalled();
  });

  it("disables every control while a command runs", async () => {
    show([waiting, approved], { busy: `approve:${waiting.subjectId}` });

    await expect.element(page.getByRole("button", { name: "Approve Wren" })).toBeDisabled();
    await expect.element(page.getByRole("button", { name: "Decline Ari" })).toBeDisabled();
  });

  it("shows the staff to a reader who is not an owner with no control", async () => {
    show([waiting, approved, declined], { canDecide: false });

    await expect.element(row("Ari")).toHaveTextContent("Approved");
    expect(page.getByRole("button").elements()).toHaveLength(0);
  });
});

describe("TeamStaffList stopped staff", () => {
  const stopped = (member: TenantTeamStaff): TenantTeamStaff => ({ ...member, blocked: true });

  it.each([
    ["waiting", waiting],
    ["approved", approved],
    ["declined", declined],
  ])("shows Stopped in place of the status of a stopped %s staff member, with no approve or decline", async (_, member) => {
    show([stopped(member)]);

    const name = member.displayName ?? "";
    await expect.element(row(name)).toHaveTextContent("Stopped");
    await expect.element(row(name)).toHaveTextContent("Cannot see this site through any team.");
    for (const status of ["Approved", "Declined", "Waits for"]) {
      expect(row(name).getByText(status, { exact: false }).elements()).toHaveLength(0);
    }
    expect(page.getByRole("button", { name: /^(Approve|Decline) / }).elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: `Stop ${name}'s access` }).elements()).toHaveLength(0);
    await expect.element(page.getByRole("button", { name: `Let ${name} back in` })).toBeVisible();
  });

  it("does not say a stopped staff member sees the site while the owner trusts the whole team", async () => {
    show([stopped(waiting)], { team: { ...teamWith([stopped(waiting)]), staffRule: "team_trusted" } });

    await expect.element(row("Wren")).toHaveTextContent("Stopped");
    expect(page.getByText("Can see this site", { exact: false }).elements()).toHaveLength(0);
  });

  it.each(["owner_approves", "team_decides", "team_trusted"])(
    "offers an owner Stop access on every staff line under the rule %s",
    async (staffRule) => {
      const staff = [waiting, approved, declined];
      show(staff, { team: { ...teamWith(staff), staffRule } });

      for (const name of ["Wren", "Ari", "Dee"]) {
        await expect.element(page.getByRole("button", { name: `Stop ${name}'s access` })).toBeVisible();
      }
    },
  );

  it("offers an owner no Stop access on their own staff line", async () => {
    show([waiting, approved], { currentSubjectId: approved.subjectId });

    await expect.element(page.getByRole("button", { name: "Stop Wren's access" })).toBeVisible();
    expect(page.getByRole("button", { name: "Stop Ari's access" }).elements()).toHaveLength(0);
  });

  it("stops a staff member only after a confirmation that says what follows", async () => {
    const { onBlock } = show([approved]);

    await page.getByRole("button", { name: "Stop Ari's access" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Stop Ari's access?");
    await expect
      .element(dialog)
      .toHaveTextContent(
        "Ari stops seeing this site's data through every team, now and later. Access they have through you directly does not change. You can undo this.",
      );
    expect(onBlock).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Stop access" }).click();

    expect(onBlock).toHaveBeenCalledWith(approved.subjectId);
  });

  it("lets a stopped staff member back in only after a confirmation that says what they get", async () => {
    const { onUnblock } = show([stopped(approved)]);

    await page.getByRole("button", { name: "Let Ari back in" }).click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Let Ari back in?");
    await expect.element(dialog).toHaveTextContent("Ari gets back what each team gives them at this site.");
    await expect
      .element(dialog)
      .toHaveTextContent("If they have approval, or you trust the whole team, they see this site's data again.");
    await expect
      .element(dialog)
      .toHaveTextContent("If a team limits its staff to their groups, they must also be in a group with this site.");
    await expect
      .element(dialog)
      .toHaveTextContent("If they wait for approval and you do not trust the whole team, they wait again.");
    await expect
      .element(dialog)
      .toHaveTextContent("If they have a decline and you do not trust the whole team, they stay out.");
    expect(onUnblock).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Let back in" }).click();

    expect(onUnblock).toHaveBeenCalledWith(approved.subjectId);
  });

  it("does not stop a staff member when the confirmation is cancelled", async () => {
    const { onBlock } = show([approved]);

    await page.getByRole("button", { name: "Stop Ari's access" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();

    await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
    expect(onBlock).not.toHaveBeenCalled();
  });

  it("shows Stopped with no control to a reader who is not an owner", async () => {
    show([stopped(approved), waiting], { canDecide: false });

    await expect.element(row("Ari")).toHaveTextContent("Stopped");
    expect(page.getByRole("button").elements()).toHaveLength(0);
  });
});

describe("TeamStaffList staff outside the team's groups", () => {
  const outside = (member: TenantTeamStaff): TenantTeamStaff => ({ ...member, outsideTeamGroups: true });
  const narrowed = (staff: TenantTeamStaff[], staffRule: string): TenantTeamAccess => ({
    ...teamWith(staff),
    staffRule,
    staffReach: "their_groups",
  });
  const line = "Not in a group with this site, so they cannot see it through the team.";

  it.each(["owner_approves", "team_decides", "team_trusted"])(
    "says that a staff member outside the groups cannot see the site, and never that they can, under the rule %s",
    async (staffRule) => {
      const staff = [outside(waiting), outside(approved), outside(declined)];
      show(staff, { team: narrowed(staff, staffRule) });

      for (const name of ["Wren", "Ari", "Dee"]) {
        await expect.element(row(name)).toHaveTextContent(line);
      }
      expect(page.getByText("Can see this site", { exact: false }).elements()).toHaveLength(0);
    },
  );

  it("says Not approved, not that they wait, for a waiting staff member outside the groups, and keeps Approve and Decline", async () => {
    const staff = [outside(waiting), approved];
    show(staff, { team: narrowed(staff, "owner_approves") });

    await expect.element(row("Wren")).toHaveTextContent("Not approved");
    expect(row("Wren").getByText("Waits for", { exact: false }).elements()).toHaveLength(0);
    await expect.element(page.getByRole("button", { name: "Approve Wren" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Decline Wren" })).toBeVisible();
    expect(row("Ari").getByText(line).elements()).toHaveLength(0);
  });

  it("says Not approved, not that they wait for an owner, to a reader who is not an owner", async () => {
    const staff = [outside(waiting)];
    show(staff, { team: narrowed(staff, "owner_approves"), canDecide: false });

    await expect.element(row("Wren")).toHaveTextContent("Not approved");
    expect(row("Wren").getByText("Waits for", { exact: false }).elements()).toHaveLength(0);
  });

  it("says that a stopped staff member outside the groups cannot see the site through any team", async () => {
    const staff = [{ ...outside(approved), blocked: true }];
    show(staff, { team: narrowed(staff, "team_trusted") });

    await expect.element(row("Ari")).toHaveTextContent("Cannot see this site through any team.");
    expect(row("Ari").getByText(line).elements()).toHaveLength(0);
  });
});
