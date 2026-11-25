import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("http://sam.localhost/auth/login"),
  tenantless: false,
  isAuthenticated: false,
  unreadable: false,
  emailRecoveryOffered: false,
  goto: vi.fn(async (_url: string, _options?: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    get data() {
      return { tenantless: state.tenantless };
    },
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  goto: state.goto,
}));
vi.mock("../auth.remote", () => ({
  getAuthState: () =>
    remoteQuery(() =>
      state.isAuthenticated
        ? { isAuthenticated: true, unreadable: false, user: { subjectId: "s" } }
        : { isAuthenticated: false, unreadable: state.unreadable, user: null },
    ),
}));
vi.mock("#lib/api/generated/index.js", () => ({
  getAuthStatus: () =>
    remoteQuery(() => ({ allowAccessRequests: true, emailRecoveryOffered: state.emailRecoveryOffered })),
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  accessRequestOptions: vi.fn(),
  accessRequestComplete: vi.fn(),
}));
vi.mock("#lib/components/auth/LoginForm.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/auth/GuestCodeForm.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("water-ui", async (importOriginal) => ({
  ...(await importOriginal<typeof import("water-ui")>()),
  Artwork: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import LoginPage from "./+page.svelte";

function loginWith(returnUrl: string) {
  state.url = new URL(`http://sam.localhost/auth/login?${new URLSearchParams({ returnUrl })}`);
  return render(LoginPage, { data: {} } as never);
}

const DRAFT_KEY = "nocturne.member.custom-report-draft.dev.s.new";

function refusedWith(reason: string, returnUrl = "/calendar") {
  state.url = new URL(`http://sam.localhost/auth/login?${new URLSearchParams({ reason, returnUrl })}`);
  return render(LoginPage, { data: {} } as never);
}

beforeEach(() => {
  state.tenantless = false;
  state.isAuthenticated = false;
  state.unreadable = false;
  state.emailRecoveryOffered = false;
  localStorage.clear();
  state.goto.mockClear();
});

describe("Login page", () => {
  it("offers Cannot sign in? in the footer, outside the sign-in methods, when email recovery is offered", async () => {
    state.emailRecoveryOffered = true;
    loginWith("/calendar");

    const link = page.getByTestId("cant-sign-in-link");
    await expect.element(link).toHaveAttribute("href", "/auth/recovery/email");
    const terms = page.getByRole("link", { name: "terms of service" }).element();
    expect(terms.compareDocumentPosition(link.element()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(terms.parentElement?.parentElement?.parentElement).toBe(link.element().parentElement?.parentElement?.parentElement);
    await expect.element(page.getByText("Having trouble signing in?")).not.toBeInTheDocument();
  });

  it("keeps the help footer exactly as it was when email recovery is not offered", async () => {
    loginWith("/calendar");

    await expect.element(page.getByRole("link", { name: "Get help" })).toHaveAttribute("href", "/auth/help");
    await expect.element(page.getByTestId("cant-sign-in-link")).not.toBeInTheDocument();
  });

  it("opens the membership request dialog in place, without leaving for sign-in", async () => {
    loginWith("/calendar");

    await page.getByTestId("request-membership-link").click();

    await expect.element(page.getByTestId("request-membership-dialog")).toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Send request" })).toBeInTheDocument();
    expect(state.goto).not.toHaveBeenCalled();
  });

  it("sends a visitor who is already signed in home rather than off-site", async () => {
    state.isAuthenticated = true;

    loginWith("https://evil.test/x");

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalledWith("/", { replace: true }));
  });

  it("shows the sign-in form and clears what the browser kept for a member once the server says nobody is signed in", async () => {
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await expect.element(page.getByTestId("sign-in-card")).toBeInTheDocument();
    await vi.waitFor(() => expect(localStorage.getItem(DRAFT_KEY)).toBeNull());
    expect(state.goto).not.toHaveBeenCalled();
  });

  it("keeps the drafts of a member who is still signed in", async () => {
    state.isAuthenticated = true;
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await vi.waitFor(() => expect(state.goto).toHaveBeenCalled());
    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });

  it("says Nocturne has no account for the sign-in when a provider sign-in is refused on a tenantless host", async () => {
    state.tenantless = true;
    refusedWith("not_a_member");

    const notice = page.getByTestId("sign-in-refused");
    await expect.element(notice).toHaveTextContent("Nocturne has no account for that sign-in. To get an account, use an invite link.");
    await expect.element(notice).not.toHaveTextContent("You are not a member of this site.");
  });

  it("says the person is not a member when a provider sign-in is refused on a host with a tenant", async () => {
    refusedWith("not_a_member");

    const notice = page.getByTestId("sign-in-refused");
    await expect.element(notice).toHaveTextContent("You are not a member of this site. Ask the site owner for access.");
    await expect.element(notice).not.toHaveTextContent("Nocturne has no account for that sign-in");
    await expect.element(page.getByTestId("request-membership-link")).toBeInTheDocument();
  });

  it("shows no refusal for a reason it does not know, and never prints the reason", async () => {
    refusedWith("<b>boom</b>");

    await expect.element(page.getByTestId("sign-in-card")).toBeInTheDocument();
    await expect.element(page.getByTestId("sign-in-refused")).not.toBeInTheDocument();
    await expect.element(page.getByText("boom")).not.toBeInTheDocument();
  });

  it("shows no refusal on an ordinary visit", async () => {
    loginWith("/calendar");

    await expect.element(page.getByTestId("sign-in-card")).toBeInTheDocument();
    await expect.element(page.getByTestId("sign-in-refused")).not.toBeInTheDocument();
  });

  it("keeps the drafts when the server could not read the session", async () => {
    state.unreadable = true;
    localStorage.setItem(DRAFT_KEY, "{}");

    loginWith("/calendar");

    await expect.element(page.getByTestId("sign-in-card")).toBeInTheDocument();
    expect(localStorage.getItem(DRAFT_KEY)).toBe("{}");
  });
});
