import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  page: { url: new URL("http://sam.localhost/reports?range=7d"), state: {} },
  goto: vi.fn(),
  replaceState: vi.fn(),
  createRequest: vi.fn(),
}));

vi.mock("$app/state", () => ({ page: state.page }));
vi.mock("$app/navigation", () => ({
  goto: state.goto,
  replaceState: state.replaceState,
}));
vi.mock("#lib/api/generated/membershipRequests.generated.remote.js", () => ({
  createRequest: state.createRequest,
}));

import RequestMembershipDialog from "./RequestMembershipDialog.svelte";
import MembershipRequestAutoSubmit from "./MembershipRequestAutoSubmit.svelte";

/** Parks `message` through the dialog and returns the return URL sign-in was given. */
async function parkThroughDialog(message: string, returnUrl?: string): Promise<string> {
  const dialog = render(RequestMembershipDialog, { open: true, returnUrl });
  await page.getByRole("textbox").fill(message);
  await page.getByRole("button", { name: "Continue to sign up" }).click();
  await vi.waitFor(() => expect(state.goto).toHaveBeenCalledOnce());
  dialog.unmount();
  const loginUrl = new URL(String(state.goto.mock.calls[0][0]), "http://sam.localhost");
  expect(loginUrl.pathname).toBe("/auth/login");
  return loginUrl.searchParams.get("returnUrl") ?? "";
}

function arriveAuthenticated(path: string, props = { isAuthenticated: true, isGuestSession: false }) {
  state.page.url = new URL(path, "http://sam.localhost");
  render(MembershipRequestAutoSubmit, props);
}

beforeEach(() => {
  sessionStorage.clear();
  localStorage.clear();
  state.page.url = new URL("http://sam.localhost/reports?range=7d");
  state.goto.mockReset();
  state.replaceState.mockReset();
  state.createRequest.mockReset();
  state.createRequest.mockResolvedValue(undefined);
});

describe("membership request sign-in flow", () => {
  it("submits the draft once on return with its flow id, then strips the id", async () => {
    const returnUrl = await parkThroughDialog("I'm Sam's endocrinologist");
    expect(returnUrl).toMatch(/^\/reports\?range=7d&membershipRequest=[0-9a-f-]{36}$/);

    arriveAuthenticated(returnUrl);
    await vi.waitFor(() => expect(state.createRequest).toHaveBeenCalledOnce());
    expect(state.createRequest).toHaveBeenCalledWith({ message: "I'm Sam's endocrinologist" });
    expect(String(state.replaceState.mock.calls[0][0])).toBe("http://sam.localhost/reports?range=7d");

    arriveAuthenticated(returnUrl);
    await new Promise((r) => setTimeout(r, 50));
    expect(state.createRequest).toHaveBeenCalledOnce();
  });

  it("returns to the login page's own destination when opened there", async () => {
    state.page.url = new URL("http://sam.localhost/auth/login?returnUrl=%2Fcalendar");

    const returnUrl = await parkThroughDialog("hello", "/calendar");

    expect(returnUrl).toMatch(/^\/calendar\?membershipRequest=/);
  });

  it("submits nothing for a sign-in that lands without the flow id", async () => {
    await parkThroughDialog("I'm Sam's endocrinologist");

    arriveAuthenticated("/");
    await new Promise((r) => setTimeout(r, 50));

    expect(state.createRequest).not.toHaveBeenCalled();
    expect(state.replaceState).not.toHaveBeenCalled();
  });

  it("submits nothing for a guest session, even with the flow id", async () => {
    const returnUrl = await parkThroughDialog("hello");

    arriveAuthenticated(returnUrl, { isAuthenticated: true, isGuestSession: true });
    await new Promise((r) => setTimeout(r, 50));

    expect(state.createRequest).not.toHaveBeenCalled();
  });

  it("removes a legacy localStorage draft unsent", async () => {
    localStorage.setItem("nocturne:membership-request:sam.localhost", "an old introduction");

    arriveAuthenticated("/");

    await vi.waitFor(() =>
      expect(localStorage.getItem("nocturne:membership-request:sam.localhost")).toBeNull(),
    );
    expect(state.createRequest).not.toHaveBeenCalled();
  });

  it("stays silent when the request is refused", async () => {
    state.createRequest.mockRejectedValue(new Error("already a member"));
    const returnUrl = await parkThroughDialog("hello");

    arriveAuthenticated(returnUrl);

    await vi.waitFor(() => expect(state.createRequest).toHaveBeenCalledOnce());
    expect(sessionStorage.length).toBe(0);
  });
});
