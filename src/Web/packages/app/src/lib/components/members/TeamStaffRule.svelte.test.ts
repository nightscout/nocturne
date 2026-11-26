import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { TenantTeamAccess, TenantTeamStaff } from "@nocturne/api-client";
import TeamStaffRule from "./TeamStaffRule.svelte";

const staffMember = (n: number, status: string, blocked = false): TenantTeamStaff => ({
  subjectId: `0000000${n}-0000-0000-0000-000000000000`,
  displayName: `Staff ${n}`,
  status,
  blocked,
});

function teamWith(staffRule: string, staff: TenantTeamStaff[] = []): TenantTeamAccess {
  return { teamId: "44444444-4444-4444-4444-444444444444", teamName: "Clinic", staffRule, staff };
}

function show(team: TenantTeamAccess, canDecide = true) {
  const onChange = vi.fn();
  render(TeamStaffRule, { team, canDecide, busy: null, onChange });
  return { onChange };
}

const ownerApproves = () => page.getByRole("radio", { name: /I approve each staff member/ });
const teamDecides = () => page.getByRole("radio", { name: /The team decides, and I hear about new staff/ });
const teamTrusted = () => page.getByRole("radio", { name: /I trust the whole team/ });

async function choose(choice: () => ReturnType<typeof page.getByRole>) {
  await choice().click();
  await page.getByRole("button", { name: "Change setting" }).click();
}

