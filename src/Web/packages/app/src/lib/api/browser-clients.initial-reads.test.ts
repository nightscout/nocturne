import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("$app/environment", () => ({ browser: true, dev: false, building: false, version: "test" }));

import { notificationsClient, resetBrowserClients } from "./browser-clients";
import type { InitialReads } from "$lib/stores/initial-reads";

const URL = "/api/v4/notifications";

describe("browser clients with prefetched reads", () => {
  const liveFetch = vi.fn();

  beforeEach(() => {
    liveFetch.mockReset();
    liveFetch.mockRejectedValue(new Error("network"));
    const parked: InitialReads = {
      now: Date.now(),
      responses: new Map([[URL, Promise.resolve(new Response("[]", { status: 200 }))]]),
    };
    vi.stubGlobal("window", { fetch: liveFetch, __nocturneInitialReads: parked });
    resetBrowserClients();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    resetBrowserClients();
  });

  it("answers the first matching call from the stash and the second from the network", async () => {
    const client = notificationsClient();

    await expect(client.getNotifications()).resolves.toEqual([]);
    expect(liveFetch).not.toHaveBeenCalled();

    await expect(client.getNotifications()).rejects.toThrow("network");
    expect(liveFetch).toHaveBeenCalledOnce();
    expect(liveFetch.mock.calls[0][0]).toBe(URL);
  });
});
