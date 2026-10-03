import { describe, expect, it } from "vitest";
import { isRedirect, type Cookies } from "@sveltejs/kit";
import { load } from "./+page.server";

type LoadEvent = Parameters<typeof load>[0];

const ORIGIN = "https://demo.nocturne.run";
const DEMO_LOGIN_ENDPOINT = "/api/v4/demo/session";

/** Where the login page sends a visitor on a demo tenant, or null when it renders. */
async function redirectTarget(
  returnUrl: string,
  signedIn: boolean
): Promise<string | null> {
  const url = new URL(`/auth/login?${new URLSearchParams({ returnUrl })}`, ORIGIN);
  const locals = {
    isAuthenticated: signedIn,
    isShareHost: false,
    apiClient: { status: { getStatus: async () => ({ isDemo: true }) } },
  };
  const event = {
    url,
    locals,
    cookies: { get: () => undefined } as unknown as Cookies,
    parent: async () => ({ tenantless: false }),
  } as unknown as LoadEvent;

  try {
    await load(event);
    return null;
  } catch (thrown) {
    if (isRedirect(thrown)) return thrown.location;
    throw thrown;
  }
}

/** The path the auto-login endpoint is asked to bounce back to. */
async function autoLoginReturnPath(returnUrl: string): Promise<string> {
  const location = await redirectTarget(returnUrl, false);
  expect(location).not.toBeNull();
  const target = new URL(location!, ORIGIN);
  expect(target.pathname).toBe(DEMO_LOGIN_ENDPOINT);
  return target.searchParams.get("redirect")!;
}

const OFF_SITE = [
  "//evil.test",
  "/\\evil.test",
  "/.//evil.test",
  "/..//evil.test",
  "/a/..//evil.test",
  "/%2e//evil.test",
  "/%2E%2E//evil.test",
  "/.\\/evil.test",
  "https://evil.test",
];

describe("login page auto-login redirects", () => {
  it.each(OFF_SITE)("sends a signed-in visitor home for %s", async (returnUrl) => {
    expect(await redirectTarget(returnUrl, true)).toBe("/");
  });

  it.each(OFF_SITE)("asks the endpoint to return home for %s", async (returnUrl) => {
    expect(await autoLoginReturnPath(returnUrl)).toBe("/?__autologin=1");
  });

  it.each([
    ["/reports/../settings", "/settings"],
    ["/join?token=a%2Fb", "/join?token=a%2Fb"],
    ["/reports?range=7d&__autologin=1", "/reports?range=7d"],
  ])("sends a signed-in visitor on to %s", async (returnUrl, expected) => {
    expect(await redirectTarget(returnUrl, true)).toBe(expected);
  });

  it("marks a legitimate return path for the endpoint", async () => {
    expect(await autoLoginReturnPath("/reports?range=7d#top")).toBe(
      "/reports?range=7d&__autologin=1"
    );
    expect(await autoLoginReturnPath("/join?token=a%2Fb")).toBe(
      "/join?token=a%2Fb&__autologin=1"
    );
  });

  it("renders the passkey page once the marker has come back", async () => {
    expect(await redirectTarget("/reports?__autologin=1", false)).toBeNull();
  });
});
