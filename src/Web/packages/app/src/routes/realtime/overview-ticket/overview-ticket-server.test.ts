import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/env/private", () => ({
  get INSTANCE_KEY() {
    return process.env.INSTANCE_KEY;
  },
}));
import type { OverviewGrant } from "@nocturne/bridge/ticket";

const probe = vi.fn<typeof fetch>();
const signed = vi.fn<(host: string, grant: OverviewGrant) => void>();
const SUBJECT = "0a5f2c1e-1111-4222-8333-444455556666";
const TENANT_A = "0198a3c4-7e21-7b10-9d2f-5c6e7f809a1b";
const TENANT_B = "0198a3c4-7e21-7b10-9d2f-5c6e7f809a1c";

vi.mock("@nocturne/bridge/ticket", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@nocturne/bridge/ticket")>()),
  signOverviewTicket: (_secret: string, host: string, grant: OverviewGrant) => {
    signed(host, grant);
    return `overview-ticket-for-${host}`;
  },
}));
vi.mock("#lib/server/api-client-factory.js", async (importOriginal) => ({
  ...(await importOriginal<typeof import("#lib/server/api-client-factory.js")>()),
  getApiBaseUrl: () => "http://api.internal",
}));
vi.mock("#lib/server/request-host.js", () => ({
  getEffectiveHost: () => "nocturne.run",
  getOriginalProto: () => "https",
}));
vi.mock("#lib/config/auth-cookies.js", () => ({
  AUTH_COOKIE_NAMES: {
    accessToken: "access",
    refreshToken: "refresh",
    guestSession: "guest",
    platformAccess: "platform",
  },
}));

const { GET } = await import("./+server");

async function mint(
  cookies: Record<string, string> = {}
): Promise<{ token: string | null; retry?: boolean }> {
  const event = {
    request: new Request("https://nocturne.run/realtime/overview-ticket"),
    cookies: { get: (name: string) => cookies[name], set: vi.fn() },
    fetch: probe,
    locals: { rawSetCookies: [] },
  };
  // The handler only uses the slice of RequestEvent built above.
  const res = await GET(event as unknown as Parameters<typeof GET>[0]);
  expect(res.status).toBe(200);
  return res.json();
}

function admits(body: Record<string, unknown>) {
  return new Response(JSON.stringify(body), { status: 200 });
}

beforeEach(() => {
  process.env.INSTANCE_KEY = "an-instance-signing-secret";
  probe.mockReset();
  signed.mockReset();
});

afterEach(() => {
  delete process.env.INSTANCE_KEY;
});

describe("overview ticket endpoint", () => {
  it("signs the subject and the tenants the overview admission names, for this host", async () => {
    probe.mockResolvedValue(admits({ subjectId: SUBJECT, tenantIds: [TENANT_B, TENANT_A] }));

    expect(await mint({ access: "member-session" })).toEqual({
      token: "overview-ticket-for-nocturne.run",
    });
    expect(probe.mock.calls[0][0]).toBe("http://api.internal/api/v4/me/overview-admission");
    expect(signed).toHaveBeenCalledWith("nocturne.run", {
      subjectId: SUBJECT,
      tenantIds: [TENANT_A, TENANT_B],
    });
  });

  it("asks with the caller's own credential and never the instance key", async () => {
    probe.mockResolvedValue(admits({ subjectId: SUBJECT, tenantIds: [] }));

    await mint({ access: "member-session" });

    const sent = new Headers(probe.mock.calls[0][1]?.headers);
    expect(sent.get("Cookie")).toBe("access=member-session");
    expect(sent.has("X-Instance-Key")).toBe(false);
    expect(sent.has("X-Instance-Service")).toBe(false);
  });

  it.each([401, 403])("reports a %i as a denial the client should not retry", async (status) => {
    probe.mockResolvedValue(new Response(null, { status }));

    expect(await mint({ guest: "guest-session" })).toEqual({ token: null, retry: false });
    expect(signed).not.toHaveBeenCalled();
  });

  it("reports an API fault as retryable", async () => {
    probe.mockResolvedValue(new Response(null, { status: 503 }));

    expect(await mint({ access: "member-session" })).toEqual({ token: null, retry: true });
  });

  it("signs no ticket for an admission that names no subject, and reports it as retryable", async () => {
    probe.mockResolvedValue(admits({ tenantIds: [TENANT_A] }));

    expect(await mint({ access: "member-session" })).toEqual({ token: null, retry: true });
    expect(signed).not.toHaveBeenCalled();
  });
});
