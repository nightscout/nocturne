import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";
import { page as pageState } from "$app/state";
import type { MyTeamsResponse } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  teams: undefined as unknown as MyTeamsResponse,
  getMyTeams: vi.fn(),
  createTeam: vi.fn(),
  goto: vi.fn(),
}));

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getMyTeams: () => {
    mocks.getMyTeams();
    return remoteQuery(() => mocks.teams);
  },
  createTeam: (arg: unknown) => remoteCommand(mocks.createTeam)(arg),
}));

vi.mock("$app/navigation", () => ({
  goto: (...args: unknown[]) => mocks.goto(...args),
}));

import TeamsPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";

describe("teams page", () => {
  beforeEach(() => {
    mocks.teams = { items: [{ id: teamId, name: "Clinic", kind: "manager" }], canCreate: true };
    mocks.getMyTeams.mockReset();
    mocks.createTeam.mockReset();
    mocks.goto.mockReset();
    pageState.data = {};
  });

  it("lists each team as a link to its page, with the part the person has in it", async () => {
    render(TeamsPage);

    const link = page.getByRole("link", { name: /Clinic/ });
    await expect.element(link).toHaveAttribute("href", `/settings/teams/${teamId}`);
    await expect.element(page.getByText("Manager", { exact: true })).toBeVisible();
  });

  it("lists a team the person is staff of, with the staff badge", async () => {
    mocks.teams = { items: [{ id: teamId, name: "Clinic", kind: "staff" }], canCreate: true };
    render(TeamsPage);

    await expect
      .element(page.getByRole("link", { name: /Clinic/ }))
      .toHaveAttribute("href", `/settings/teams/${teamId}`);
    await expect.element(page.getByText("Staff", { exact: true })).toBeVisible();
    expect(page.getByText("Manager", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows the kind the API sends, not a fixed one", async () => {
    mocks.teams = { items: [{ id: teamId, name: "Clinic", kind: "other" }], canCreate: true };
    render(TeamsPage);

    await expect.element(page.getByText("Member", { exact: true })).toBeVisible();
    expect(page.getByText("Manager", { exact: true }).elements()).toHaveLength(0);
  });

  it("shows one sentence, and asks the API for nothing, for the demo visitor", async () => {
    pageState.data = { refusedAsDemoSubject: true };
    render(TeamsPage);

    await expect.element(page.getByText("Teams are not available in the demo.")).toBeVisible();
    expect(mocks.getMyTeams).not.toHaveBeenCalled();
    expect(page.getByRole("button", { name: "Create team" }).elements()).toHaveLength(0);
  });

  it("says so when the person is in no team", async () => {
    mocks.teams = { items: [], canCreate: true };
    render(TeamsPage);

    await expect.element(page.getByText("You are not a member of a team.")).toBeVisible();
  });

  it("offers the create control when the API says the person can create", async () => {
    render(TeamsPage);

    await expect.element(page.getByRole("button", { name: "Create team" })).toBeVisible();
    expect(page.getByText("The operator of this site limits who can create a team.").elements()).toHaveLength(0);
  });

  it("replaces the create control with the reason when the person cannot create", async () => {
    mocks.teams = { items: [{ id: teamId, name: "Clinic", kind: "manager" }], canCreate: false };
    render(TeamsPage);

    await expect
      .element(page.getByText("The operator of this site limits who can create a team."))
      .toBeVisible();
    expect(page.getByRole("button", { name: "Create team" }).elements()).toHaveLength(0);
    expect(page.getByLabelText("Team name").elements()).toHaveLength(0);
  });

  it("creates a team with the trimmed name and opens it", async () => {
    mocks.createTeam.mockResolvedValue({ id: "22222222-2222-2222-2222-222222222222", name: "Ward" });
    render(TeamsPage);

    const create = page.getByRole("button", { name: "Create team" });
    await expect.element(create).toBeDisabled();
    await page.getByLabelText("Team name").fill("  Ward  ");
    await create.click();

    await vi.waitFor(() => expect(mocks.createTeam).toHaveBeenCalledWith({ name: "Ward" }));
    await vi.waitFor(() =>
      expect(mocks.goto).toHaveBeenCalledWith("/settings/teams/22222222-2222-2222-2222-222222222222"),
    );
  });

  it("tells the person to wait when creating is rate limited, and stays on the page", async () => {
    mocks.createTeam.mockImplementation(async () => error(429, "Too many requests"));
    render(TeamsPage);

    await page.getByLabelText("Team name").fill("Ward");
    await page.getByRole("button", { name: "Create team" }).click();

    await expect.element(page.getByText("Wait a few minutes and try again.", { exact: false })).toBeVisible();
    expect(mocks.goto).not.toHaveBeenCalled();
  });

  it("words a refused name from its code, not from the API text", async () => {
    mocks.createTeam.mockRejectedValue({
      status: 400,
      body: { message: "English detail", code: "team_name_invalid" },
    });
    render(TeamsPage);

    await page.getByLabelText("Team name").fill("Ward");
    await page.getByRole("button", { name: "Create team" }).click();

    await expect
      .element(page.getByText("The team name is empty or too long. Change the name and try again."))
      .toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
  });
});
