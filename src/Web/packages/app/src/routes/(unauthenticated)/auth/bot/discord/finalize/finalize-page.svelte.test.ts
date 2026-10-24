import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browser_page } from "vitest/browser";
import { page } from "$app/state";

const { confirm_discord_link } = vi.hoisted(() => ({ confirm_discord_link: vi.fn() }));

vi.mock("./finalize.remote", () => ({
  getDiscordLinkClaim: () => ({ current: { discordUsername: "linked-account" }, error: undefined }),
  confirmDiscordLink: confirm_discord_link,
}));

import FinalizePage from "./+page.svelte";

describe("finalizing a Discord link", () => {
  beforeEach(() => {
    page.data.isAuthenticated = true;
    page.url = new URL("http://localhost/auth/bot/discord/finalize?claim=signed-claim") as typeof page.url;
    confirm_discord_link.mockReset();
  });

  it.each([
    [409, "This Discord account is already linked to another member."],
    [400, "This link is expired or invalid. Please try again from Settings."],
    [500, "We could not complete the Discord link. If the link expired, try again from Settings."],
  ])("tells the member why a %i refusal linked nothing", async (status, message) => {
    confirm_discord_link.mockRejectedValue({ status, body: { message: "server wording" } });
    render(FinalizePage);

    await browser_page.getByRole("button", { name: "Link account" }).click();

    await expect.element(browser_page.getByText(message)).toBeVisible();
    await expect.element(browser_page.getByText("Discord account linked")).not.toBeInTheDocument();
  });
});