describe("TeamStaffRule", () => {
  it.each([
    ["owner_approves", ownerApproves],
    ["team_decides", teamDecides],
    ["team_trusted", teamTrusted],
  ])("shows the three choices with %s chosen", async (rule, chosen) => {
    show(teamWith(rule));

    await expect.element(page.getByRole("radiogroup", { name: "Who decides which staff of the team can see this site" })).toBeVisible();
    await expect.element(chosen()).toBeChecked();
    expect(page.getByRole("radio").elements()).toHaveLength(3);
    await expect.element(teamTrusted()).toHaveTextContent("you cannot decline one person");
  });

  it("asks first, naming how many wait, before letting the team decide approves them", async () => {
    const { onChange } = show(
      teamWith("owner_approves", [staffMember(1, "pending"), staffMember(2, "pending"), staffMember(3, "approved")]),
    );

    await choose(teamDecides);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Let the team decide?");
    await expect.element(dialog).toHaveTextContent("New staff of Clinic can see this site at once.");
    await expect.element(dialog).toHaveTextContent("2 waiting staff members are approved.");
    expect(onChange).not.toHaveBeenCalled();

    await dialog.getByRole("button", { name: "Change setting" }).click();

    expect(onChange).toHaveBeenCalledWith("team_decides");
  });

  it("names one waiting staff member in the singular", async () => {
    show(teamWith("owner_approves", [staffMember(1, "pending")]));

    await choose(teamDecides);

    await expect.element(page.getByRole("alertdialog")).toHaveTextContent("1 waiting staff member is approved.");
  });

  it("asks first, naming the waiting and the declined who get access, before trusting the whole team", async () => {
    const { onChange } = show(
      teamWith("owner_approves", [staffMember(1, "pending"), staffMember(2, "declined"), staffMember(3, "declined")]),
    );

    await choose(teamTrusted);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Trust the whole team?");
    await expect.element(dialog).toHaveTextContent("Every staff member of Clinic can see this site. You get no notices about them.");
    await expect.element(dialog).toHaveTextContent("1 waiting staff member gets access while you trust the whole team.");
    await expect.element(dialog).toHaveTextContent("2 declined staff members get access while you trust the whole team.");
    await expect.element(dialog).not.toHaveTextContent("that you did not stop");

    await dialog.getByRole("button", { name: "Change setting" }).click();

    expect(onChange).toHaveBeenCalledWith("team_trusted");
  });

  it("neither counts nor promises access to a stopped staff member when it asks to trust the whole team", async () => {
    show(teamWith("owner_approves", [staffMember(1, "pending", true), staffMember(2, "pending"), staffMember(3, "declined", true)]));

    await choose(teamTrusted);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent(
      "Every staff member of Clinic that you did not stop can see this site. You get no notices about them.",
    );
    await expect.element(dialog).toHaveTextContent("1 waiting staff member gets access while you trust the whole team.");
    await expect.element(dialog).not.toHaveTextContent("declined");
  });

  it("names no stopped staff member among those who lose access when it stops trusting the whole team", async () => {
    show(teamWith("team_trusted", [staffMember(1, "pending", true), staffMember(2, "declined", true), staffMember(3, "approved")]));

    await choose(ownerApproves);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Approve each staff member yourself?");
    await expect.element(dialog).not.toHaveTextContent("lose");
  });

  it("asks first, naming the waiting and the declined who lose access, before it stops trusting the whole team", async () => {
    const { onChange } = show(
      teamWith("team_trusted", [staffMember(1, "pending"), staffMember(2, "pending"), staffMember(3, "declined")]),
    );

    await choose(ownerApproves);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Approve each staff member yourself?");
    await expect.element(dialog).toHaveTextContent("Each new staff member of Clinic waits for your approval. Approved staff keep their access.");
    await expect.element(dialog).toHaveTextContent("2 waiting staff members lose access until you approve them.");
    await expect.element(dialog).toHaveTextContent("1 declined staff member loses access at once.");

    await dialog.getByRole("button", { name: "Change setting" }).click();

    expect(onChange).toHaveBeenCalledWith("owner_approves");
  });

  it("asks first, naming the waiting who are approved and the declined who lose access, before it stops trusting for the team deciding", async () => {
    const { onChange } = show(
      teamWith("team_trusted", [staffMember(1, "pending"), staffMember(2, "pending"), staffMember(3, "declined")]),
    );

    await choose(teamDecides);
    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("Let the team decide?");
    await expect.element(dialog).toHaveTextContent("2 waiting staff members are approved.");
    await expect.element(dialog).toHaveTextContent("1 declined staff member loses access at once.");
    await expect.element(dialog).not.toHaveTextContent("until you approve them");

    await dialog.getByRole("button", { name: "Change setting" }).click();

    expect(onChange).toHaveBeenCalledWith("team_decides");
  });

  it("names no count when nobody waits or was declined", async () => {
    show(teamWith("team_trusted", [staffMember(1, "approved")]));

    await choose(teamDecides);

    const dialog = page.getByRole("alertdialog");
    await expect.element(dialog).toHaveTextContent("New staff of Clinic can see this site at once.");
    await expect.element(dialog).not.toHaveTextContent("lose");
    await expect.element(dialog).not.toHaveTextContent("approved.");
  });

  it("changes nothing when the confirmation is cancelled, and goes back to the setting on Keep the setting", async () => {
    const { onChange } = show(teamWith("team_decides", [staffMember(1, "pending")]));

    await choose(teamTrusted);
    await page.getByRole("alertdialog").getByRole("button", { name: "Cancel" }).click();

    await expect.element(page.getByRole("alertdialog")).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();

    await page.getByRole("button", { name: "Keep the setting" }).click();

    await expect.element(teamDecides()).toBeChecked();
    await expect.element(page.getByRole("button", { name: "Change setting" })).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
  });

  it("puts focus on the chosen setting after Keep the setting", async () => {
    show(teamWith("team_decides"));

    await teamTrusted().click();
    await page.getByRole("button", { name: "Keep the setting" }).click();

    await expect.element(teamDecides()).toHaveFocus();
  });

  it("puts focus on the new setting once a confirmed change is done", async () => {
    const props = $state({
      team: teamWith("team_decides"),
      canDecide: true,
      busy: null as string | null,
      onChange: vi.fn(() => {
        props.busy = "rule";
      }),
    });
    render(TeamStaffRule, props);

    await choose(teamTrusted);
    await page.getByRole("alertdialog").getByRole("button", { name: "Change setting" }).click();

    expect(props.onChange).toHaveBeenCalledWith("team_trusted");
    await expect.element(teamTrusted()).toBeDisabled();
    props.team = { ...props.team, staffRule: "team_trusted" };
    props.busy = null;

    await expect.element(teamTrusted()).toBeChecked();
    await expect.element(teamTrusted()).toHaveFocus();
  });

  it("offers no change until another rule is chosen", async () => {
    show(teamWith("team_decides"));

    await expect.element(teamDecides()).toBeChecked();
    expect(page.getByRole("button", { name: "Change setting" }).elements()).toHaveLength(0);

    await teamTrusted().click();

    await expect.element(page.getByRole("button", { name: "Change setting" })).toBeVisible();
    expect(page.getByRole("alertdialog").elements()).toHaveLength(0);
  });

  it.each([
    ["owner_approves", "An owner of this site approves each staff member of this team."],
    ["team_decides", "The team decides who on its staff can see this site."],
    ["team_trusted", "This site trusts the whole team. Every staff member can see this site."],
  ])("states %s with no choices to a reader who is not an owner", async (rule, sentence) => {
    show(teamWith(rule), false);

    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByRole("radio").elements()).toHaveLength(0);
  });

  it("leaves stopped staff out when it tells a reader who is not an owner that the site trusts the whole team", async () => {
    show(teamWith("team_trusted", [staffMember(1, "pending", true), staffMember(2, "approved")]), false);

    await expect
      .element(page.getByText("This site trusts the whole team. Every staff member that the site did not stop can see this site."))
      .toBeVisible();
  });
});
