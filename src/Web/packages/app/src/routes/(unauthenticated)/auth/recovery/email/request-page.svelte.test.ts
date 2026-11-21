import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";

const state = vi.hoisted(() => ({
  request: vi.fn(),
}));

vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  requestRecoveryLink: (request: { email: string }) => state.request(request),
}));

import RequestPage from "./+page.svelte";

async function submit(email: string) {
  await page.getByLabelText("Email address").fill(email);
  await page.getByRole("button", { name: "Send link" }).click();
}

describe("Recovery by email request page", () => {
  beforeEach(() => {
    state.request.mockReset();
    state.request.mockResolvedValue(undefined);
  });

  it("asks for one email address and sends it trimmed", async () => {
    render(RequestPage);

    await expect.element(page.getByLabelText("Email address")).toHaveAttribute("type", "email");
    await expect.element(page.getByLabelText("Email address")).toHaveAttribute("autocomplete", "email");
    await submit("  sam@example.com  ");

    await vi.waitFor(() => expect(state.request).toHaveBeenCalledWith({ email: "sam@example.com" }));
  });

  it.each(["known@example.com", "nobody@example.com"])(
    "answers %s with the same confirmation, which names no account",
    async (address) => {
      render(RequestPage);

      await submit(address);

      const confirmation = page.getByTestId("recovery-link-sent");
      await expect
        .element(confirmation)
        .toHaveTextContent(
          "If that address is confirmed on an account here, we have sent a link to it. It works for 30 minutes.",
        );
      expect(confirmation.element().textContent).not.toContain(address);
      await expect.element(page.getByRole("link", { name: "Back to sign in" }).first()).toBeInTheDocument();
    },
  );

  it("tells a throttled visitor to wait", async () => {
    state.request.mockImplementation(async () => error(429, "Too many requests"));
    render(RequestPage);

    await submit("sam@example.com");

    await expect.element(page.getByText("Too many attempts.", { exact: false })).toBeInTheDocument();
    await expect.element(page.getByTestId("recovery-link-sent")).not.toBeInTheDocument();
  });

  it("says recovery by email is unavailable when delivery was turned off", async () => {
    state.request.mockImplementation(async () =>
      error(404, { message: "Not found", code: "email_links_unavailable" } as never),
    );
    render(RequestPage);

    await submit("sam@example.com");

    await expect
      .element(page.getByText("Recovery by email is not available on this site.", { exact: false }))
      .toBeInTheDocument();
    await expect.element(page.getByTestId("recovery-link-sent")).not.toBeInTheDocument();
  });

  it("leads to the recovery code page for a code from an administrator", async () => {
    render(RequestPage);

    await expect
      .element(page.getByRole("link", { name: "Enter a recovery code" }))
      .toHaveAttribute("href", "/auth/recovery");
  });
});
