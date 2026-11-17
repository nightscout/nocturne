import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
  hubState: "Open" as string | undefined,
  handover: undefined as { callerIsHelper: boolean } | undefined,
  roles: [] as { id: string; slug: string; name: string; accessLevel?: string; canGrant: boolean }[],
}));

const SEEDED_ROLES = [
  { slug: "owner", accessLevel: "Manage" },
  { slug: "admin", accessLevel: "Manage" },
  { slug: "caretaker", accessLevel: "ReadAndLogTreatments" },
  { slug: "viewer", accessLevel: "ReadOnly" },
  { slug: "clinician", accessLevel: "ReadOnly" },
].map((r) => ({ ...r, id: `role-${r.slug}`, name: r.slug, canGrant: true }));

let share = $state.raw<Record<string, unknown>>({});
const SHAREABLE = ["glucose.read", "treatments.read"];

const commands = vi.hoisted(() => ({
  setState: vi.fn(),
  createInvite: vi.fn(),
  createGuestLink: vi.fn(),
  rotateShareLink: vi.fn(),
  goto: vi.fn(),
  refreshHub: vi.fn(),
}));
const refreshHub = commands.refreshHub;

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  goto: commands.goto,
}));
vi.mock("$app/state", () => ({ page: { data: { effectivePermissions: ["*"] } } }));
vi.mock("#lib/api/generated/setupHubs.generated.remote.js", () => ({
  getSetupHub: () =>
    Object.assign(
      remoteQuery(() =>
        state.hubState === undefined
          ? undefined
          : {
              items: [{ key: "Sharing", state: state.hubState }],
              resolvedCount: 0,
              openCount: 1,
              totalCount: 1,
              revision: "r",
              showStrip: false,
              handover: state.handover,
            },
      ),
      { refresh: commands.refreshHub },
    ),
  setSetupHubItemState: commands.setState,
}));
vi.mock("#lib/api/generated/tenantSettings.generated.remote.js", () => ({
  getPatientRelationship: () => remoteQuery(() => state.relationship),
}));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => remoteQuery(() => state.roles),
}));
vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({
  createInvite: commands.createInvite,
}));
vi.mock("#lib/api/generated/guestLinks.generated.remote.js", () => ({
  getGuestLinks: () => remoteQuery(() => []),
  getGuestLinkScopeOptions: () =>
    remoteQuery(() => ({ available: ["glucose.read"], defaults: ["glucose.read"] })),
  createGuestLink: remoteCommand((request) => commands.createGuestLink(request)),
  revokeGuestLink: vi.fn(),
  dismissGuestLink: vi.fn(),
}));
vi.mock("#lib/api/generated/shareLinks.generated.remote.js", () => ({
  getShareLink: () => remoteQuery(() => share),
  rotateShareLink: commands.rotateShareLink,
  revealShareLink: vi.fn(),
  disableShareLink: vi.fn(),
  setShareLinkFullHistory: vi.fn(),
  setShareLinkScopes: vi.fn(),
}));

import SharingPage from "./+page.svelte";

const card = (id: string) => page.getByTestId(`sharing-audience-${id}`);
const flow = (id: string) => page.getByTestId(`sharing-flow-${id}`);

beforeEach(() => {
  vi.clearAllMocks();
  state.relationship = { relationship: "Caregiver", patientName: "Sam" };
  state.hubState = "Open";
  state.handover = undefined;
  state.roles = SEEDED_ROLES;
  share = { enabled: false, scopes: [], shareableScopes: SHAREABLE, fullHistory: false };
});

