import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { page as pageState } from "$app/state";
import { remoteCommand } from "#lib/test-stubs/remote-resource.js";
import { formatMediumDate } from "#lib/utils/formatting.js";
import type { TeamAccessLinkDetails, TeamAccessSiteOption } from "@nocturne/api-client";

const mocks = vi.hoisted(() => ({
  getAccessLinkInfo: vi.fn(),
  approveAccessLink: vi.fn(),
  getMyTenants: vi.fn(),
  goToTenant: vi.fn(),
}));

const live = $state({ info: undefined as TeamAccessLinkDetails | undefined, error: undefined as unknown });

vi.mock("#lib/api/generated/teams.generated.remote.js", () => ({
  getAccessLinkInfo: (token: string) => {
    mocks.getAccessLinkInfo(token);
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
  approveAccessLink: (arg: unknown) => remoteCommand(mocks.approveAccessLink)(arg),
}));

vi.mock("#lib/api/generated/myTenants.generated.remote.js", () => ({
  getMyTenants: (arg: unknown) => mocks.getMyTenants(arg),
}));

vi.mock("#lib/utils/tenant-host.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("#lib/utils/tenant-host.js")>()),
  goToTenant: (...args: unknown[]) => mocks.goToTenant(...args),
}));

import AccessPage from "./+page.svelte";

const teamId = "11111111-1111-1111-1111-111111111111";
const samSite = "22222222-2222-2222-2222-222222222222";
const joSite = "33333333-3333-3333-3333-333333333333";
const viewerRole = "44444444-4444-4444-4444-444444444444";
const caretakerRole = "55555555-5555-5555-5555-555555555555";

function site(overrides: Partial<TeamAccessSiteOption> = {}): TeamAccessSiteOption {
  return {
    tenantId: samSite,
    displayName: "Sam's site",
    alreadyGiven: false,
    roles: [
      { id: caretakerRole, name: "Caretaker", slug: "caretaker", permissions: ["glucose.read", "treatments.readwrite"] },
      { id: viewerRole, name: "Viewer", slug: "viewer", permissions: ["glucose.read"] },
    ],
    defaultRoleId: viewerRole,
    limitForced: false,
    ...overrides,
  };
}

function details(sites: TeamAccessSiteOption[]): TeamAccessLinkDetails {
  return {
    teamId,
    teamName: "Clinic",
    createdByName: "Alex",
    suggestedRoleSlug: "viewer",
    suggestedRolePermissions: ["glucose.read"],
    suggestedLimitTo24Hours: false,
    expiresAt: "2099-01-01T00:00:00Z",
    sites,
  };
}

function openLink(search: string) {
  pageState.url = new URL(`https://example.org/teams/access${search}`);
}

function refused(status: number, code?: string) {
  return { status, body: { message: "English detail", ...(code ? { code } : {}) } };
}

const approveButton = () => page.getByRole("button", { name: "Give the team access" });

