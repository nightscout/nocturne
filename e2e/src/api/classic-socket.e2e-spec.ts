import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { io, type Socket } from "socket.io-client";
import { minutesAgo, postEntries, postTreatments, sgvSeries } from "../helpers/data.ts";
import { env } from "../helpers/env.ts";
import { eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface DirectGrant {
  token: string;
}

interface Delta {
  delta?: boolean;
  sgvs?: { mgdl?: number; mills?: number }[];
  treatments?: { notes?: string }[];
  devicestatus?: { device?: string }[];
}

const READ_SCOPES = ["glucose.read", "treatments.read", "devices.read", "therapy.read"];

/**
 * A classic Nightscout socket client, connected the way LoopFollow connects: its access token in
 * the handshake query, then an `authorize` naming it as `secret` and taking no acknowledgement.
 */
class ClassicSocket {
  readonly updates: unknown[] = [];
  connected = false;
  private readonly socket: Socket;

  private constructor(socket: Socket) {
    this.socket = socket;
    socket.on("connected", () => (this.connected = true));
    socket.on("dataUpdate", (payload: unknown) => this.updates.push(payload));
  }

  static async authorize(tenant: Tenant, token: string): Promise<ClassicSocket> {
    const socket = io(`http://127.0.0.1:${env.webPort}`, {
      transports: ["websocket"],
      reconnection: false,
      extraHeaders: { "x-forwarded-host": tenant.host },
      query: { token },
    });
    const client = new ClassicSocket(socket);
    await new Promise<void>((resolve, reject) => {
      socket.once("connect", resolve);
      socket.once("connect_error", reject);
    });
    socket.emit("authorize", { client: "LoopFollow", history: 1, secret: token });
    return client;
  }

  get deltas(): Delta[] {
    return this.updates.filter((u): u is Delta => typeof u === "object" && u !== null && !Array.isArray(u));
  }

  waitForDelta(matches: (delta: Delta) => boolean, what: string): Promise<Delta> {
    return eventually(async () => this.deltas.find(matches), { what });
  }

  close(): void {
    this.socket.close();
  }
}

async function grantToken(tenant: Tenant, limitTo24Hours = false): Promise<string> {
  const grant = await tenant.api.ok<DirectGrant>("POST", "/api/auth/direct-grants", {
    label: "e2e LoopFollow",
    scopes: READ_SCOPES,
    limitTo24Hours,
  });
  return grant.token;
}

// LoopFollow polls three times slower once the socket says `connected`, and refetches a collection
// when a `dataUpdate` delta names it. Both have to hold for its display to stay current.
describe("classic Nightscout socket", () => {
  let tenant: Tenant;
  let follower: ClassicSocket;

  beforeAll(async () => {
    tenant = await seedTenant();
    follower = await ClassicSocket.authorize(tenant, await grantToken(tenant));
    await eventually(async () => follower.connected, { what: "the connected event" });
  });

  afterAll(() => follower?.close());

  it("names an uploaded reading in a delta", async () => {
    const [entry] = sgvSeries({ count: 1, valueAt: () => 213, device: "e2e-classic-socket" });
    await postEntries(tenant.api, [entry!]);

    const delta = await follower.waitForDelta((d) => d.sgvs?.some((s) => s.mgdl === 213) === true, "the reading's delta");

    expect(delta.delta).toBe(true);
    expect(delta.sgvs?.find((s) => s.mgdl === 213)?.mills).toBe(entry!.date);
  });

  it("names an uploaded treatment in a delta", async () => {
    const notes = `e2e classic socket ${Date.now()}`;
    await postTreatments(tenant.api, [{ eventType: "Note", created_at: minutesAgo(10), notes, enteredBy: "e2e" }]);

    const delta = await follower.waitForDelta((d) => d.treatments?.some((t) => t.notes === notes) === true, "the treatment's delta");

    expect(delta.delta).toBe(true);
  });

  it("names an uploaded device status in a delta", async () => {
    const device = `e2e-classic-socket-${Date.now()}`;
    await tenant.api.ok("POST", "/api/v1/devicestatus", { device, created_at: new Date().toISOString(), uploaderBattery: 81 });

    const delta = await follower.waitForDelta((d) => d.devicestatus !== undefined, "the device status delta");

    expect(delta.delta).toBe(true);
  });

  it("names treatments when an override is written as a state span", async () => {
    const before = follower.deltas.length;
    await tenant.api.ok("POST", "/api/v4/state-spans", {
      category: "Override",
      state: "Custom",
      startMills: Date.now() - 5 * 60 * 1000,
      endMills: Date.now() + 55 * 60 * 1000,
      source: "e2e",
    });

    await eventually(async () => follower.deltas.slice(before).find((d) => d.treatments !== undefined), {
      what: "the override's delta",
    });
  });

  it("is sent nothing but deltas as dataUpdate", () => {
    expect(follower.updates.length).toBeGreaterThan(0);
    expect(follower.updates).toEqual(follower.deltas);
    expect(follower.deltas.every((d) => d.delta === true)).toBe(true);
  });

  it("does not tell a credential limited to 24 hours that it is connected", async () => {
    const clamped = await ClassicSocket.authorize(tenant, await grantToken(tenant, true));
    try {
      // The unclamped follower hears this write once it is relayed, so the clamped one would have too.
      await postEntries(tenant.api, sgvSeries({ count: 1, end: Date.now() - 20 * 60 * 1000, valueAt: () => 214, device: "e2e-classic-socket" }));
      await follower.waitForDelta((d) => d.sgvs?.some((s) => s.mgdl === 214) === true, "the later reading's delta");

      expect(clamped.connected).toBe(false);
      expect(clamped.updates).toEqual([]);
    } finally {
      clamped.close();
    }
  });
});
