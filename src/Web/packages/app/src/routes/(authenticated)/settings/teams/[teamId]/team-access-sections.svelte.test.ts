import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import type { TeamDetail } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  team: undefined as unknown as TeamDetail,
  createAccessLink: vi.fn(),
  revokeAccessLink: vi.fn(),
  getTeam: vi.fn(),
}));

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getTeam: (id: string) => {
    mocks.getTeam(id);
    return { current: mocks.team, error: undefined, loading: false, ready: true };
  },
  createAccessLink: (arg: unknown) => remoteCommand(mocks.createAccessLink)(arg),
  revokeAccessLink: (arg: unknown) => remoteCommand(mocks.revokeAccessLink)(arg),
  renameTeam: vi.fn(),
  removeManager: vi.fn(),
  createManagerInvite: vi.fn(),
  revokeManagerInvite: vi.fn(),
  deleteTeam: vi.fn(),
  createStaffInvite: vi.fn(),
  revokeStaffInvite: vi.fn(),
  updateStaff: vi.fn(),
  removeStaff: vi.fn(),
}));

vi.mock("$app/navigation", () => ({ goto: vi.fn() }));

import TeamPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const me = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const linkId = "dddddddd-dddd-dddd-dddd-dddddddddddd";

const manager: TeamDetail = {
  id: teamId,
  name: "Clinic",
  managers: [{ subjectId: me, displayName: "Alex" }],
  managerNames: ["Alex"],
  invites: [],
  accessLinks: [],
  sites: [],
  suggestableRoles: [
    { slug: "caretaker", name: "Caretaker", permissions: ["glucose.read", "treatments.readwrite"] },
    { slug: "viewer", name: "Viewer", permissions: ["glucose.read"] },
  ],
  isLastManager: true,
  canDelete: false,
  callerKind: "manager",
};

const makeLink = () => page.getByRole("button", { name: "Make access link" });
const accessForm = () => page.getByRole("form", { name: "Make an access link" });