describe("team access page", () => {
  beforeEach(() => {
    openLink("?token=abc");
    pageState.data = { baseDomain: "nocturne.test" };
    live.info = details([site()]);
    live.error = undefined;
    mocks.getAccessLinkInfo.mockReset();
    mocks.approveAccessLink.mockReset();
    mocks.getMyTenants.mockReset();
    mocks.goToTenant.mockReset();
    mocks.getMyTenants.mockResolvedValue([{ id: samSite, slug: "sam" }]);
  });

  it("names the team and the manager who made the link, and says Nocturne does not check who runs a team", async () => {
    render(AccessPage);

    await expect.element(page.getByText("Alex", { exact: true }).first()).toBeVisible();
    await expect.element(page.getByText("Clinic", { exact: true })).toBeVisible();
    await expect
      .element(page.getByText("Nocturne does not check who runs a team. Give access only if you know Alex."))
      .toBeVisible();
    expect(mocks.getAccessLinkInfo).toHaveBeenCalledWith("abc");
  });

  it("tells a person who owns no site that only an owner can give access, and offers no control", async () => {
    live.info = details([]);
    render(AccessPage);

    await expect
      .element(page.getByText("Only an owner of a site can give a team access. You do not own a site."))
      .toBeVisible();
    expect(approveButton().elements()).toHaveLength(0);
    expect(page.getByRole("radio").elements()).toHaveLength(0);
    expect(page.getByRole("checkbox").elements()).toHaveLength(0);
  });

  it("opens the one site the person can use, with its roles and the API's default chosen", async () => {
    render(AccessPage);

    await expect.element(page.getByRole("checkbox", { name: "Viewer" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).not.toBeChecked();
    await expect.element(page.getByText("Glucose", { exact: false }).first()).toBeVisible();
    await expect.element(approveButton()).toBeEnabled();
  });

  it("gives the team access with the defaults and goes to the site's member list on its own host", async () => {
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    await approveButton().click();

    await vi.waitFor(() =>
      expect(mocks.approveAccessLink).toHaveBeenCalledWith({
        token: "abc",
        request: { tenantId: samSite, roleIds: [viewerRole], limitTo24Hours: false, accessEndsAt: undefined },
      }),
    );
    await vi.waitFor(() =>
      expect(mocks.goToTenant).toHaveBeenCalledWith("sam", "nocturne.test", "settings/members"),
    );
  });

  it("sends the roles the person ticked, and no role they cleared", async () => {
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await page.getByRole("checkbox", { name: "Viewer" }).click();
    await approveButton().click();

    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());
    expect(mocks.approveAccessLink.mock.calls[0]![0].request.roleIds).toEqual([caretakerRole]);
  });

  it("disables the approve control, with the reason, when no role is chosen", async () => {
    render(AccessPage);

    await page.getByRole("checkbox", { name: "Viewer" }).click();

    await expect.element(approveButton()).toBeDisabled();
    await expect.element(page.getByText("Choose at least one role.")).toBeVisible();
  });

  it("offers no default role and no control for a site whose roles the link does not allow", async () => {
    live.info = details([site({ roles: [], defaultRoleId: undefined })]);
    render(AccessPage);

    await expect
      .element(
        page.getByText("No role of this site fits this link and your own access, so the team cannot get access here."),
      )
      .toBeVisible();
    expect(approveButton().elements()).toHaveLength(0);
  });

  it("chooses no default role when the API names none", async () => {
    live.info = details([site({ defaultRoleId: undefined })]);
    render(AccessPage);

    await expect.element(page.getByRole("checkbox", { name: "Viewer" })).not.toBeChecked();
    await expect.element(approveButton()).toBeDisabled();
  });

  it("asks which site first when the person owns several, and shows no form until one is chosen", async () => {
    live.info = details([site(), site({ tenantId: joSite, displayName: "Jo's site" })]);
    render(AccessPage);

    await expect.element(page.getByRole("radio", { name: "Sam's site" })).toBeVisible();
    expect(approveButton().elements()).toHaveLength(0);
    expect(page.getByRole("checkbox").elements()).toHaveLength(0);

    await page.getByRole("radio", { name: "Jo's site" }).click();

    await expect.element(approveButton()).toBeEnabled();
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: joSite });
    await approveButton().click();
    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());
    expect(mocks.approveAccessLink.mock.calls[0]![0].request.tenantId).toBe(joSite);
  });

  it("says a site the team already has access to has it, with no control for that site", async () => {
    live.info = details([site({ alreadyGiven: true }), site({ tenantId: joSite, displayName: "Jo's site" })]);
    render(AccessPage);

    await expect.element(page.getByText("This team already has access to this site.")).toBeVisible();
    expect(page.getByRole("radio", { name: "Sam's site" }).elements()).toHaveLength(0);
    await expect.element(page.getByRole("radio", { name: "Jo's site" })).toBeVisible();
  });

  it("offers no approve control when the team has access to every site the person owns", async () => {
    live.info = details([site({ alreadyGiven: true })]);
    render(AccessPage);

    await expect.element(page.getByText("This team already has access to this site.")).toBeVisible();
    expect(approveButton().elements()).toHaveLength(0);
    expect(page.getByRole("radio").elements()).toHaveLength(0);
  });

  it("shows a forced 24-hour limit as on and disabled, with the reason, and sends it", async () => {
    live.info = details([site({ limitForced: true })]);
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    const limit = page.getByRole("checkbox", { name: "Only the last 24 hours" });
    await expect.element(limit).toBeChecked();
    await expect.element(limit).toBeDisabled();
    await expect
      .element(page.getByText("The link or your own access requires this limit."))
      .toBeVisible();

    await approveButton().click();
    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());
    expect(mocks.approveAccessLink.mock.calls[0]![0].request.limitTo24Hours).toBe(true);
  });

  it("lets the person add the 24-hour limit when it is not forced", async () => {
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    const limit = page.getByRole("checkbox", { name: "Only the last 24 hours" });
    await expect.element(limit).not.toBeChecked();
    await expect.element(limit).toBeEnabled();
    await limit.click();
    await approveButton().click();

    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());
    expect(mocks.approveAccessLink.mock.calls[0]![0].request.limitTo24Hours).toBe(true);
  });

  it("requires an end date no later than the API's latest end, and shows why", async () => {
    live.info = details([site({ latestEnd: "2099-06-15T12:00:00Z" })]);
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    const end = page.getByLabelText("Access ends", { exact: true });
    await expect.element(end).toBeVisible();
    await expect.element(end).toHaveAttribute("max", expect.stringMatching(/^2099-06-1[45]$/));
    const max = end.element().getAttribute("max")!;
    const [year, month, day] = max.split("-").map(Number);
    const note = page.getByText("The link or your own access sets this limit.", { exact: false });
    await expect.element(note).toBeVisible();
    await expect.element(note).toHaveTextContent(`on ${formatMediumDate(new Date(year!, month! - 1, day))} or before`);
    await expect.element(approveButton()).toBeDisabled();

    await end.fill("2099-06-01");
    await expect.element(approveButton()).toBeEnabled();
    await approveButton().click();

    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());
    expect(mocks.approveAccessLink.mock.calls[0]![0].request.accessEndsAt).toMatch(/^2099-06-0[12]T/);
  });

  it("disables approval, with the reason, when the latest end leaves no day to choose", async () => {
    live.info = details([site({ latestEnd: "2000-01-01T12:00:00Z" })]);
    render(AccessPage);

    await expect
      .element(page.getByText("No end date is left that this link and your own access allow."))
      .toBeVisible();
    await expect.element(page.getByLabelText("Access ends", { exact: true })).toBeDisabled();
    await expect.element(approveButton()).toBeDisabled();
  });

  it("leaves the end optional when the API gives no latest end", async () => {
    render(AccessPage);

    await expect.element(page.getByLabelText("Access ends (optional)")).toBeVisible();
    await expect.element(approveButton()).toBeEnabled();
  });

  it("covers the whole approval, shows no refusal from the refreshed lookup, then goes to the site", async () => {
    let finish: () => void = () => {};
    mocks.approveAccessLink.mockImplementation(async () => {
      live.error = refused(400, "team_invite_used_up");
      await new Promise<void>((resolve) => (finish = resolve));
      return { accessId: "a", tenantId: samSite };
    });
    render(AccessPage);

    await approveButton().click();
    await vi.waitFor(() => expect(mocks.approveAccessLink).toHaveBeenCalled());

    await expect.element(page.getByText("Giving access...")).toBeVisible();
    expect(page.getByText("Link unavailable").elements()).toHaveLength(0);
    expect(page.getByText("no uses left", { exact: false }).elements()).toHaveLength(0);
    expect(approveButton().elements()).toHaveLength(0);
    expect(mocks.goToTenant).not.toHaveBeenCalled();

    finish();
    await vi.waitFor(() => expect(mocks.goToTenant).toHaveBeenCalled());
    await expect.element(page.getByText("Giving access...")).toBeVisible();
  });

  it("shows a done state naming the team and the site when the site's host cannot be reached", async () => {
    pageState.data = {};
    mocks.approveAccessLink.mockImplementation(async () => {
      live.error = refused(400, "team_invite_used_up");
      return { accessId: "a", tenantId: samSite };
    });
    render(AccessPage);

    await approveButton().click();

    await expect.element(page.getByText("The team has access")).toBeVisible();
    await expect.element(page.getByText("Clinic", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Sam's site", { exact: true })).toBeVisible();
    expect(mocks.goToTenant).not.toHaveBeenCalled();
    expect(page.getByText("Link unavailable").elements()).toHaveLength(0);
  });

  it("shows the done state when the site is not among the person's reachable sites", async () => {
    mocks.getMyTenants.mockResolvedValue([{ id: joSite, slug: "jo" }]);
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    await approveButton().click();

    await expect.element(page.getByText("The team has access")).toBeVisible();
    expect(mocks.goToTenant).not.toHaveBeenCalled();
  });

  it("shows the done state when the list of sites cannot be read", async () => {
    mocks.getMyTenants.mockRejectedValue(new Error("down"));
    mocks.approveAccessLink.mockResolvedValue({ accessId: "a", tenantId: samSite });
    render(AccessPage);

    await approveButton().click();

    await expect.element(page.getByText("The team has access")).toBeVisible();
  });

  it.each([
    ["team_invite_revoked", "A manager revoked this link. Ask a manager of the team for a new link."],
    ["team_invite_used_up", "This link has no uses left. Ask a manager of the team for a new link."],
    [
      "team_invite_no_longer_valid",
      "The manager who made this link left the team. Ask a manager of the team for a new link.",
    ],
    ["team_access_already_given", "This team already has access to this site."],
    ["team_access_not_owner", "Only an owner of the site can give a team access to it."],
    ["team_access_roles_required", "Choose at least one role."],
    [
      "team_access_role_not_in_site",
      "A role that you chose is not a role of this site. Reload the page and try again.",
    ],
    ["team_access_role_administrative", "A team can have only roles for data. Choose other roles."],
    [
      "team_access_role_wider_than_link",
      "A role that you chose gives more than this link allows. Choose other roles.",
    ],
    [
      "team_access_role_wider_than_approver",
      "A role that you chose gives more than you hold. Choose other roles.",
    ],
    ["team_access_limit_required", "This access must be limited to the last 24 hours."],
    [
      "team_access_end_later_than_link",
      "The access must end no later than this link allows. Choose an earlier end date.",
    ],
    [
      "team_access_end_outlasts_approver",
      "The access cannot last longer than your own access. Choose an earlier end date.",
    ],
    ["team_access_end_invalid", "The end date must be in the future. Choose a later date."],
    [
      "team_access_link_end_passed",
      "The latest end date that this link allows has passed. Ask a manager of the team for a new link.",
    ],
    ["team_invite_expired", "This link has expired. Ask a manager of the team for a new link."],
  ])("words a refused approval with the code %s from the code alone", async (code, sentence) => {
    mocks.approveAccessLink.mockRejectedValue(refused(code.includes("not_owner") ? 403 : 400, code));
    render(AccessPage);

    await approveButton().click();

    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(mocks.goToTenant).not.toHaveBeenCalled();
    expect(page.getByText("The team has access").elements()).toHaveLength(0);
  });

  it("says the link is not valid when the approval finds it gone", async () => {
    mocks.approveAccessLink.mockRejectedValue(refused(404));
    render(AccessPage);

    await approveButton().click();

    await expect
      .element(page.getByText("This link is not valid. Ask the manager who sent it for a new link."))
      .toBeVisible();
    expect(page.getByText("Try again.", { exact: false }).elements()).toHaveLength(0);
  });

  it("tells the person to wait when the approval is rate limited", async () => {
    mocks.approveAccessLink.mockRejectedValue(refused(429));
    render(AccessPage);

    await approveButton().click();

    await expect.element(page.getByText("Wait a few minutes and try again.", { exact: false })).toBeVisible();
  });

  it("does not tell a person with no site that the site is theirs", async () => {
    live.info = details([]);
    render(AccessPage);

    await expect.element(page.getByText("You do not own a site.", { exact: false })).toBeVisible();
    expect(page.getByText("that you own", { exact: false }).elements()).toHaveLength(0);
  });

  it("shows every permission of a role, the label where there is one and the raw string where there is none", async () => {
    live.info = details([
      site({
        roles: [
          {
            id: viewerRole,
            name: "Viewer",
            slug: "viewer",
            permissions: ["glucose.read", "device.notify", "connectors.readwrite", "made.up.permission"],
          },
        ],
      }),
    ]);
    render(AccessPage);

    await expect.element(page.getByText("View glucose readings")).toBeVisible();
    await expect.element(page.getByText("Send notifications to this device", { exact: false })).toBeVisible();
    await expect.element(page.getByText("Connect, change and remove data sources", { exact: false })).toBeVisible();
    await expect.element(page.getByText("made.up.permission")).toBeVisible();
  });

  it("keeps what the person chose after a refused approval", async () => {
    mocks.approveAccessLink.mockRejectedValue(refused(400, "team_access_role_wider_than_approver"));
    render(AccessPage);

    await page.getByRole("checkbox", { name: "Caretaker" }).click();
    await approveButton().click();
    await expect
      .element(page.getByText("A role that you chose gives more than you hold. Choose other roles."))
      .toBeVisible();

    await expect.element(page.getByRole("checkbox", { name: "Caretaker" })).toBeChecked();
    await expect.element(approveButton()).toBeEnabled();
  });

  it.each([
    ["has expired", "team_invite_expired", "This link has expired. Ask a manager of the team for a new link."],
    ["was revoked", "team_invite_revoked", "A manager revoked this link. Ask a manager of the team for a new link."],
    ["is used up", "team_invite_used_up", "This link has no uses left. Ask a manager of the team for a new link."],
    [
      "has a maker who left",
      "team_invite_no_longer_valid",
      "The manager who made this link left the team. Ask a manager of the team for a new link.",
    ],
    [
      "allows an end that has passed",
      "team_access_link_end_passed",
      "The latest end date that this link allows has passed. Ask a manager of the team for a new link.",
    ],
  ])("words a link that %s from its code, with no control", async (_case, code, sentence) => {
    live.error = refused(400, code);
    render(AccessPage);

    await expect.element(page.getByText("Link unavailable")).toBeVisible();
    await expect.element(page.getByText(sentence)).toBeVisible();
    expect(page.getByText("English detail").elements()).toHaveLength(0);
    expect(approveButton().elements()).toHaveLength(0);
    expect(page.getByRole("radio").elements()).toHaveLength(0);
  });

  it("says a link the API does not know is not valid", async () => {
    live.error = refused(404);
    render(AccessPage);

    await expect.element(page.getByText("This link is not valid.", { exact: false })).toBeVisible();
    expect(approveButton().elements()).toHaveLength(0);
  });

  it("does not call a refused sign-in a bad link", async () => {
    live.error = refused(403);
    render(AccessPage);

    await expect.element(page.getByText("Your sign-in cannot give a team access.")).toBeVisible();
    expect(page.getByText("This link is not valid.", { exact: false }).elements()).toHaveLength(0);
  });

  it("asks for a new sign-in when the session came from a sign-in link", async () => {
    live.error = refused(403, "sign_in_required");
    render(AccessPage);

    await expect.element(page.getByText("Sign in again to do this.", { exact: false })).toBeVisible();
    expect(page.getByText("Your sign-in cannot give a team access.").elements()).toHaveLength(0);
    expect(approveButton().elements()).toHaveLength(0);
  });

  it("tells the person to wait when the lookup is rate limited", async () => {
    live.error = refused(429);
    render(AccessPage);

    await expect.element(page.getByText("Wait a few minutes and try again.", { exact: false })).toBeVisible();
  });

  it("shows one sentence, and asks the API for nothing, for the demo visitor", async () => {
    pageState.data = { refusedAsDemoSubject: true };
    render(AccessPage);

    await expect.element(page.getByText("Teams are not available in the demo.")).toBeVisible();
    expect(mocks.getAccessLinkInfo).not.toHaveBeenCalled();
    expect(approveButton().elements()).toHaveLength(0);
  });

  it("says so, and asks for nothing, when the link has no token", async () => {
    openLink("");
    render(AccessPage);

    await expect.element(page.getByText("This link has no access token.", { exact: false })).toBeVisible();
    expect(mocks.getAccessLinkInfo).not.toHaveBeenCalled();
  });
});
