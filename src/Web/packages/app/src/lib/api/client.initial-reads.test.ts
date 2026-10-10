import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: true, dev: false, building: false, version: "test" }));

import { getApiClient, resetApiClient } from "./client";
import type { InitialReads } from "$lib/stores/initial-reads";

const URL = "/api/v4/notifications";

describe("getApiClient with prefetched reads", () => {
  const liveFetch = vi.fn();

  beforeEach(() => {
    liveFetch.mockReset();
    liveFetch.mockRejectedValue(new Error("network"));
    const parked: InitialReads = {
      now: Date.now(),
      responses: new Map([[URL, Promise.resolve(new Response("[]", { status: 200 }))]]),
    };
    vi.stubGlobal("window", { fetch: liveFetch, __nocturneInitialReads: parked });
    resetApiClient();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetApiClient();
  });

  it("answers the first matching call from the stash and the second from the network", async () => {
    const client = getApiClient();

    await expect(client.notifications.getNotifications()).resolves.toEqual([]);
    expect(liveFetch).not.toHaveBeenCalled();

    await expect(client.notifications.getNotifications()).rejects.toThrow("network");
    expect(liveFetch).toHaveBeenCalledOnce();
    expect(liveFetch.mock.calls[0][0]).toBe(URL);
  });
});
