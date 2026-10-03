import { beforeAll, describe, expect, it } from "vitest";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Synthetic readings in the shape xDrip+ posts to /api/v1/activity (postHeartRate): no _id, and
// each sync cycle resends the newest reading of the previous one.

const MINUTE = 60_000;

interface V1Activity {
  _id: string;
  type?: string;
  mills: number;
  bpm?: number;
}

function xDripHeartRate(at: number, bpm: number) {
  return {
    type: "hr-bpm",
    timeStamp: at,
    created_at: new Date(Math.floor(at / 1000) * 1000).toISOString().replace(".000Z", "Z"),
    bpm,
  };
}

describe("xDrip+ heart-rate upload", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  async function storedAt(times: number[]): Promise<V1Activity[]> {
    const all = await tenant.api.ok<V1Activity[]>("GET", "/api/v1/activity?count=1000");
    return all.filter((a) => a.bpm !== undefined && times.includes(a.mills)).sort((a, b) => a.mills - b.mills);
  }

  it("stores each resent sample once and reads it back with its type", async () => {
    const first = Date.now() - 30 * MINUTE + 123;
    const times = [first, first + MINUTE, first + 2 * MINUTE];

    const cycle1 = await tenant.api.request("POST", "/api/v1/activity", [xDripHeartRate(times[0], 70), xDripHeartRate(times[1], 75)]);
    const cycle2 = await tenant.api.request("POST", "/api/v1/activity", [xDripHeartRate(times[1], 75), xDripHeartRate(times[2], 80)]);
    expect(cycle1.status).toBe(200);
    expect(cycle2.status).toBe(200);

    const stored = await storedAt(times);
    expect(stored.map(({ type, mills, bpm }) => ({ type, mills, bpm }))).toEqual([
      { type: "hr-bpm", mills: times[0], bpm: 70 },
      { type: "hr-bpm", mills: times[1], bpm: 75 },
      { type: "hr-bpm", mills: times[2], bpm: 80 },
    ]);
  });

  it("answers an error, not 200, when a reading fails to store", async () => {
    const at = Date.now() - 60 * MINUTE + 456;

    const res = await tenant.api.request("POST", "/api/v1/activity", [{ ...xDripHeartRate(at, 70), device: "d".repeat(300) }]);

    expect(res.status).toBe(500);
    expect(await storedAt([at])).toEqual([]);
  });
});

describe("concurrent sleep upload", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("answers every duplicate with 200 and stores one session", async () => {
    const at = Date.now() - 12 * 60 * MINUTE;
    const body = [{ _id: "5f1a2b3c4d5e6f7a8b9c0d2a", type: "sleep", mills: at, duration: 420 }];

    const responses = await Promise.all(Array.from({ length: 6 }, () => tenant.api.request("POST", "/api/v1/activity", body)));

    expect(responses.map((r) => r.status)).toEqual(Array(6).fill(200));
    const all = await tenant.api.ok<V1Activity[]>("GET", "/api/v1/activity?count=1000");
    expect(all.filter((a) => a.type === "sleep" && a.mills === at)).toHaveLength(1);
  });
});
