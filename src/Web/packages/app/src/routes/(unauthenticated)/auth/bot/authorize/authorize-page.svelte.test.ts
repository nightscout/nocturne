import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browser_page } from "vitest/browser";
import { page } from "$app/state";

const { claim_link } = vi.hoisted(() => ({ claim_link: vi.fn() }));

vi.mock("../bot.remote", () => ({
  getBotAuthorizeContext: () => ({ current: { mode: "tenant", isAuthenticated: true } }),
  buildTenantRedirectUrl: vi.fn(),
}));

vi.mock("#lib/api/generated/chatIdentities.generated.remote.js", () => ({
  getPending: () => ({ current: { platform: "discord" }, error: undefined }),
  claimLink: claim_link,
}));

import AuthorizePage from "./+page.svelte";

describe("confirming a chat link", () => {
  beforeEach(() => {
    page.url = new URL("http://localhost/auth/bot/authorize?state=pending-token") as typeof page.url;
    claim_link.mockReset();
  });

  it.each([
    [409, "This chat account is already linked to another member."],
    [400, "This link is expired or invalid. Run /connect in your chat app again."],
    [500, "Failed to link account. Try again."],
  ])("tells the member why a %i refusal linked nothing", async (status, message) => {
    claim_link.mockRejectedValue({ status, body: { message: "server wording" } });
    render(AuthorizePage);

    await browser_page.getByRole("button", { name: "Connect" }).click();

    await expect.element(browser_page.getByText(message)).toBeVisible();
    expect(claim_link).toHaveBeenCalledExactlyOnceWith({ token: "pending-token" });
  });
});
