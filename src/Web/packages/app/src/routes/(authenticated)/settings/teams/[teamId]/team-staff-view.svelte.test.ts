import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamDetail } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  getTeam: vi.fn(),
  removeStaff: vi.fn(),
  goto: vi.fn(),
}));

const live = $state({ error: undefined as unknown });

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getTeam: (id: string) => {
    mocks.getTeam(id);
    return {
      get current() {
        return live.error === undefined ? mocks.team : undefined;
      },
      get error() {
        return live.error;
      },
      loading: false,
      ready: true,
    };
  },
  removeStaff: (arg: unknown) => remoteCommand(mocks.removeStaff)(arg),
  updateStaff: vi.fn(),
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

vi.mock("$app/navigation", () => ({
  goto: (...args: unknown[]) => mocks.goto(...args),
}));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const me = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

const staffView: TeamDetail = {
  id: teamId,
  name: "Clinic",
  managers: [],
  managerNames: ["Sam", "Robin"],
  invites: [],
  staff: [],
  staffInvites: [],
  accessLinks: [],
  sites: [],
  suggestableRoles: [
    { slug: "caretaker", name: "Caretaker", permissions: ["glucose.read"] },
    { slug: "viewer", name: "Viewer", permissions: ["glucose.read"] },
  ],
  isLastManager: false,
  canDelete: false,
  callerKind: "staff",
  ownStaff: {
    narrowing: { roleCeilingSlug: "viewer", limitTo24Hours: true, endsAt: "2099-02-01T00:00:00Z" },
    hasEnded: false,
  },
};

const dialog = () => page.getByRole("alertdialog");
const leaveButton = () => page.getByRole("button", { name: "Leave", exact: true });

describe("team page for a staff member", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: me } };
    mocks.team = staffView;
    live.error = undefined;
    mocks.getTeam.mockReset();
    mocks.removeStaff.mockReset();
    mocks.goto.mockReset();
  });

  it("names the team and lists the managers from their names", async () => {
    render(TeamPage);

    await expect.element(page.getByRole("heading", { name: "Clinic" })).toBeVisible();
    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Robin", { exact: true })).toBeVisible();
    await expect
      .element(page.getByText("You are staff of this team. These are its managers and your access."))
      .toBeVisible();
  });

  it("states what narrows the staff member", async () => {
    render(TeamPage);

    const access = page.getByText("No more than the role Viewer", { exact: false });
    await expect.element(access).toBeVisible();
    await expect.element(access).toHaveTextContent("Only the last 24 hours");
    await expect.element(access).toHaveTextContent("Access ends");
  });

  it("says which sites the staff member sees while the access lasts", async () => {
    render(TeamPage);

    await expect
      .element(
        page.getByText(
          "You see the sites that the team has access to and that let you in. You cannot manage the team.",
        ),
      )
      .toBeVisible();
    expect(page.getByText("Your access through this team ended", { exact: false }).elements()).toHaveLength(0);
  });

  it("says the access ended, and claims no current access, when the API says it has", async () => {
    mocks.team = {
      ...staffView,
      ownStaff: { narrowing: { limitTo24Hours: false, endsAt: "2020-02-01T12:00:00Z" }, hasEnded: true },
    };
    render(TeamPage);

    const access = page.getByText("Everything the team has", { exact: false });
    await expect.element(access).toBeVisible();
    await expect.element(access).toHaveTextContent("Access ended");
    await expect
      .element(page.getByText(/^Your access through this team ended on .*2020.*\. You cannot manage the team\.$/))
      .toBeVisible();
    expect(page.getByText("Access ends", { exact: false }).elements()).toHaveLength(0);
    expect(page.getByText("You see the sites", { exact: false }).elements()).toHaveLength(0);

    await leaveButton().click();
    await expect.element(dialog()).toHaveTextContent("Leave Clinic? A manager of the team must invite you again.");
    expect(dialog().getByText("You stop seeing", { exact: false }).elements()).toHaveLength(0);
  });

  it("leaves with the caller's own id after a confirmation that says what happens, then returns to the list", async () => {
    mocks.removeStaff.mockResolvedValue({ success: true });
    render(TeamPage);

    await leaveButton().click();
    await expect
      .element(dialog())
      .toHaveTextContent(
        "Leave Clinic? You stop seeing the sites that the team has access to and that let you in. A manager of the team must invite you again.",
      );
    expect(mocks.removeStaff).not.toHaveBeenCalled();

    await dialog().getByRole("button", { name: "Leave team" }).click();

    await vi.waitFor(() => expect(mocks.removeStaff).toHaveBeenCalledWith({ teamId, subjectId: me }));
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith("/settings/teams"));
  });

  it("does not leave when the confirmation is cancelled", async () => {
    render(TeamPage);

    await leaveButton().click();
    await dialog().getByRole("button", { name: "Cancel" }).click();

    await expect.element(dialog()).not.toBeInTheDocument();
    expect(mocks.removeStaff).not.toHaveBeenCalled();
  });

  it("shows an error and stays on the page when leaving fails", async () => {
    mocks.removeStaff.mockRejectedValue({ status: 500, body: { message: "English detail" } });
    render(TeamPage);

    await leaveButton().click();
    await dialog().getByRole("button", { name: "Leave team" }).click();

    await expect.element(page.getByRole("alert")).toBeVisible();
    expect(mocks.goto).not.toHaveBeenCalled();
    await expect.element(leaveButton()).toBeVisible();
  });

  it("shows no error for the team while the staff member leaves it, even when the team read fails", async () => {
    let finish: () => void = () => {};
    mocks.removeStaff.mockImplementation(async () => {
      live.error = { status: 404, body: { message: "Not found" } };
      await new Promise<void>((resolve) => (finish = resolve));
      return { success: true };
    });
    render(TeamPage);

    await leaveButton().click();
    await dialog().getByRole("button", { name: "Leave team" }).click();
    await vi.waitFor(() => expect(mocks.removeStaff).toHaveBeenCalled());

    await expect.poll(() => page.getByText("Could not load the team").elements().length).toBe(0);

    finish();
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith("/settings/teams"));
  });

  it("shows no section that only a manager may use", async () => {
    render(TeamPage);

    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    for (const title of [
      "Name",
      "Invite a manager",
      "Staff",
      "Invite staff",
      "Request access",
      "Sites this team has access to",
      "Delete team",
    ]) {
      expect(page.getByText(title, { exact: true }).elements(), title).toHaveLength(0);
    }
    for (const name of ["Rename team", "Make link", "Make staff link", "Make access link", "Remove Sam"]) {
      expect(page.getByRole("button", { name }).elements(), name).toHaveLength(0);
    }
  });
});
