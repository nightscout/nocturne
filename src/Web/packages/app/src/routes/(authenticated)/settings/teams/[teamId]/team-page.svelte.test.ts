import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamDetail } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  renameTeam: vi.fn(),
  removeManager: vi.fn(),
  createManagerInvite: vi.fn(),
  revokeManagerInvite: vi.fn(),
  deleteTeam: vi.fn(),
  getTeam: vi.fn(),
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
  renameTeam: (arg: unknown) => remoteCommand(mocks.renameTeam)(arg),
  removeManager: (arg: unknown) => remoteCommand(mocks.removeManager)(arg),
  createManagerInvite: (arg: unknown) => remoteCommand(mocks.createManagerInvite)(arg),
  revokeManagerInvite: (arg: unknown) => remoteCommand(mocks.revokeManagerInvite)(arg),
  deleteTeam: (arg: unknown) => remoteCommand(mocks.deleteTeam)(arg),
  createAccessLink: vi.fn(),
  revokeAccessLink: vi.fn(),
  createStaffInvite: vi.fn(),
  revokeStaffInvite: vi.fn(),
  updateStaff: vi.fn(),
  removeStaff: vi.fn(),
}));

vi.mock("$app/navigation", () => ({
  goto: (...args: unknown[]) => mocks.goto(...args),
}));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const me = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const sam = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
const inviteId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

const twoManagers: TeamDetail = {
  id: teamId,
  name: "Clinic",
  managers: [
    { subjectId: me, displayName: "Alex" },
    { subjectId: sam, displayName: "Sam" },
  ],
  managerNames: ["Alex", "Sam"],
  invites: [],
  isLastManager: false,
  canDelete: false,
  callerKind: "manager",
};

const dialog = () => page.getByRole("alertdialog");
const leaveButton = () => page.getByRole("button", { name: "Leave", exact: true });

