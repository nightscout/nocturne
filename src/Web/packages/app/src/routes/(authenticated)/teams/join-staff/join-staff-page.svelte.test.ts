import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamStaffInviteDetails } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  getStaffInviteInfo: vi.fn(),
  acceptStaffInvite: vi.fn(),
  goto: vi.fn(),
}));

const live = $state({ info: undefined as TeamStaffInviteDetails | undefined, error: undefined as unknown });

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getStaffInviteInfo: (token: string) => {
    mocks.getStaffInviteInfo(token);
    return {
      get current() {
        return live.error === undefined ? live.info : undefined;
      },
      get error() {
        return live.error;
      },
      loading: false,
      ready: true,
    };
  },
  acceptStaffInvite: (arg: unknown) => remoteCommand(mocks.acceptStaffInvite)(arg),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: (...args: unknown[]) => mocks.goto(...args),
}));

import JoinStaffPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";

const roles = [
  { slug: "caretaker", name: "Caretaker", permissions: ["glucose.read"] },
  { slug: "viewer", name: "Viewer", permissions: ["glucose.read"] },
];

function openLink(search: string) {
  pageState.url = new URL(`https://example.org/teams/join-staff${search}`);
}

function refused(status: number, code?: string) {
  return { status, body: { message: "English detail", ...(code ? { code } : {}) } };
}

const acceptButton = () => page.getByRole("button", { name: "Accept invite" });

describe("team staff join page", () => {
  beforeEach(() => {
    openLink("?token=abc");
    pageState.data = {};
    live.info = {
      teamId,
      teamName: "Clinic",
      createdByName: "Sam",
      narrowing: { roleCeilingSlug: "viewer", limitTo24Hours: true, endsAt: "2099-02-01T00:00:00Z" },
      alreadyMember: false,
      suggestableRoles: roles,
    };
    live.error = undefined;
    mocks.getStaffInviteInfo.mockReset();
    mocks.acceptStaffInvite.mockReset();
    mocks.goto.mockReset();
  });

  it("names the team, who made the link and what joining gives", async () => {
    render(JoinStaffPage);

    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Clinic", { exact: true })).toBeVisible();
    await expect.element(page.getByText("invited you to be staff of", { exact: false })).toBeVisible();
    const access = page.getByText("No more than the role Viewer", { exact: false });
    await expect.element(access).toBeVisible();
    await expect.element(access).toHaveTextContent("Only the last 24 hours");
    await expect.element(access).toHaveTextContent("Access ends");
    await expect
      .element(
        page.getByText(
          "As staff, you see the sites that the team has access to and that let you in. You cannot manage the team.",
        ),
      )
      .toBeVisible();
    await expect
      .element(page.getByText("At some sites, an owner must approve you before you can see the site."))
      .toBeVisible();
    await expect.element(acceptButton()).toBeVisible();
    expect(mocks.getStaffInviteInfo).toHaveBeenCalledWith("abc");
  });

  it("says the staff member gets everything the team has when the link sets no ceiling", async () => {
    live.info = { ...live.info!, narrowing: { limitTo24Hours: false } };
    render(JoinStaffPage);

    await expect.element(page.getByText("Everything the team has", { exact: false })).toBeVisible();
    expect(page.getByText("Only the last 24 hours", { exact: false }).elements()).toHaveLength(0);
    expect(page.getByText("Access end", { exact: false }).elements()).toHaveLength(0);
  });

  it("accepts with the token from the link and opens the team", async () => {
    mocks.acceptStaffInvite.mockResolvedValue({ teamId });
    render(JoinStaffPage);

    await acceptButton().click();

    await vi.waitFor(() => expect(mocks.acceptStaffInvite).toHaveBeenCalledWith("abc"));
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith(`/settings/teams/${teamId}`));
  });

  it("shows no refusal after an accept succeeded, when the refreshed lookup says the link is used up", async () => {
    let finish: () => void = () => {};
    mocks.acceptStaffInvite.mockImplementation(async () => {
      live.error = refused(400, "team_invite_used_up");
      await new Promise<void>((resolve) => (finish = resolve));
      return { teamId };
    });
    render(JoinStaffPage);

    await acceptButton().click();
    await vi.waitFor(() => expect(mocks.acceptStaffInvite).toHaveBeenCalled());

    await expect.element(page.getByText("Joining...")).toBeVisible();
    expect(page.getByText("Invite unavailable").elements()).toHaveLength(0);

    finish();
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith(`/settings/teams/${teamId}`));
  });

  it.each([
    ["is already a member", "team_invite_already_member", "You are already a member of this team."],
    [
      "gives an end that passed",
      "team_staff_invite_end_passed",
      "The end date that this invite gives has passed. Ask a manager of the team for a new link.",
    ],
    ["has expired", "team_invite_expired", "This invite link has expired. Ask a manager of the team for a new link."],
  ])("words a refused accept for a link that %s and does not leave the page", async (_case, code, sentence) => {
    mocks.acceptStaffInvite.mockRejectedValue(refused(400, code));
    render(JoinStaffPage);

    await acceptButton().click();

    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(mocks.goto).not.toHaveBeenCalled();
  });

  it("says a member is already in the team and links to it with no accept control", async () => {
    live.info = { ...live.info!, alreadyMember: true };
    render(JoinStaffPage);

    await expect.element(page.getByText("You are already a member", { exact: true })).toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "Open the team" }))
      .toHaveAttribute("href", `/settings/teams/${teamId}`);
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it.each([
    ["has expired", "team_invite_expired", "This invite link has expired. Ask a manager of the team for a new link."],
    ["was revoked", "team_invite_revoked", "A manager revoked this invite link. Ask a manager of the team for a new link."],
    ["is used up", "team_invite_used_up", "This invite link has no uses left. Ask a manager of the team for a new link."],
    [
      "has a maker who left",
      "team_invite_no_longer_valid",
      "The manager who made this link left the team. Ask a manager of the team for a new link.",
    ],
    [
      "gives an end that passed",
      "team_staff_invite_end_passed",
      "The end date that this invite gives has passed. Ask a manager of the team for a new link.",
    ],
  ])("words a link that %s from its code, with no accept control", async (_case, code, sentence) => {
    live.error = refused(400, code);
    render(JoinStaffPage);

    await expect.element(page.getByText("Invite unavailable")).toBeVisible();
    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("says a link the API does not know is not valid", async () => {
    live.error = refused(404);
    render(JoinStaffPage);

    await expect.element(page.getByText("This invite link is not valid.", { exact: false })).toBeVisible();
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("does not call a refused sign-in a bad link", async () => {
    live.error = refused(403);
    render(JoinStaffPage);

    await expect.element(page.getByText("Your sign-in cannot join a team.")).toBeVisible();
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("keeps the sign-in-again sentence for a session that started from a sign-in link", async () => {
    live.error = refused(403, "sign_in_required");
    render(JoinStaffPage);

    await expect.element(page.getByText("Sign in again to do this.", { exact: false })).toBeVisible();
  });

  it("shows one sentence, and asks the API for nothing, for the demo visitor", async () => {
    pageState.data = { refusedAsDemoSubject: true };
    render(JoinStaffPage);

    await expect.element(page.getByText("Teams are not available in the demo.")).toBeVisible();
    expect(mocks.getStaffInviteInfo).not.toHaveBeenCalled();
  });

  it("says so, and asks for nothing, when the link has no token", async () => {
    openLink("");
    render(JoinStaffPage);

    await expect.element(page.getByText("This link has no invite token.", { exact: false })).toBeVisible();
    expect(mocks.getStaffInviteInfo).not.toHaveBeenCalled();
  });
});
