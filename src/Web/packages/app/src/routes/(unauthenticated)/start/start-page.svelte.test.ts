import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { error } from "@sveltejs/kit";
import { page } from "$app/state";
import { remoteCommand, remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";

const mocks = vi.hoisted(() => ({
  info: undefined as unknown,
  infoError: undefined as unknown,
  slugValid: true,
  redeem: vi.fn(),
  tenantInviteOptions: vi.fn(),
  tenantInviteComplete: vi.fn(),
  setAuthCookies: vi.fn(),
  startRegistration: vi.fn(),
}));

vi.mock("water-ui", async (importOriginal) => ({
  ...(await importOriginal<typeof import("water-ui")>()),
  Artwork: (await import("#lib/test-stubs/Artwork.test-stub.svelte")).default,
  ConfirmationBackground: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
  hostSurface: () => "light",
  watchSurface: () => () => {},
  prefersReducedMotion: () => false,
}));

vi.mock("@simplewebauthn/browser", () => ({ startRegistration: mocks.startRegistration }));

vi.mock("#lib/api/generated/tenantCreationInvites.generated.remote.js", () => ({
  getInfo: () => (mocks.infoError ? remoteQueryError(mocks.infoError) : remoteQuery(() => mocks.info)),
  validateSlug: () =>
    remoteQuery(() =>
      mocks.slugValid ? { isValid: true } : { isValid: false, message: "This name is reserved" },
    ),
  redeem: (arg: unknown) => remoteCommand(mocks.redeem)(arg),
}));

vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  tenantInviteOptions: (arg: unknown) => remoteCommand(mocks.tenantInviteOptions)(arg),
  tenantInviteComplete: (arg: unknown) => remoteCommand(mocks.tenantInviteComplete)(arg),
}));

vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
  getOidcProviders: () => remoteQuery(() => ({ enabled: false, providers: [] })),
  setAuthCookies: (arg: unknown) => remoteCommand(mocks.setAuthCookies)(arg),
}));

import StartPage from "./+page@.svelte";

function invite(signedIn = false) {
  return {
    invite: { baseDomain: "example.org", invitedByName: "Rhys", expiresAt: new Date("2026-12-01T00:00:00Z") },
    signedIn,
    viewerName: signedIn ? "Sam Taylor" : undefined,
  };
}

async function chooseAddress(slug = "sam-taylor", name = "Sam's glucose") {
  await browserPage.getByRole("button", { name: "Start" }).click();
  await browserPage.getByRole("textbox", { name: "", exact: false }).first().fill(slug);
  await browserPage.getByLabelText("Site name").fill(name);
  await expect.element(browserPage.getByText("This address is available.")).toBeVisible();
}