describe("team page", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: me } };
    mocks.team = twoManagers;
    live.error = undefined;
    mocks.getTeam.mockReset();
    mocks.renameTeam.mockReset();
    mocks.removeManager.mockReset();
    mocks.createManagerInvite.mockReset();
    mocks.revokeManagerInvite.mockReset();
    mocks.deleteTeam.mockReset();
    mocks.goto.mockReset();
  });

  it("renames the team with the trimmed name, and only when the name changed", async () => {
    mocks.renameTeam.mockResolvedValue({ success: true });
    render(TeamPage);

    const rename = page.getByRole("button", { name: "Rename team" });
    await expect.element(page.getByLabelText("Team name")).toHaveValue("Clinic");
    await expect.element(rename).toBeDisabled();

    await page.getByLabelText("Team name").fill("  Ward  ");
    await rename.click();

    await vi.waitFor(() =>
      expect(mocks.renameTeam).toHaveBeenCalledWith({ teamId, request: { name: "Ward" } }),
    );
  });

  it("lists the managers and marks the person", async () => {
    render(TeamPage);

    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    await expect.element(page.getByText("You", { exact: true })).toBeVisible();
  });

  it("removes another manager only after a confirmation that names them", async () => {
    mocks.removeManager.mockResolvedValue({ success: true });
    render(TeamPage);

    await page.getByRole("button", { name: "Remove Sam" }).click();
    await expect.element(dialog()).toBeVisible();
    await expect.element(dialog()).toHaveTextContent("Remove Sam as a manager of Clinic?");
    expect(mocks.removeManager).not.toHaveBeenCalled();

    await dialog().getByRole("button", { name: "Remove manager" }).click();

    await vi.waitFor(() =>
      expect(mocks.removeManager).toHaveBeenCalledWith({ teamId, subjectId: sam }),
    );
    expect(mocks.goto).not.toHaveBeenCalled();
  });

  it("does not remove a manager when the confirmation is cancelled", async () => {
    render(TeamPage);

    await page.getByRole("button", { name: "Remove Sam" }).click();
    await dialog().getByRole("button", { name: "Cancel" }).click();

    await expect.element(dialog()).not.toBeInTheDocument();
    expect(mocks.removeManager).not.toHaveBeenCalled();
  });

  it("leaves with the person's own id after a confirmation, then returns to the list", async () => {
    mocks.removeManager.mockResolvedValue({ success: true });
    render(TeamPage);

    await expect.element(leaveButton()).toBeEnabled();
    await leaveButton().click();
    await expect.element(dialog()).toHaveTextContent("Leave Clinic?");

    await dialog().getByRole("button", { name: "Leave team" }).click();

    await vi.waitFor(() =>
      expect(mocks.removeManager).toHaveBeenCalledWith({ teamId, subjectId: me }),
    );
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith("/settings/teams"));
  });

  it("disables Leave for the last manager and gives the reason beside it", async () => {
    mocks.team = {
      ...twoManagers,
      managers: [{ subjectId: me, displayName: "Alex" }],
      isLastManager: true,
    };
    render(TeamPage);

    await expect.element(leaveButton()).toBeDisabled();
    await expect
      .element(
        page.getByText("A team must keep one manager. Invite another manager before you leave."),
      )
      .toBeVisible();
  });

  it("gives no reason when the manager may leave", async () => {
    render(TeamPage);

    await expect.element(leaveButton()).toBeEnabled();
    expect(
      page.getByText("A team must keep one manager.", { exact: false }).elements(),
    ).toHaveLength(0);
  });

  it("hides Delete unless the API says the person can delete", async () => {
    render(TeamPage);

    await expect.element(leaveButton()).toBeVisible();
    expect(page.getByRole("button", { name: "Delete team" }).elements()).toHaveLength(0);
  });

  it("deletes after a confirmation that names the team and says every manager and staff member loses it", async () => {
    mocks.team = { ...twoManagers, canDelete: true };
    mocks.deleteTeam.mockResolvedValue({ success: true });
    render(TeamPage);

    await page.getByRole("button", { name: "Delete team" }).click();
    await expect
      .element(dialog())
      .toHaveTextContent("Delete Clinic? Every manager and staff member loses the team.");
    expect(mocks.deleteTeam).not.toHaveBeenCalled();

    await dialog().getByRole("button", { name: "Delete team" }).click();

    await vi.waitFor(() => expect(mocks.deleteTeam).toHaveBeenCalledWith(teamId));
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith("/settings/teams"));
  });

  it("does not delete when the confirmation is cancelled", async () => {
    mocks.team = { ...twoManagers, canDelete: true };
    render(TeamPage);

    await page.getByRole("button", { name: "Delete team" }).click();
    await dialog().getByRole("button", { name: "Cancel" }).click();

    await expect.element(dialog()).not.toBeInTheDocument();
    expect(mocks.deleteTeam).not.toHaveBeenCalled();
  });

  it("says near the invite control that a link stops working when its maker leaves", async () => {
    render(TeamPage);

    await expect
      .element(page.getByText("One person can use each link.", { exact: false }))
      .toHaveTextContent("A link stops working when the manager who made it leaves the team.");
  });

  it("says no link works when there are no pending invites", async () => {
    render(TeamPage);

    await expect.element(page.getByText("No link works now.")).toBeVisible();
  });

  it("lists each pending link with its maker and expiry, and revokes one", async () => {
    mocks.team = {
      ...twoManagers,
      invites: [
        {
          id: inviteId,
          label: "For Jo",
          expiresAt: "2026-12-01T00:00:00Z",
          useCount: 0,
          maxUses: 1,
          createdBySubjectId: sam,
          createdByName: "Sam",
        },
      ],
    };
    mocks.revokeManagerInvite.mockResolvedValue({ success: true });
    render(TeamPage);

    await expect.element(page.getByText("For Jo")).toBeVisible();
    await expect.element(page.getByText("Made by Sam", { exact: false })).toBeVisible();
    expect(page.getByText("No link works now.").elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Revoke the link For Jo" }).click();

    await vi.waitFor(() =>
      expect(mocks.revokeManagerInvite).toHaveBeenCalledWith({ teamId, inviteId }),
    );
  });

  it("shows a new link once, with a copy control and the statement that it is shown once", async () => {
    mocks.createManagerInvite.mockResolvedValue({
      id: inviteId,
      token: "secret",
      inviteUrl: "https://example.org/teams/join?token=secret",
      expiresAt: "2026-12-01T00:00:00Z",
    });
    render(TeamPage);

    expect(page.getByRole("button", { name: "Copy the invite link" }).elements()).toHaveLength(0);
    await page.getByLabelText("Label (optional)").fill("  For Jo ");
    await page.getByLabelText("30 days").first().click();
    await page.getByRole("button", { name: "Make link" }).click();

    await vi.waitFor(() =>
      expect(mocks.createManagerInvite).toHaveBeenCalledWith({
        teamId,
        request: { label: "For Jo", expiresInDays: 30, maxUses: 1 },
      }),
    );
    await expect
      .element(page.getByLabelText("Invite link", { exact: true }))
      .toHaveValue("https://example.org/teams/join?token=secret");
    await expect.element(page.getByRole("button", { name: "Copy the invite link" })).toBeVisible();
    await expect
      .element(page.getByText("We show the link one time only.", { exact: false }))
      .toBeVisible();
    await expect.element(page.getByLabelText("Label (optional)")).toHaveValue("");
  });

  it("words a refused link from its code, and shows no link", async () => {
    mocks.createManagerInvite.mockRejectedValue({
      status: 400,
      body: { message: "English detail", code: "team_invite_expiry_invalid" },
    });
    render(TeamPage);

    await page.getByRole("button", { name: "Make link" }).click();

    await expect
      .element(page.getByText("Nocturne does not allow this expiry. Select another expiry."))
      .toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Copy the invite link" }).elements()).toHaveLength(0);
  });

  it("says that one person can use each link", async () => {
    render(TeamPage);

    await expect.element(page.getByText("One person can use each link.", { exact: false })).toBeVisible();
  });

  it("describes the page under its heading for a manager", async () => {
    render(TeamPage);

    await expect
      .element(
        page.getByText(
          "The managers and staff of this team, the links that add them, and the sites that the team has access to.",
        ),
      )
      .toBeVisible();
  });

  it("words a refused leave from its code", async () => {
    mocks.removeManager.mockRejectedValue({
      status: 400,
      body: { message: "English detail", code: "team_last_manager" },
    });
    render(TeamPage);

    await leaveButton().click();
    await dialog().getByRole("button", { name: "Leave team" }).click();

    await expect
      .element(page.getByText("A team must keep one manager. Invite another manager first."))
      .toBeVisible();
    expect(mocks.goto).not.toHaveBeenCalled();
  });

  it("shows no error for the team while the person leaves it, even when the team read fails", async () => {
    let finish: () => void = () => {};
    mocks.removeManager.mockImplementation(async () => {
      live.error = { status: 404, body: { message: "Not found" } };
      await new Promise<void>((resolve) => (finish = resolve));
      return { success: true };
    });
    render(TeamPage);

    await leaveButton().click();
    await dialog().getByRole("button", { name: "Leave team" }).click();
    await vi.waitFor(() => expect(mocks.removeManager).toHaveBeenCalled());

    await expect.poll(() => page.getByText("Could not load the team").elements().length).toBe(0);
    expect(page.getByText("That item no longer exists", { exact: false }).elements()).toHaveLength(0);

    finish();
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith("/settings/teams"));
  });

  it("clears the shown link when that link is revoked", async () => {
    mocks.createManagerInvite.mockResolvedValue({
      id: inviteId,
      token: "secret",
      inviteUrl: "https://example.org/teams/join?token=secret",
      expiresAt: "2026-12-01T00:00:00Z",
    });
    mocks.team = {
      ...twoManagers,
      invites: [
        {
          id: inviteId,
          label: "For Jo",
          expiresAt: "2026-12-01T00:00:00Z",
          useCount: 0,
          maxUses: 1,
          createdBySubjectId: me,
          createdByName: "Alex",
        },
      ],
    };
    mocks.revokeManagerInvite.mockResolvedValue({ success: true });
    render(TeamPage);

    await page.getByRole("button", { name: "Make link" }).click();
    await expect.element(page.getByText("Your link is ready")).toBeVisible();

    await page.getByRole("button", { name: "Revoke the link For Jo" }).click();

    await vi.waitFor(() => expect(mocks.revokeManagerInvite).toHaveBeenCalled());
    await expect.poll(() => page.getByText("Your link is ready").elements().length).toBe(0);
  });

  it("keeps the shown link when a different link is revoked", async () => {
    mocks.createManagerInvite.mockResolvedValue({
      id: inviteId,
      token: "secret",
      inviteUrl: "https://example.org/teams/join?token=secret",
      expiresAt: "2026-12-01T00:00:00Z",
    });
    const otherId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    mocks.team = {
      ...twoManagers,
      invites: [
        {
          id: otherId,
          label: "Other",
          expiresAt: "2026-12-01T00:00:00Z",
          useCount: 0,
          maxUses: 1,
          createdBySubjectId: me,
          createdByName: "Alex",
        },
      ],
    };
    mocks.revokeManagerInvite.mockResolvedValue({ success: true });
    render(TeamPage);

    await page.getByRole("button", { name: "Make link" }).click();
    await page.getByRole("button", { name: "Revoke the link Other" }).click();

    await vi.waitFor(() => expect(mocks.revokeManagerInvite).toHaveBeenCalled());
    await expect.element(page.getByText("Your link is ready")).toBeVisible();
  });

  it("shows no manager controls to a caller who is not a manager", async () => {
    mocks.team = { ...twoManagers, callerKind: "other", canDelete: true, invites: [] };
    render(TeamPage);

    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    for (const name of ["Rename team", "Make link", "Delete team", "Remove Sam"]) {
      expect(page.getByRole("button", { name }).elements(), name).toHaveLength(0);
    }
    expect(page.getByLabelText("Team name").elements()).toHaveLength(0);
  });

  it("shows one sentence, and asks the API for nothing, for the demo visitor", async () => {
    pageState.data = { user: { subjectId: me }, refusedAsDemoSubject: true };
    render(TeamPage);

    await expect.element(page.getByText("Teams are not available in the demo.")).toBeVisible();
    expect(mocks.getTeam).not.toHaveBeenCalled();
  });
});
