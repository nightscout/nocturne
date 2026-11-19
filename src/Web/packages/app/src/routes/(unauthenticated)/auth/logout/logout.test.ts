import { describe, expect, it, vi } from "vitest";

vi.mock("#lib/api/generated/oidcs.generated.remote.js", () => ({
  logout: vi.fn(async () => ({ providerLogoutUrl: null })),
}));

import { SIGNED_OUT_COOKIE } from "#lib/stores/member-storage.js";
import { POST } from "./+server.js";

function cookieJar() {
  const set = new Map<string, { value: string; opts: Record<string, unknown> }>();
  return {
    set,
    cookies: {
      delete: vi.fn(),
      set: (name: string, value: string, opts: Record<string, unknown>) => set.set(name, { value, opts }),
    },
  };
}

describe("sign-out", () => {
  it("marks this tenant's browser storage to clear on the next page, with a host-only cookie", async () => {
    const jar = cookieJar();

    await expect(POST({ cookies: jar.cookies } as never)).rejects.toMatchObject({ status: 303, location: "/" });

    const marker = jar.set.get(SIGNED_OUT_COOKIE);
    expect(marker?.value).toBe("1");
    expect(marker?.opts).toMatchObject({ path: "/", httpOnly: false });
    expect(marker?.opts).not.toHaveProperty("domain");
  });
});
