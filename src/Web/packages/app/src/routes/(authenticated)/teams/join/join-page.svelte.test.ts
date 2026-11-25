import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamInviteDetails } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  getInviteInfo: vi.fn(),
  acceptInvite: vi.fn(),
  goto: vi.fn(),
}));

const live = $state({ info: undefined as TeamInviteDetails | undefined, error: undefined as unknown });

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getInviteInfo: (token: string) => {
    mocks.getInviteInfo(token);
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
  acceptInvite: (arg: unknown) => remoteCommand(mocks.acceptInvite)(arg),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: (...args: unknown[]) => mocks.goto(...args),
}));

import JoinPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";

function openLink(search: string) {
  pageState.url = new URL(`https://example.org/teams/join${search}`);
}

function refused(status: number, code?: string) {
  return { status, body: { message: "English detail", ...(code ? { code } : {}) } };
}

const acceptButton = () => page.getByRole("button", { name: "Accept invite" });

describe("team join page", () => {
  beforeEach(() => {
    openLink("?token=abc");
    pageState.data = {};
    live.info = { teamId, teamName: "Clinic", createdByName: "Sam", kind: "manager", alreadyMember: false };
    live.error = undefined;
    mocks.getInviteInfo.mockReset();
    mocks.acceptInvite.mockReset();
    mocks.goto.mockReset();
  });

  it("names the team and who invited the person, with one accept control", async () => {
    render(JoinPage);

    await expect.element(page.getByText("Sam", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Clinic", { exact: true })).toBeVisible();
    await expect.element(page.getByText("to be a manager of", { exact: false })).toBeVisible();
    await expect.element(acceptButton()).toBeVisible();
    expect(mocks.getInviteInfo).toHaveBeenCalledWith("abc");
  });

  it("words the invite from the kind the API sends", async () => {
    live.info = { teamId, teamName: "Clinic", createdByName: "Sam", kind: "other", alreadyMember: false };
    render(JoinPage);

    await expect.element(page.getByText("invited you to join", { exact: false })).toBeVisible();
    expect(page.getByText("to be a manager of", { exact: false }).elements()).toHaveLength(0);
  });

  it("accepts with the token from the link and opens the team", async () => {
    mocks.acceptInvite.mockResolvedValue({ teamId });
    render(JoinPage);

    await acceptButton().click();

    await vi.waitFor(() => expect(mocks.acceptInvite).toHaveBeenCalledWith("abc"));
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith(`/settings/teams/${teamId}`));
  });

  it("shows no refusal after an accept succeeded, when the refreshed lookup says the link is used up", async () => {
    let finish: () => void = () => {};
    mocks.acceptInvite.mockImplementation(async () => {
      live.error = refused(400, "team_invite_used_up");
      await new Promise<void>((resolve) => (finish = resolve));
      return { teamId };
    });
    render(JoinPage);

    await acceptButton().click();
    await vi.waitFor(() => expect(mocks.acceptInvite).toHaveBeenCalled());

    await expect.element(page.getByText("Joining...")).toBeVisible();
    expect(page.getByText("Invite unavailable").elements()).toHaveLength(0);
    expect(page.getByText("no uses left", { exact: false }).elements()).toHaveLength(0);

    finish();
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalledWith(`/settings/teams/${teamId}`));
  });

  it("does not show the already-a-member state while an accept succeeds", async () => {
    let finish: () => void = () => {};
    mocks.acceptInvite.mockImplementation(async () => {
      live.info = { ...live.info!, alreadyMember: true };
      await new Promise<void>((resolve) => (finish = resolve));
      return { teamId };
    });
    render(JoinPage);

    await acceptButton().click();
    await vi.waitFor(() => expect(mocks.acceptInvite).toHaveBeenCalled());

    expect(page.getByText("You are already a member").elements()).toHaveLength(0);

    finish();
    await vi.waitFor(() => expect(mocks.goto).toHaveBeenCalled());
  });

  it("still shows the sentence for a link that is used up before any accept", async () => {
    live.error = refused(400, "team_invite_used_up");
    render(JoinPage);

    await expect.element(page.getByText("Invite unavailable")).toBeVisible();
    await expect
      .element(page.getByText("This invite link has no uses left. Ask a manager of the team for a new link."))
      .toBeVisible();
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("keeps the sign-in-again sentence for a session that started from a sign-in link", async () => {
    live.error = refused(403, "sign_in_required");
    render(JoinPage);

    await expect.element(page.getByText("Sign in again to do this.", { exact: false })).toBeVisible();
    expect(page.getByText("Your sign-in cannot join a team.").elements()).toHaveLength(0);
  });

  it("words a refused accept from its code and does not leave the page", async () => {
    mocks.acceptInvite.mockRejectedValue(refused(400, "team_invite_expired"));
    render(JoinPage);

    await acceptButton().click();

    await expect
      .element(page.getByText("This invite link has expired. Ask a manager of the team for a new link."))
      .toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(mocks.goto).not.toHaveBeenCalled();
  });

  it("says a member is already in the team and links to it with no accept control", async () => {
    live.info = { teamId, teamName: "Clinic", createdByName: "Sam", kind: "manager", alreadyMember: true };
    render(JoinPage);

    await expect.element(page.getByText("You are already a member", { exact: true })).toBeVisible();
    await expect.element(page.getByText("You are already a member of", { exact: false })).toBeVisible();
    expect(page.getByText("manager", { exact: false }).elements()).toHaveLength(0);
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
  ])("words a link that %s from its code, with no accept control", async (_case, code, sentence) => {
    live.error = refused(400, code);
    render(JoinPage);

    await expect.element(page.getByText("Invite unavailable")).toBeVisible();
    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(acceptButton().elements()).toHaveLength(0);
    expect(page.getByRole("link", { name: "Open the team" }).elements()).toHaveLength(0);
  });

  it("says a link the API does not know is not valid", async () => {
    live.error = refused(404);
    render(JoinPage);

    await expect
      .element(page.getByText("This invite link is not valid.", { exact: false }))
      .toBeVisible();
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("does not call a refused sign-in a bad link", async () => {
    live.error = refused(403);
    render(JoinPage);

    await expect.element(page.getByText("Your sign-in cannot join a team.")).toBeVisible();
    expect(page.getByText("This invite link", { exact: false }).elements()).toHaveLength(0);
    expect(acceptButton().elements()).toHaveLength(0);
  });

  it("tells the person to wait when the lookup is rate limited", async () => {
    live.error = refused(429);
    render(JoinPage);

    await expect.element(page.getByText("Wait a few minutes and try again.", { exact: false })).toBeVisible();
  });

  it("shows one sentence, and asks the API for nothing, for the demo visitor", async () => {
    pageState.data = { refusedAsDemoSubject: true };
    render(JoinPage);

    await expect.element(page.getByText("Teams are not available in the demo.")).toBeVisible();
    expect(mocks.getInviteInfo).not.toHaveBeenCalled();
    expect(acceptButton().elements()).toHaveLength(0);
    expect(document.querySelectorAll("p p")).toHaveLength(0);
  });

  it("says so, and asks for nothing, when the link has no token", async () => {
    openLink("");
    render(JoinPage);

    await expect.element(page.getByText("This link has no invite token.", { exact: false })).toBeVisible();
    expect(mocks.getInviteInfo).not.toHaveBeenCalled();
    expect(acceptButton().elements()).toHaveLength(0);
  });
});