describe("sharing guided page", () => {
  it("asks who else should see the patient's data, with a card per kind of person", async () => {
    render(SharingPage);

    await expect.element(page.getByRole("heading", { name: "Who else can see Sam's data?" })).toBeVisible();
    for (const id of ["family", "temporary", "public", "just-me"])
      await expect.element(card(id)).toHaveAttribute("aria-pressed", "false");
    expect(document.querySelector('[data-testid^="sharing-flow-"]')).toBeNull();
  });

  it("opens the member invite with the roles in plain words, and refreshes the hub once someone is invited", async () => {
    commands.createInvite.mockResolvedValue({ inviteUrl: "https://example.test/invite/x" });
    render(SharingPage);

    await card("family").click();
    const invite = flow("family");
    await expect.element(invite.getByTestId("create-invite-card")).toBeVisible();
    await expect.element(invite.getByText("Can see everything")).toBeVisible();
    await expect.element(invite.getByText("Can see and log treatments")).toBeVisible();
    await expect.element(invite.getByText("Can manage settings")).toBeVisible();
    expect(invite.getByText("Direct Permissions").elements()).toHaveLength(0);

    const create = invite.getByRole("button", { name: "Create Link" });
    await expect.element(create).toBeDisabled();
    await invite.getByText("Can see and log treatments").click();
    await create.click();

    await vi.waitFor(() =>
      expect(commands.createInvite).toHaveBeenCalledWith(
        expect.objectContaining({ roleIds: ["role-caretaker"] }),
      ),
    );
    await vi.waitFor(() => expect(refreshHub).toHaveBeenCalled());
  });

  it("drops an access level whose role was edited past what its words promise", async () => {
    state.roles = SEEDED_ROLES.map((r) =>
      r.slug === "clinician" || r.slug === "caretaker" ? { ...r, accessLevel: "Manage" } : r,
    );
    render(SharingPage);

    await card("family").click();
    const invite = flow("family");
    await expect.element(invite.getByText("Can manage settings")).toBeVisible();
    expect(invite.getByText("Can see everything").elements()).toHaveLength(0);
    expect(invite.getByText("Can see and log treatments").elements()).toHaveLength(0);
  });

  it("falls back to the role picker when no seeded role is left to offer", async () => {
    state.roles = [
      { id: "role-custom", slug: "custom", name: "Custom", accessLevel: "ReadOnly", canGrant: true },
    ];
    render(SharingPage);

    await card("family").click();
    const invite = flow("family");
    await expect.element(invite.getByText("Roles", { exact: true })).toBeVisible();
    await expect.element(invite.getByText("Custom")).toBeVisible();
    expect(invite.getByText("What can they do?").elements()).toHaveLength(0);
  });

  it("opens the guest links for a school, clinic or someone temporary", async () => {
    commands.createGuestLink.mockResolvedValue({ code: "ABC-DEFG", fullUrl: "https://example.test/guest/ABC-DEFG" });
    render(SharingPage);

    await card("temporary").click();
    await expect.element(flow("temporary").getByTestId("guest-links")).toBeVisible();

    await page.getByRole("button", { name: "Create Guest Link" }).click();
    await page.getByLabelText("Who is this for?").fill("School nurse");
    await page.getByRole("button", { name: "Create Link" }).click();

    await vi.waitFor(() => expect(refreshHub).toHaveBeenCalled());
  });

  it("opens the public link switched off, and once on shows what it reveals over the last 24 hours", async () => {
    commands.rotateShareLink.mockImplementation(async () => {
      share = {
        enabled: true,
        scopes: ["glucose.read"],
        shareableScopes: SHAREABLE,
        fullHistory: false,
        redactedUrl: "https://x.share.test",
      };
      return { ...share, url: "https://abc.share.test" };
    });
    render(SharingPage);

    await card("public").click();
    const publicFlow = flow("public");
    await expect.element(publicFlow.getByText(/only the last 24 hours unless you choose all history/)).toBeVisible();
    await expect.element(publicFlow.getByTestId("public-access-toggle")).not.toBeChecked();
    await expect.element(publicFlow.getByText("Public access is off.")).toBeVisible();

    await publicFlow.getByTestId("public-access-toggle").click();

    await expect
      .element(publicFlow.getByText(/Anyone with the link can see/))
      .toBeVisible();
    await expect.element(publicFlow.getByText("the last 24 hours", { exact: true })).toBeVisible();
    await expect.element(publicFlow.getByText(/They cannot see treatments/)).toBeVisible();
    await vi.waitFor(() => expect(refreshHub).toHaveBeenCalled());
  });

  it("clears the other choices for just me, and sets the item aside", async () => {
    commands.setState.mockResolvedValue({});
    commands.goto.mockResolvedValue(undefined);
    render(SharingPage);

    await card("family").click();
    await card("public").click();
    await card("just-me").click();

    await expect.element(card("family")).toHaveAttribute("aria-pressed", "false");
    await expect.element(card("public")).toHaveAttribute("aria-pressed", "false");
    expect(document.querySelectorAll('[data-testid^="sharing-flow-"]')).toHaveLength(1);

    await page.getByTestId("sharing-keep-to-me").click();

    await vi.waitFor(() =>
      expect(commands.setState).toHaveBeenCalledWith({
        key: "Sharing",
        request: { state: "NotForMe" },
      }),
    );
  });

  it("sets the item aside even before the hub has loaded", async () => {
    state.hubState = undefined;
    commands.setState.mockResolvedValue({});
    commands.goto.mockResolvedValue(undefined);
    render(SharingPage);

    await card("just-me").click();
    await page.getByTestId("sharing-keep-to-me").click();

    await vi.waitFor(() =>
      expect(commands.setState).toHaveBeenCalledWith({
        key: "Sharing",
        request: { state: "NotForMe" },
      }),
    );
  });

  it("does not set aside an item that is already done", async () => {
    state.hubState = "Done";
    commands.goto.mockResolvedValue(undefined);
    render(SharingPage);

    await card("just-me").click();
    await page.getByTestId("sharing-keep-to-me").click();

    await vi.waitFor(() => expect(commands.goto).toHaveBeenCalledWith("/setup"));
    expect(commands.setState).not.toHaveBeenCalled();
  });

  describe("for a helper", () => {
    beforeEach(() => {
      state.relationship = { relationship: "Helper", patientName: "Sam" };
      state.handover = { callerIsHelper: true };
    });

    it("hands it over with a single-use owner invite and says what that gives away", async () => {
      render(SharingPage);

      const handover = page.getByTestId("sharing-handover");
      await expect.element(handover.getByRole("heading", { name: "Hand it over" })).toBeVisible();
      await expect
        .element(handover.getByText("They can do everything you can do, including remove you."))
        .toBeVisible();
      await expect.element(handover.getByRole("radio", { name: /Owner/ })).toBeChecked();
      expect(handover.getByRole("radio").elements()).toHaveLength(1);
      expect(handover.getByText("Allow multiple uses").elements()).toHaveLength(0);
      expect(handover.getByText("Only last 24 hours").elements()).toHaveLength(0);
      expect(document.querySelector('[data-testid^="sharing-audience-"]')).toBeNull();
    });

    it("creates the invite, shows the link, and waits for them to accept", async () => {
      commands.createInvite.mockResolvedValue({ inviteUrl: "https://example.test/invite/x" });
      render(SharingPage);

      const handover = page.getByTestId("sharing-handover");
      await handover.getByRole("button", { name: "Create Link" }).click();

      await vi.waitFor(() =>
        expect(commands.createInvite).toHaveBeenCalledWith(
          expect.objectContaining({ roleIds: ["role-owner"], maxUses: 1, limitTo24Hours: false }),
        ),
      );
      await expect.element(handover.getByRole("textbox")).toHaveValue("https://example.test/invite/x");
      await expect.element(page.getByTestId("sharing-handover-waiting")).toHaveTextContent("Waiting for them to accept.");
      await vi.waitFor(() => expect(refreshHub).toHaveBeenCalled());
    });

    it("shows the person it was handed to the usual choices, not another owner invite", async () => {
      state.handover = { callerIsHelper: false };
      render(SharingPage);

      await expect.element(card("family")).toBeVisible();
      for (const id of ["temporary", "public", "just-me"])
        await expect.element(card(id)).toHaveAttribute("aria-pressed", "false");
      expect(page.getByTestId("sharing-handover").elements()).toHaveLength(0);
      expect(page.getByTestId("create-invite-card").elements()).toHaveLength(0);
    });
  });
});
