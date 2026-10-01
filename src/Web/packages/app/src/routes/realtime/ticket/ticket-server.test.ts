import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The ticket endpoint's one job beyond minting: telling the client whether a
 * ticket-less answer is worth retrying. The client treats a denial as terminal
 * and deliberately silent, so a fault misfiled as a denial takes realtime down
 * with no error shown and no recovery short of a page reload.
 */

const probe = vi.fn<typeof fetch>();

vi.mock("@nocturne/bridge/ticket", () => ({
  REALTIME_ADMISSION_PATH: "/api/v4/me/realtime-admission",
  signHandshakeTicket: (
    _secret: string,
    host: string,
    tenantRelay: boolean,
    subjectId?: string
  ) =>
    `${tenantRelay ? "member" : "restricted"}-ticket-for-${host}${
      subjectId ? `:${subjectId}` : ""
    }`,
}));
vi.mock("$env/dynamic/public", () => ({ env: {} }));
vi.mock("$lib/server/api-client-factory", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$lib/server/api-client-factory")>()),
  getApiBaseUrl: () => apiBaseUrl,
}));
vi.mock("$lib/server/request-host", () => ({
  getEffectiveHost: () => effectiveHost,
  getOriginalProto: () => "https",
}));
vi.mock("$lib/config/auth-cookies", () => ({
  AUTH_COOKIE_NAMES: {
    accessToken: "access",
    refreshToken: "refresh",
    guestSession: "guest",
    platformAccess: "platform",
  },
}));

let apiBaseUrl: string | undefined = "http://api.internal";
let effectiveHost: string | undefined = "sleepy.nocturne.run";

const { GET } = await import("./+server");

/** The endpoint's answer, which is always a 200 whatever it decided. */
async function mint(
  cookies: Record<string, string> = {}
): Promise<{ token: string | null; retry?: boolean }> {
  const event = {
    request: new Request("https://sleepy.nocturne.run/realtime/ticket"),
    cookies: { get: (name: string) => cookies[name], set: vi.fn() },
    fetch: probe,
    locals: { rawSetCookies: [] },
  };
  // The handler only uses the slice of RequestEvent built above.
  const res = await GET(event as unknown as Parameters<typeof GET>[0]);
  expect(res.status).toBe(200);
  return res.json();
}

beforeEach(() => {
  process.env.INSTANCE_KEY = "an-instance-signing-secret";
  apiBaseUrl = "http://api.internal";
  effectiveHost = "sleepy.nocturne.run";
  probe.mockReset();
});

function admits(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200 });
}

function refuses(status: number) {
  return new Response(null, { status });
}

afterEach(() => {
  delete process.env.INSTANCE_KEY;
});

describe("realtime ticket endpoint", () => {
  it("mints a tenant-room ticket for a member session", async () => {
    probe.mockResolvedValue(admits({ tenantRelay: true }));

    expect(await mint({ access: "member-session" })).toEqual({
      token: "member-ticket-for-sleepy.nocturne.run",
    });
    expect(probe.mock.calls[0][0]).toBe(
      "http://api.internal/api/v4/me/realtime-admission"
    );
  });

  it("signs the admission's subject id into a member ticket", async () => {
    // The bridge routes each relayed notification to the subject room named here; without
    // the id the member's socket receives none of its own notifications.
    probe.mockResolvedValue(
      admits({ tenantRelay: true, subjectId: "0a5f2c1e-1111-4222-8333-444455556666" })
    );

    expect(await mint({ access: "member-session" })).toEqual({
      token:
        "member-ticket-for-sleepy.nocturne.run:0a5f2c1e-1111-4222-8333-444455556666",
    });
  });

  it("leaves the subject id out when the admission carries none", async () => {
    probe.mockResolvedValue(admits({ tenantRelay: true, subjectId: null }));

    expect(await mint({ access: "member-session" })).toEqual({
      token: "member-ticket-for-sleepy.nocturne.run",
    });
  });

  it("mints a restricted ticket for a guest session", async () => {
    probe.mockResolvedValue(admits({ tenantRelay: false }));

    expect(await mint({ guest: "guest-session" })).toEqual({
      token: "restricted-ticket-for-sleepy.nocturne.run",
    });
  });

  it("mints a restricted ticket when the admission is silent on the tenant room", async () => {
    probe.mockResolvedValue(admits({}));

    expect(await mint()).toEqual({
      token: "restricted-ticket-for-sleepy.nocturne.run",
    });
  });

  it("asks with the caller's own credential and never the instance key", async () => {
    // With the instance key and no cookie, the API would authenticate the
    // visitor as the instance service and admit them to the tenant room.
    probe.mockResolvedValue(admits({ tenantRelay: true }));

    await mint({ guest: "guest-session" });

    const sent = new Headers(probe.mock.calls[0][1]?.headers);
    expect(sent.get("Cookie")).toBe("guest=guest-session");
    expect(sent.has("X-Instance-Key")).toBe(false);
    expect(sent.has("X-Instance-Service")).toBe(false);
  });

  it("reports a refused read as a denial the client should not retry", async () => {
    probe.mockResolvedValue(refuses(401));

    expect(await mint()).toEqual({ token: null, retry: false });
  });

  it("reports an API fault as retryable", async () => {
    probe.mockResolvedValue(refuses(503));

    expect(await mint()).toEqual({ token: null, retry: true });
  });

  it("reports an unreachable API as retryable", async () => {
    probe.mockRejectedValue(new Error("network"));

    expect(await mint()).toEqual({ token: null, retry: true });
  });

  it.each([
    ["no signing secret", () => (process.env.INSTANCE_KEY = "")],
    ["no API url", () => (apiBaseUrl = undefined)],
    ["no resolvable host", () => (effectiveHost = undefined)],
  ])(
    "reports an instance with %s as retryable, not as a denial",
    async (_case, misconfigure) => {
      misconfigure();

      // A deployment fault, not a per-user policy decision: filing it as a
      // denial would latch every viewer into a terminal, silent "realtime not
      // permitted" that survives the operator fixing the configuration.
      expect(await mint()).toEqual({ token: null, retry: true });
      expect(probe).not.toHaveBeenCalled();
    }
  );
});
