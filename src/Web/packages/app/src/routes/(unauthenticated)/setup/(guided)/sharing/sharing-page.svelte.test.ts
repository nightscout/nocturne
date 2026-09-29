import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

const state = vi.hoisted(() => ({
  relationship: {} as { relationship?: string; patientName?: string },
  hubState: "Open",
}));

let share = $state.raw<Record<string, unknown>>({});

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
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () =>
    Object.assign(
      remoteQuery(() => ({
        items: [{ key: "Sharing", state: state.hubState }],
        resolvedCount: 0,
        openCount: 1,
        totalCount: 1,
        revision: "r",
        showStrip: false,
      })),
      { refresh: commands.refreshHub },
    ),
  setSetupHubItemState: commands.setState,
}));
vi.mock("$api/generated/tenantSettings.generated.remote", () => ({
  getPatientRelationship: () => remoteQuery(() => state.relationship),
}));
vi.mock("$api/generated/roles.generated.remote", () => ({
  getRoles: () =>
    remoteQuery(() =>
      ["owner", "admin", "caretaker", "viewer", "clinician"].map((slug) => ({
        id: `role-${slug}`,
        slug,
        name: slug,
      })),
    ),
}));
vi.mock("$api/generated/memberInvites.generated.remote", () => ({
  createInvite: commands.createInvite,
}));
vi.mock("$api/generated/guestLinks.generated.remote", () => ({
  getGuestLinks: () => remoteQuery(() => []),
  createGuestLink: commands.createGuestLink,
  revokeGuestLink: vi.fn(),
  dismissGuestLink: vi.fn(),
}));
vi.mock("$api/generated/shareLinks.generated.remote", () => ({
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
  share = { enabled: false, scopes: [], fullHistory: false };
});

describe("sharing guided page", () => {
  it("asks who else should see the patient's data, with a card per kind of person", async () => {
    render(SharingPage);

    await expect.element(page.getByRole("heading", { name: "Who else should see Sam's data?" })).toBeVisible();
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
      share = { enabled: true, scopes: ["glucose.read"], fullHistory: false, redactedUrl: "https://x.share.test" };
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

  it("does not set aside an item that is already done", async () => {
    state.hubState = "Done";
    commands.goto.mockResolvedValue(undefined);
    render(SharingPage);

    await card("just-me").click();
    await page.getByTestId("sharing-keep-to-me").click();

    await vi.waitFor(() => expect(commands.goto).toHaveBeenCalledWith("/setup"));
    expect(commands.setState).not.toHaveBeenCalled();
  });
});
