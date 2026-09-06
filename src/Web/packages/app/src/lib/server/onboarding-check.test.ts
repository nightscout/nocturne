import { describe, it, expect, vi } from "vitest";
import type { Cookies } from "@sveltejs/kit";
import { checkOnboarding } from "./onboarding-check";

function createCookies(initial: Record<string, string> = {}): {
  cookies: Cookies;
  store: Map<string, string>;
} {
  const store = new Map(Object.entries(initial));
  const cookies = {
    get: (name: string) => store.get(name),
    set: (name: string, value: string) => {
      store.set(name, value);
    },
    delete: (name: string) => {
      store.delete(name);
    },
  } as unknown as Cookies;
  return { cookies, store };
}

describe("checkOnboarding", () => {
  it("returns isComplete: true from the cookie fast path without calling the API", async () => {
    const { cookies } = createCookies({ "nocturne-setup-complete": "true" });
    const getAuthStatus = vi.fn();

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: true });
    expect(getAuthStatus).not.toHaveBeenCalled();
  });

  it("returns isComplete: true and re-sets the cookie when the API confirms completion", async () => {
    const { cookies, store } = createCookies();
    const getAuthStatus = vi
      .fn()
      .mockResolvedValue({ onboardingCompleted: true });

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: true });
    expect(store.get("nocturne-setup-complete")).toBe("true");
  });

  it("returns isComplete: false when the API explicitly says onboarding is not done", async () => {
    const { cookies, store } = createCookies();
    const getAuthStatus = vi
      .fn()
      .mockResolvedValue({ onboardingCompleted: false });

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: false });
    expect(store.has("nocturne-setup-complete")).toBe(false);
  });

  it("passes an AbortSignal so a hung API can't block the load function (regression for #dashboard-hang)", async () => {
    const { cookies } = createCookies();
    const getAuthStatus = vi
      .fn()
      .mockResolvedValue({ onboardingCompleted: true });

    await checkOnboarding(cookies, { passkey: { getAuthStatus } }, true);

    expect(getAuthStatus).toHaveBeenCalledTimes(1);
    const signal = getAuthStatus.mock.calls[0][0];
    expect(signal).toBeInstanceOf(AbortSignal);
  });

  it("returns isComplete: true on API failure to avoid trapping users in a redirect loop", async () => {
    const { cookies } = createCookies();
    const getAuthStatus = vi.fn().mockRejectedValue(new Error("network down"));

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: true });
  });

  it("returns isComplete: false on the 503 the API serves when no tenant exists", async () => {
    // The fresh-install signal, and the only one available on a host that resolves no tenant:
    // the status endpoint reports "setup_required" for a populated multi-tenant apex too.
    const { cookies } = createCookies();
    const getAuthStatus = vi.fn().mockRejectedValue({ status: 503 });

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: false });
  });

  it("returns isComplete: true on the 404 a populated tenantless host serves", async () => {
    // A multi-tenant apex or a reserved dashboard slug: tenants exist, this host just names
    // none of them. Sending it to the fresh-install wizard would be wrong.
    const { cookies } = createCookies();
    const getAuthStatus = vi.fn().mockRejectedValue({ status: 404 });

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: true });
  });

  it("returns isComplete: true when the request aborts (timeout)", async () => {
    const { cookies } = createCookies();
    const getAuthStatus = vi.fn().mockImplementation(
      (signal?: AbortSignal) =>
        new Promise((_resolve, reject) => {
          signal?.addEventListener("abort", () =>
            reject(new DOMException("aborted", "AbortError")),
          );
        }),
    );

    const result = await checkOnboarding(
      cookies,
      { passkey: { getAuthStatus } },
      true,
    );

    expect(result).toEqual({ isComplete: true });
  }, 10_000);
});
