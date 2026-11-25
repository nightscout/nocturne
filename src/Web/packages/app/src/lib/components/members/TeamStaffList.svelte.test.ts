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

function show(staff: TenantTeamStaff[], extra: Partial<{ canDecide: boolean; busy: string | null }> = {}) {
  const onApprove = vi.fn();
  const onDecline = vi.fn();
  render(TeamStaffList, {
    team: teamWith(staff),
    canDecide: true,
    busy: null,
    onApprove,
    onDecline,
    ...extra,
  });
  return { onApprove, onDecline };
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
    const onApprove = vi.fn();
    const onDecline = vi.fn();
    render(TeamStaffList, {
      team: { ...teamWith([approved]), accessEndsAt: accessEndInstant("2020-01-01"), hasEnded: true },
      canDecide: true,
      busy: null,
      onApprove,
      onDecline,
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
