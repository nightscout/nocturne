import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { TenantTeamAccess, TenantTeamStaff } from "@nocturne/api-client";
import TeamStaffRule from "./TeamStaffRule.svelte";

const staffMember = (n: number, status: string): TenantTeamStaff => ({
  subjectId: `0000000${n}-0000-0000-0000-000000000000`,
  displayName: `Staff ${n}`,
  status,
});

function teamWith(teamManagesStaff: boolean, staff: TenantTeamStaff[] = []): TenantTeamAccess {
  return { teamId: "44444444-4444-4444-4444-444444444444", teamName: "Clinic", teamManagesStaff, staff };
}

function show(team: TenantTeamAccess, canDecide = true) {
  const onChange = vi.fn();
  render(TeamStaffRule, { team, canDecide, busy: null, onChange });
  return { onChange };
}

const theSwitch = () => page.getByRole("switch", { name: "The team decides who on its staff can see this site" });

describe("TeamStaffRule", () => {
  it("shows the switch on, with what on does", async () => {
    show(teamWith(true));

    await expect.element(theSwitch()).toBeChecked();
    await expect
      .element(page.getByText("On: new staff, and staff that the team gives more access, can see this site at once."))
      .toBeVisible();
    await expect
      .element(page.getByText("If you change it to off, approved staff keep their access.", { exact: false }))
      .toBeVisible();
  });

  it("shows the switch off, with what off does", async () => {
    show(teamWith(false));

    await expect.element(theSwitch()).not.toBeChecked();
    await expect
      .element(
        page.getByText(
          "Off: each new staff member, and each one that the team gives more access, waits for an owner's approval.",
        ),
      )
      .toBeVisible();
    await expect
      .element(page.getByText("If you change it to on, all waiting staff are approved.", { exact: false }))
      .toBeVisible();
  });

  it("names the switch's description for a screen reader", async () => {
    show(teamWith(true));

    await expect.element(theSwitch()).toHaveAccessibleDescription(/On: new staff/);
  });

  it("asks first, naming how many wait, before turning it on approves them", async () => {
    const { onChange } = show(teamWith(false, [staffMember(1, "pending"), staffMember(2, "pending"), staffMember(3, "approved")]));

    await theSwitch().click();
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Approve all waiting staff?");
    await expect
      .element(dialog)
      .toHaveTextContent("2 staff members of Clinic wait for approval. If you change this setting to on, they are all approved.");
    expect(onChange).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Approve all" }).click();

    expect(onChange).toHaveBeenCalledWith(true);
  });

  it("names one waiting staff member in the singular", async () => {
    show(teamWith(false, [staffMember(1, "pending")]));

    await theSwitch().click();

    await expect
      .element(page.getByRole("alertdialog"))
      .toHaveTextContent("1 staff member of Clinic waits for approval. If you change this setting to on, they are approved.");
  });

  it("does not turn it on when the confirmation is cancelled", async () => {
    const { onChange } = show(teamWith(false, [staffMember(1, "pending")]));

    await theSwitch().click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();

    await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("turns it on at once when nobody waits", async () => {
    const { onChange } = show(teamWith(false, [staffMember(1, "declined")]));

    await theSwitch().click();

    expect(onChange).toHaveBeenCalledWith(true);
    expect(page.getByRole("alertdialog").elements()).toHaveLength(0);
  });

  it("turns it off without asking", async () => {
    const { onChange } = show(teamWith(true));

    await theSwitch().click();

    expect(onChange).toHaveBeenCalledWith(false);
    expect(page.getByRole("alertdialog").elements()).toHaveLength(0);
  });

  it("states the setting with no switch to a reader who is not an owner", async () => {
    show(teamWith(false), false);

    await expect
      .element(page.getByText("An owner of this site approves each staff member of this team."))
      .toBeVisible();
    expect(page.getByRole("switch").elements()).toHaveLength(0);
  });

  it("states that the team decides, with no switch, to a reader who is not an owner", async () => {
    show(teamWith(true), false);

    await expect.element(page.getByText("The team decides who on its staff can see this site.")).toBeVisible();
    expect(page.getByRole("switch").elements()).toHaveLength(0);
  });
});
