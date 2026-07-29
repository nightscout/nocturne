import { createHash } from "crypto";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  INSTANCE_KEY_HEADER,
  INSTANCE_SERVICE_HEADER,
} from "$lib/server/instance-key";

const handleBotDispatch = vi.fn(
  (_event: { deliveryId: string }, _api: unknown) => Promise.resolve(),
);
const buildScopedBotApiClient = vi.fn(
  (_fetchFn: typeof fetch, _tenantSlug: string) => ({ scoped: true }),
);

vi.mock("$lib/server/bot", () => ({
  handleBotDispatch: (event: { deliveryId: string }, api: unknown) =>
    handleBotDispatch(event, api),
}));

vi.mock("$lib/server/bot/api-client", () => ({
  buildScopedBotApiClient: (fetchFn: typeof fetch, tenantSlug: string) =>
    buildScopedBotApiClient(fetchFn, tenantSlug),
}));

const { POST } = await import("./+server");

const INSTANCE_KEY = "s3cret-instance-key";
const DIGEST = createHash("sha256").update(INSTANCE_KEY).digest("hex");

const EVENT = {
  deliveryId: "8ba6dd4d-5f0d-4a0d-9a58-6a4e4bd2c111",
  channelType: "discord_dm",
  destination: "channel-1",
  tenantSlug: "acme",
  payload: { ruleName: "Low glucose" },
};

/** Invokes the handler the way SvelteKit would, with only what the route reads. */
function post(body: unknown, headers: Record<string, string>) {
  const request = new Request("https://acme.nocturne.run/api/v4/bot/dispatch", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });

  return (POST as unknown as (event: {
    request: Request;
    fetch: typeof fetch;
  }) => Promise<Response>)({ request, fetch });
}

const serviceHeaders = {
  [INSTANCE_KEY_HEADER]: DIGEST,
  [INSTANCE_SERVICE_HEADER]: "nocturne-api",
};

describe("POST /api/v4/bot/dispatch", () => {
  const previousKey = process.env.INSTANCE_KEY;
  const previousDomain = process.env.BASE_DOMAIN;

  beforeEach(() => {
    process.env.INSTANCE_KEY = INSTANCE_KEY;
    process.env.BASE_DOMAIN = "nocturne.run";
    handleBotDispatch.mockReset();
    buildScopedBotApiClient.mockClear();
  });

  afterEach(() => {
    if (previousKey === undefined) delete process.env.INSTANCE_KEY;
    else process.env.INSTANCE_KEY = previousKey;
    if (previousDomain === undefined) delete process.env.BASE_DOMAIN;
    else process.env.BASE_DOMAIN = previousDomain;
  });

  it("rejects a request with no instance key", async () => {
    const response = await post(EVENT, {});

    expect(response.status).toBe(401);
    expect(handleBotDispatch).not.toHaveBeenCalled();
  });

  it("rejects a request with a wrong instance key", async () => {
    const response = await post(EVENT, {
      [INSTANCE_KEY_HEADER]: createHash("sha256")
        .update("wrong-key")
        .digest("hex"),
      [INSTANCE_SERVICE_HEADER]: "nocturne-api",
    });

    expect(response.status).toBe(401);
    expect(handleBotDispatch).not.toHaveBeenCalled();
  });

  it("rejects a valid key with no service marker", async () => {
    const response = await post(EVENT, { [INSTANCE_KEY_HEADER]: DIGEST });

    expect(response.status).toBe(401);
    expect(handleBotDispatch).not.toHaveBeenCalled();
  });

  it("accepts a request carrying the instance key and dispatches it", async () => {
    const response = await post(EVENT, serviceHeaders);

    expect(response.status).toBe(204);
    expect(handleBotDispatch).toHaveBeenCalledTimes(1);
    expect(handleBotDispatch.mock.calls[0][0]).toMatchObject({
      deliveryId: EVENT.deliveryId,
    });
  });

  it("scopes the API client to the tenant named in the body", async () => {
    await post(EVENT, serviceHeaders);

    expect(buildScopedBotApiClient).toHaveBeenCalledTimes(1);
    expect(buildScopedBotApiClient.mock.calls[0][1]).toBe("acme");
  });

  it("ignores a forwarded host that disagrees with the body", async () => {
    await post(EVENT, {
      ...serviceHeaders,
      "X-Forwarded-Host": "victim.nocturne.run",
    });

    expect(buildScopedBotApiClient.mock.calls[0][1]).toBe("acme");
  });

  it("rejects an event with no tenant slug", async () => {
    const { tenantSlug: _omitted, ...noSlug } = EVENT;
    const response = await post(noSlug, serviceHeaders);

    expect(response.status).toBe(400);
    expect(handleBotDispatch).not.toHaveBeenCalled();
  });

  it.each([
    ["a dotted slug that would reach a different host", "sometoken.share"],
    ["an uppercase slug", "Acme"],
    ["a slug with a leading hyphen", "-acme"],
    ["a slug with CR/LF", "acme\r\nX-Evil: 1"],
    ["an empty slug", ""],
    ["a non-string slug", 1 as unknown as string],
  ])("rejects %s", async (_label, tenantSlug) => {
    // The slug becomes a Host label, so shape matters: "<token>.share" would put
    // the API into share-resolution mode rather than resolving the tenant.
    const response = await post({ ...EVENT, tenantSlug }, serviceHeaders);

    expect(response.status).toBe(400);
    expect(handleBotDispatch).not.toHaveBeenCalled();
  });
});