describe("team page access sections", () => {
  beforeEach(() => {
    pageState.params = { teamId };
    pageState.data = { user: { subjectId: me } };
    mocks.team = manager;
    mocks.createAccessLink.mockReset();
    mocks.revokeAccessLink.mockReset();
    mocks.getTeam.mockReset();
  });

  it("offers the roles the API sends, and no role it did not send", async () => {
    render(TeamPage);

    await expect.element(accessForm().getByRole("radio", { name: "Caretaker" })).toBeVisible();
    await expect.element(accessForm().getByRole("radio", { name: "Viewer" })).toBeVisible();
    expect(page.getByRole("radio", { name: "Administrator" }).elements()).toHaveLength(0);
    expect(page.getByRole("radio", { name: "Denied" }).elements()).toHaveLength(0);
  });

  it("states how the link works and gives no way to make it before a role is chosen", async () => {
    render(TeamPage);

    const explanation = page.getByText("A person opens the link", { exact: false });
    await expect.element(explanation).toBeVisible();
    await expect
      .element(explanation)
      .toHaveTextContent("A link stops working when the manager who made it leaves the team.");
    await expect.element(makeLink()).toBeDisabled();
    await expect.element(page.getByText("Choose a role to make the link.")).toBeVisible();
  });

  it("shows what the chosen role lets the team see and change", async () => {
    render(TeamPage);

    await accessForm().getByRole("radio", { name: "Caretaker" }).click();

    await expect.element(page.getByText("What the team can see and change with this role")).toBeVisible();
    await expect.element(makeLink()).toBeEnabled();
  });

  it("shows every permission of the chosen role, the label where there is one and the raw string where there is none", async () => {
    mocks.team = {
      ...manager,
      suggestableRoles: [
        {
          slug: "viewer",
          name: "Viewer",
          permissions: ["glucose.read", "device.notify", "connectors.readwrite", "made.up.permission"],
        },
      ],
    };
    render(TeamPage);

    await accessForm().getByRole("radio", { name: "Viewer" }).click();

    await expect.element(page.getByText("View glucose readings")).toBeVisible();
    await expect.element(page.getByText("Send notifications to this device", { exact: false })).toBeVisible();
    await expect.element(page.getByText("Connect, change and remove data sources", { exact: false })).toBeVisible();
    await expect.element(page.getByText("made.up.permission")).toBeVisible();
  });

  it("reads a suggestion that is no longer offered as a literal, not a raw slug", async () => {
    mocks.team = {
      ...manager,
      accessLinks: [
        {
          id: linkId,
          label: "Old link",
          expiresAt: "2026-12-01T00:00:00Z",
          useCount: 0,
          createdBySubjectId: me,
          createdByName: "Sam",
          suggestion: { roleSlug: "denied", limitTo24Hours: false },
        },
      ],
    };
    render(TeamPage);

    await expect.element(page.getByText("Suggests a role that is no longer offered")).toBeVisible();
    expect(page.getByText("denied", { exact: false }).elements()).toHaveLength(0);
  });

  it("sends the chosen role, limit, end and expiry, and shows the new link once", async () => {
    mocks.createAccessLink.mockResolvedValue({
      id: linkId,
      token: "secret",
      linkUrl: "https://example.org/teams/access?token=secret",
      expiresAt: "2026-12-01T00:00:00Z",
    });
    render(TeamPage);

    expect(page.getByRole("button", { name: "Copy the access link" }).elements()).toHaveLength(0);
    await page.getByLabelText("Label of the access link (optional)").fill("  Night clinic ");
    await accessForm().getByRole("radio", { name: "Viewer" }).click();
    await accessForm().getByLabelText("Only the last 24 hours").click();
    await accessForm().getByLabelText("30 days").click();
    await makeLink().click();

    await vi.waitFor(() => expect(mocks.createAccessLink).toHaveBeenCalled());
    const [{ teamId: sentTeam, request }] = mocks.createAccessLink.mock.calls[0]!;
    expect(sentTeam).toBe(teamId);
    expect(request).toMatchObject({
      label: "Night clinic",
      roleSlug: "viewer",
      limitTo24Hours: true,
      expiresInDays: 30,
    });
    expect(request.accessEndsAt).toBeUndefined();
    await expect
      .element(page.getByLabelText("Access link", { exact: true }))
      .toHaveValue("https://example.org/teams/access?token=secret");
    await expect.element(page.getByRole("button", { name: "Copy the access link" })).toBeVisible();
    await expect.element(page.getByText("We show the link one time only.", { exact: false })).toBeVisible();
  });

  it("sends the end of the access as an instant when a day is chosen", async () => {
    mocks.createAccessLink.mockResolvedValue({ id: linkId, token: "t", linkUrl: "https://example.org/x" });
    render(TeamPage);

    await accessForm().getByRole("radio", { name: "Viewer" }).click();
    await page.getByLabelText("Latest end of the access (optional)").fill("2099-01-31");
    await makeLink().click();

    await vi.waitFor(() => expect(mocks.createAccessLink).toHaveBeenCalled());
    const sent = mocks.createAccessLink.mock.calls[0]![0].request.accessEndsAt as string;
    expect(new Date(sent).getTime()).toBeGreaterThan(Date.parse("2099-01-31T00:00:00Z") - 86_400_000);
    expect(sent).toMatch(/^2099-0[12]-\d{2}T/);
  });

  it("words a refused link from its code and shows no link", async () => {
    mocks.createAccessLink.mockRejectedValue({
      status: 400,
      body: { message: "English detail", code: "team_access_role_invalid" },
    });
    render(TeamPage);

    await accessForm().getByRole("radio", { name: "Viewer" }).click();
    await makeLink().click();

    await expect
      .element(page.getByText("A link cannot suggest this role. Choose another role."))
      .toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(page.getByRole("button", { name: "Copy the access link" }).elements()).toHaveLength(0);
  });

  it("lists each live link with its suggestion, its maker and its expiry, and revokes it", async () => {
    mocks.team = {
      ...manager,
      accessLinks: [
        {
          id: linkId,
          label: "Night clinic",
          expiresAt: "2026-12-01T00:00:00Z",
          useCount: 0,
          createdBySubjectId: me,
          createdByName: "Sam",
          suggestion: { roleSlug: "viewer", limitTo24Hours: true, accessEndsAt: "2027-01-01T00:00:00Z" },
        },
      ],
    };
    mocks.revokeAccessLink.mockResolvedValue({ success: true });
    render(TeamPage);

    await expect.element(page.getByText("Night clinic", { exact: true })).toBeVisible();
    const suggestion = page.getByText("Suggests the role Viewer", { exact: false });
    await expect.element(suggestion).toBeVisible();
    await expect.element(suggestion).toHaveTextContent("Only the last 24 hours");
    await expect.element(suggestion).toHaveTextContent("Access ends by");
    await expect.element(page.getByText("Made by Sam", { exact: false })).toBeVisible();
    expect(page.getByText("No access link works now.").elements()).toHaveLength(0);

    await page.getByRole("button", { name: "Revoke the link Night clinic" }).click();

    await vi.waitFor(() =>
      expect(mocks.revokeAccessLink).toHaveBeenCalledWith({ teamId, linkId }),
    );
  });

  it("says no link works when the API sends none", async () => {
    render(TeamPage);

    await expect.element(page.getByText("No access link works now.")).toBeVisible();
  });

  it("lists the sites that gave the team access, with the roles, the limit, the end and when, and nothing about who approved", async () => {
    mocks.team = {
      ...manager,
      sites: [
        {
          tenantId: "99999999-9999-9999-9999-999999999999",
          displayName: "Sam's site",
          roleNames: ["Caretaker", "Viewer"],
          limitTo24Hours: true,
          accessEndsAt: "2027-01-01T00:00:00Z",
          givenAt: "2026-11-01T00:00:00Z",
        },
        {
          tenantId: "88888888-8888-8888-8888-888888888888",
          displayName: "Jo's site",
          roleNames: ["Viewer"],
          limitTo24Hours: false,
          givenAt: "2026-11-02T00:00:00Z",
        },
      ],
    };
    render(TeamPage);

    await expect.element(page.getByText("Sites this team has access to")).toBeVisible();
    await expect.element(page.getByText("Sam's site")).toBeVisible();
    await expect.element(page.getByText("Roles: Caretaker, Viewer")).toBeVisible();
    await expect.element(page.getByText("Roles: Viewer", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Access does not end", { exact: false })).toBeVisible();
    await expect.element(page.getByText("All history", { exact: false })).toBeVisible();
    expect(page.getByText("Approved", { exact: false }).elements()).toHaveLength(0);
    expect(page.getByText("No site has given this team access.").elements()).toHaveLength(0);
  });

  it("says an access has ended when the API says it has, and not that it ends", async () => {
    mocks.team = {
      ...manager,
      sites: [
        {
          tenantId: "99999999-9999-9999-9999-999999999999",
          displayName: "Sam's site",
          roleNames: ["Viewer"],
          limitTo24Hours: false,
          accessEndsAt: "2020-01-01T00:00:00Z",
          hasEnded: true,
          givenAt: "2019-11-01T00:00:00Z",
        },
      ],
    };
    render(TeamPage);

    await expect.element(page.getByText("Access ended", { exact: false })).toBeVisible();
    expect(page.getByText("Access ends", { exact: false }).elements()).toHaveLength(0);
  });

  it("says literally that no site has given the team access", async () => {
    render(TeamPage);

    await expect.element(page.getByText("No site has given this team access.")).toBeVisible();
  });

  it("shows neither section to a caller who is not a manager", async () => {
    mocks.team = { ...manager, callerKind: "other", accessLinks: [], sites: [], suggestableRoles: [] };
    render(TeamPage);

    await expect.element(page.getByText("Alex", { exact: true })).toBeVisible();
    expect(page.getByText("Request access").elements()).toHaveLength(0);
    expect(page.getByText("Sites this team has access to").elements()).toHaveLength(0);
    expect(makeLink().elements()).toHaveLength(0);
  });
});
