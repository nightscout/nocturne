import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

let revokeImpl: () => Promise<unknown>;

vi.mock("$lib/api/generated/sessions.generated.remote", () => ({
  list: () => ({
    current: [
      {
        sessionId: "other-session",
        deviceDescription: "Firefox on Linux",
        isCurrent: false,
        lastActiveAt: "2026-09-01T00:00:00Z",
        expiresAt: "2026-10-01T00:00:00Z",
      },
    ],
    refresh: () => Promise.resolve(),
  }),
  revoke: () => revokeImpl(),
  revokeOthers: () => Promise.resolve({}),
}));

import ActiveSessions from "./ActiveSessions.svelte";

// A rejected generated remote function is SvelteKit's `HttpError`: a plain
// `{ status, body }` with no `Error` in its prototype chain.
function rejection(status: number, message: string) {
  return Promise.reject({ status, body: { message } });
}

async function attemptRevoke() {
  render(ActiveSessions);

  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await page.getByRole("button", { name: "Sign out", exact: true }).last().click();
}

describe("ActiveSessions", () => {
  beforeEach(() => {
    revokeImpl = () => Promise.resolve({});
  });

  it("shows the server's reason when signing out a session is refused", async () => {
    revokeImpl = () =>
      rejection(409, "That session was already signed out elsewhere.");

    await attemptRevoke();

    await expect
      .element(page.getByText("That session was already signed out elsewhere."))
      .toBeInTheDocument();
  });

  it("answers a 404 with the caller's own wording", async () => {
    revokeImpl = () => rejection(404, "Not Found");

    await attemptRevoke();

    await expect
      .element(page.getByText("Failed to sign out the session. Please try again."))
      .toBeInTheDocument();
  });

  it("keeps a server fault behind the component's own wording", async () => {
    revokeImpl = () => rejection(500, "npgsql: connection reset");

    await attemptRevoke();

    await expect
      .element(page.getByText("Failed to sign out the session. Please try again."))
      .toBeInTheDocument();
    await expect
      .element(page.getByText("npgsql: connection reset"))
      .not.toBeInTheDocument();
  });
});
