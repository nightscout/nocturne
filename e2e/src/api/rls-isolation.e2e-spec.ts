import { beforeAll, describe, expect, it } from "vitest";
import { postEntries, postTreatments, minutesAgo, sgvSeries } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface V1Entry {
  _id: string;
  sgv: number;
  device: string;
}

// Row level security keeps tenants apart in the database; these assert it end to end, through
// the same host-based tenant resolution production uses.
describe("tenant isolation", () => {
  let alice: Tenant;
  let bob: Tenant;

  beforeAll(async () => {
    [alice, bob] = await Promise.all([seedTenant(), seedTenant()]);
    await postEntries(alice.api, sgvSeries({ count: 5, device: "alice-cgm" }));
    await postTreatments(alice.api, [{ eventType: "Note", created_at: minutesAgo(5), notes: "alice only", enteredBy: "e2e" }]);
  });

  it("shows a tenant its own data", async () => {
    const res = await alice.api.get<V1Entry[]>("/api/v1/entries.json?count=10");
    expect(res.body).toHaveLength(5);
  });

  it("shows another tenant none of it", async () => {
    const entries = await bob.api.get<V1Entry[]>("/api/v1/entries.json?count=10");
    expect(entries.status).toBe(200);
    expect(entries.body).toEqual([]);

    const treatments = await bob.api.get<unknown[]>("/api/v1/treatments.json?count=10");
    expect(treatments.body).toEqual([]);
  });

  it("refuses one tenant's session on another tenant's host", async () => {
    const bobOnAlice = alice.anonymous.with({ kind: "bearer", token: bob.accessToken });
    const res = await bobOnAlice.get<V1Entry[]>("/api/v1/entries.json?count=10");
    expect([401, 403]).toContain(res.status);

    const me = await bobOnAlice.get("/api/v4/glucose/sensor?limit=1");
    expect([401, 403]).toContain(me.status);
  });

  it("does not let a write through another tenant's host land anywhere", async () => {
    const bobOnAlice = alice.anonymous.with({ kind: "bearer", token: bob.accessToken });
    const write = await bobOnAlice.post("/api/v1/entries", sgvSeries({ count: 1, device: "bob-intruder" }));
    expect([401, 403]).toContain(write.status);

    const aliceEntries = await alice.api.get<V1Entry[]>("/api/v1/entries.json?count=20");
    expect(aliceEntries.body.some((e) => e.device === "bob-intruder")).toBe(false);
  });

  it("keeps v4 reads apart", async () => {
    const res = await bob.api.get<{ data: unknown[] }>("/api/v4/glucose/sensor?limit=10");
    expect(res.status).toBe(200);
    expect(res.body.data).toEqual([]);
  });
});
