import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browser_page } from "vitest/browser";
import { page } from "$app/state";

const { confirm_discord_link, goto } = vi.hoisted(() => ({
  confirm_discord_link: vi.fn(),
  goto: vi.fn(),
}));

vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto,
}));

vi.mock("./finalize.remote", () => ({
  getDiscordLinkClaim: () => ({ current: { discordUsername: "linked-account" }, error: undefined }),
  confirmDiscordLink: confirm_discord_link,
}));

import FinalizePage from "./+page.svelte";

describe("finalizing a Discord link", () => {
  beforeEach(() => {
    page.data.isAuthenticated = true;
    page.data.isHandedOffSession = false;
    page.url = new URL("http://localhost/auth/bot/discord/finalize?claim=signed-claim") as typeof page.url;
    confirm_discord_link.mockReset();
  });

  it.each([
    [409, "This Discord account is already linked to another member."],
    [400, "This link is expired or invalid. Try again from Settings."],
    [500, "We could not complete the Discord link. If the link expired, try again from Settings."],
  ])("tells the member why a %i refusal linked nothing", async (status, message) => {
    confirm_discord_link.mockRejectedValue({ status, body: { message: "server wording" } });
    render(FinalizePage);

    await browser_page.getByRole("button", { name: "Link account" }).click();

    await expect.element(browser_page.getByText(message)).toBeVisible();
    await expect.element(browser_page.getByText("Discord account linked")).not.toBeInTheDocument();
  });

  it("offers a handed-off session a sign-in in place of Link account", async () => {
    page.data.isHandedOffSession = true;
    render(FinalizePage);

    await expect.element(browser_page.getByText("Sign in again to link your Discord account.")).toBeVisible();
    expect(browser_page.getByRole("button", { name: "Link account" }).elements()).toHaveLength(0);
    expect(confirm_discord_link).not.toHaveBeenCalled();
  });

  it("sends a visitor with no session to sign in, returning to the claim", async () => {
    page.data.isAuthenticated = false;
    goto.mockClear();
    render(FinalizePage);

    await vi.waitFor(() =>
      expect(goto).toHaveBeenCalledWith(
        `/auth/login?returnUrl=${encodeURIComponent("/auth/bot/discord/finalize?claim=signed-claim")}`,
        { replace: true }
      )
    );
  });
});