describe("invite start page", () => {
  beforeEach(() => {
    page.url = new URL("https://example.org/start?token=abc");
    mocks.info = invite();
    mocks.infoError = undefined;
    mocks.slugValid = true;
    for (const fn of [mocks.redeem, mocks.tenantInviteOptions, mocks.tenantInviteComplete, mocks.setAuthCookies, mocks.startRegistration]) {
      fn.mockReset();
    }
  });

  it("says the link is unusable when it carries no code", async () => {
    page.url = new URL("https://example.org/start");
    render(StartPage);

    await expect.element(browserPage.getByText("You cannot use this link")).toBeVisible();
    await expect
      .element(browserPage.getByText("The link has no invite code. Check the link that you got."))
      .toBeVisible();
  });

  it("shows the server's reason for an unusable link", async () => {
    try {
      error(400, "This invite link has expired.");
    } catch (refusal) {
      mocks.infoError = refusal;
    }
    render(StartPage);

    await expect.element(browserPage.getByText("This invite link has expired.")).toBeVisible();
  });

  it("welcomes the invitee, names the inviter, and discloses administrator access", async () => {
    render(StartPage);

    await expect.element(browserPage.getByText("Rhys invited you")).toBeVisible();
    await expect.element(browserPage.getByRole("heading", { level: 1 })).toHaveTextContent("Make your own Nocturne site.");
    await expect
      .element(browserPage.getByText("The administrators of this server can open your site for support.", { exact: false }))
      .toBeVisible();
  });

  it("does not move on from the address step until the address is available and the site has a name", async () => {
    mocks.slugValid = false;
    render(StartPage);

    await browserPage.getByRole("button", { name: "Start" }).click();
    await browserPage.getByRole("textbox").first().fill("admin");
    await browserPage.getByLabelText("Site name").fill("Sam's glucose");

    await expect.element(browserPage.getByText("This name is reserved")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Next" })).toBeDisabled();
  });

  it("shows the address in each place it is used while it is typed", async () => {
    render(StartPage);

    await chooseAddress();

    await expect.element(browserPage.getByText("Where you use this address")).toBeVisible();
    await expect.element(browserPage.getByText("sam-taylor", { exact: true }).first()).toBeVisible();
  });

  it("makes the site for a signed-in visitor and ends on its address", async () => {
    mocks.info = invite(true);
    mocks.redeem.mockResolvedValue({ slug: "sam-taylor", displayName: "Sam's glucose", address: "https://sam-taylor.example.org" });
    render(StartPage);

    await chooseAddress();
    await browserPage.getByRole("button", { name: "Next" }).click();
    await expect.element(browserPage.getByText("You are signed in as", { exact: false })).toBeVisible();
    await browserPage.getByRole("button", { name: "Make my site" }).click();

    await vi.waitFor(() =>
      expect(mocks.redeem).toHaveBeenCalledWith({ token: "abc", request: { slug: "sam-taylor", siteName: "Sam's glucose" } }),
    );
    await expect.element(browserPage.getByText("Your site is ready.", { exact: false })).toBeVisible();
    await expect.element(browserPage.getByText("https://sam-taylor.example.org", { exact: true })).toBeVisible();
    await expect.element(browserPage.getByRole("img", { name: "QR code of your site address" })).toBeVisible();
  });

  it("makes the account with a passkey, then shows the recovery codes, then the address", async () => {
    mocks.tenantInviteOptions.mockResolvedValue({ options: "{}", challengeToken: "challenge" });
    mocks.startRegistration.mockResolvedValue({ id: "credential" });
    mocks.tenantInviteComplete.mockResolvedValue({
      success: true,
      recoveryCodes: ["AAAA-BBBB", "CCCC-DDDD"],
      accessToken: "access",
      tenant: { slug: "sam-taylor", displayName: "Sam's glucose", address: "https://sam-taylor.example.org" },
    });
    render(StartPage);

    await chooseAddress();
    await browserPage.getByRole("button", { name: "Next" }).click();
    await browserPage.getByLabelText("Display name").fill("Sam Taylor");
    await browserPage.getByLabelText("Username").fill("sam");
    await browserPage.getByRole("button", { name: "Create account with passkey" }).click();

    await expect.element(browserPage.getByText("AAAA-BBBB")).toBeVisible();
    expect(mocks.tenantInviteOptions).toHaveBeenCalledWith(
      expect.objectContaining({ token: "abc", slug: "sam-taylor", siteName: "Sam's glucose", username: "sam", displayName: "Sam Taylor" }),
    );
    expect(mocks.tenantInviteComplete).toHaveBeenCalledWith(
      expect.objectContaining({ challengeToken: "challenge", attestationResponseJson: JSON.stringify({ id: "credential" }) }),
    );
    expect(mocks.setAuthCookies).toHaveBeenCalled();

    await browserPage.getByText("I wrote down my recovery codes").click();
    await browserPage.getByRole("button", { name: "Next" }).click();
    await expect.element(browserPage.getByText("https://sam-taylor.example.org", { exact: true })).toBeVisible();
  });

  it("keeps the new account signed in and returns to the address when the site could not be made", async () => {
    mocks.tenantInviteOptions.mockResolvedValue({ options: "{}", challengeToken: "challenge" });
    mocks.startRegistration.mockResolvedValue({ id: "credential" });
    mocks.tenantInviteComplete.mockResolvedValue({
      success: true,
      recoveryCodes: ["AAAA-BBBB"],
      accessToken: "access",
      tenantRefusal: "This name is already taken",
    });
    render(StartPage);

    await chooseAddress();
    await browserPage.getByRole("button", { name: "Next" }).click();
    await browserPage.getByLabelText("Display name").fill("Sam Taylor");
    await browserPage.getByLabelText("Username").fill("sam");
    await browserPage.getByRole("button", { name: "Create account with passkey" }).click();

    await expect.element(browserPage.getByText("We made your account, but not your site.", { exact: false })).toBeVisible();
    await browserPage.getByText("I wrote down my recovery codes").click();
    await browserPage.getByRole("button", { name: "Next" }).click();

    await expect.element(browserPage.getByText("Select your address.", { exact: false })).toBeVisible();
  });

  it("reports a passkey ceremony that failed", async () => {
    mocks.tenantInviteOptions.mockImplementation(async () => error(400, "This username is reserved"));
    render(StartPage);

    await chooseAddress();
    await browserPage.getByRole("button", { name: "Next" }).click();
    await browserPage.getByLabelText("Display name").fill("Sam Taylor");
    await browserPage.getByLabelText("Username").fill("admin");
    await browserPage.getByRole("button", { name: "Create account with passkey" }).click();

    await expect.element(browserPage.getByRole("alert").first()).toBeVisible();
    expect(mocks.tenantInviteComplete).not.toHaveBeenCalled();
  });
});
