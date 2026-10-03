import { createHash, randomUUID } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import type { ApiClient } from "../helpers/http.ts";
import { minutesAgo } from "../helpers/data.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Loop (NightscoutKit) edits a treatment with PUT /api/v1/treatments, one object per request, the
// record's `_id` in the body, authenticated by the SHA-1 of its API secret. An override's `_id` is
// Loop's own UUID. Nightscout answers 200 with the saved document and inserts an unknown `_id`.

interface DirectGrant {
  token: string;
}

interface SavedTreatment {
  _id: string;
  eventType: string;
  created_at?: string;
  carbs?: number;
  notes?: string;
  duration?: number;
}

interface StateSpan {
  originalId?: string;
  startMills: number;
  endMills?: number;
}

interface Page<T> {
  data: T[];
}

/** An override as NightscoutKit's OverrideTreatment serialises it. */
function loopOverride(id: string, startedAt: string, extra: Record<string, unknown>) {
  return {
    _id: id,
    eventType: "Temporary Override",
    created_at: startedAt,
    timestamp: startedAt,
    enteredBy: "Loop",
    reason: "Running",
    correctionRange: [140, 160],
    insulinNeedsScaleFactor: 0.8,
    ...extra,
  };
}

/** A client authenticated as Loop is: the SHA-1 of a direct grant's token as its API secret. */
async function loopClient(tenant: Tenant): Promise<ApiClient> {
  const grant = await tenant.api.ok<DirectGrant>("POST", "/api/auth/direct-grants", {
    label: "e2e loop",
    scopes: ["glucose.readwrite", "treatments.readwrite", "devices.readwrite", "therapy.readwrite"],
  });
  return tenant.anonymous.with({ kind: "api-secret", secret: createHash("sha1").update(grant.token).digest("hex") });
}

describe("v1 PUT /treatments, as Loop sends it", () => {
  let tenant: Tenant;
  let loop: ApiClient;

  const overridesFor = async (id: string) => {
    const from = new Date(Date.now() - 6 * 60 * 60_000).toISOString();
    const page = await tenant.api.ok<Page<StateSpan>>("GET", `/api/v4/state-spans/overrides?from=${from}&limit=1000`);
    return page.data.filter((s) => s.originalId?.toLowerCase() === id.toLowerCase());
  };

  beforeAll(async () => {
    tenant = await seedTenant();
    loop = await loopClient(tenant);
  });

  it("updates an override in place by its UUID _id", async () => {
    const id = randomUUID().toUpperCase();
    const startedAt = minutesAgo(30);
    await loop.ok("POST", "/api/v1/treatments", [loopOverride(id, startedAt, { durationType: "indefinite" })]);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", loopOverride(id, startedAt, { duration: 20 }));

    expect(put.status, put.text).toBe(200);
    expect(Array.isArray(put.body)).toBe(false);
    expect(put.body).toMatchObject({ eventType: "Temporary Override", duration: 20 });

    const spans = await overridesFor(id);
    expect(spans).toHaveLength(1);
    expect(Math.abs(spans[0]!.startMills - Date.parse(startedAt))).toBeLessThan(1000);
    expect(spans[0]!.endMills! - spans[0]!.startMills).toBe(20 * 60_000);
  });

  it("inserts an override whose _id the server has never seen", async () => {
    const id = randomUUID().toUpperCase();
    const startedAt = minutesAgo(90);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", loopOverride(id, startedAt, { duration: 45 }));

    expect(put.status, put.text).toBe(200);
    expect(put.body).toMatchObject({ eventType: "Temporary Override", duration: 45 });
    const spans = await overridesFor(id);
    expect(spans).toHaveLength(1);
    expect(Math.abs(spans[0]!.startMills - Date.parse(startedAt))).toBeLessThan(1000);
    expect(spans[0]!.endMills! - spans[0]!.startMills).toBe(45 * 60_000);
  });

  it("refuses an array, which Nightscout's save never accepted", async () => {
    const put = await loop.put("/api/v1/treatments", [loopOverride(randomUUID(), minutesAgo(10), { duration: 5 })]);
    expect(put.status).toBe(400);
  });
});

// Loop keeps the `_id` each carb POST returns (its objectIdCache, keyed by syncIdentifier), edits the
// carb with PUT /api/v1/treatments carrying that `_id` and the syncIdentifier, and deletes it with
// DELETE /api/v1/treatments/{_id}. Nightscout answers the POST with the id it stored the document
// under; 15.0.8 matches a save on `identifier`, then `_id`, then `created_at` + `eventType`.
describe("v1 PUT /treatments, editing what an earlier POST stored", () => {
  let tenant: Tenant;
  let loop: ApiClient;

  /** A carb entry as NightscoutKit's CarbCorrectionNightscoutTreatment serialises it. */
  const loopCarb = (syncIdentifier: string, carbs: number, createdAt: string) => ({
    eventType: "Carb Correction",
    carbs,
    absorptionTime: 180,
    syncIdentifier,
    enteredBy: "loop://iPhone",
    created_at: createdAt,
  });

  const storedAt = async (createdAt: string) =>
    (await loop.ok<SavedTreatment[]>("GET", "/api/v1/treatments.json?count=1000")).filter(
      (t) => t.created_at !== undefined && Date.parse(t.created_at) === Date.parse(createdAt),
    );

  const postCarb = async (syncIdentifier: string, carbs: number, createdAt: string) => {
    const [created] = await loop.ok<SavedTreatment[]>("POST", "/api/v1/treatments", [loopCarb(syncIdentifier, carbs, createdAt)]);
    return created!._id;
  };

  beforeAll(async () => {
    tenant = await seedTenant();
    loop = await loopClient(tenant);
  });

  it("serves a Loop carb under the _id its POST returned", async () => {
    const createdAt = minutesAgo(40);
    const id = await postCarb(randomUUID().toUpperCase(), 18, createdAt);

    const stored = await storedAt(createdAt);
    expect(stored).toHaveLength(1);
    expect(stored[0]!._id).toBe(id);
  });

  it("edits a Loop carb in place by the _id its POST returned", async () => {
    const syncIdentifier = randomUUID().toUpperCase();
    const createdAt = minutesAgo(55);
    const id = await postCarb(syncIdentifier, 20, createdAt);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", { ...loopCarb(syncIdentifier, 35, createdAt), _id: id });

    expect(put.status, put.text).toBe(200);
    const stored = await storedAt(createdAt);
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ _id: id, carbs: 35 });
  });

  it("deletes a Loop carb by the _id its POST returned", async () => {
    const createdAt = minutesAgo(70);
    const id = await postCarb(randomUUID().toUpperCase(), 22, createdAt);

    const del = await loop.delete(`/api/v1/treatments/${id}`);

    expect(del.status, del.text).toBe(200);
    expect(await storedAt(createdAt)).toHaveLength(0);
  });

  it("does not bring back a Loop carb the user deleted", async () => {
    const syncIdentifier = randomUUID().toUpperCase();
    const createdAt = minutesAgo(85);
    const id = await postCarb(syncIdentifier, 22, createdAt);
    const [served] = await storedAt(createdAt);
    expect((await tenant.api.delete(`/api/v1/treatments/${served!._id}`)).status).toBe(200);

    const put = await loop.put<SavedTreatment[]>("/api/v1/treatments", { ...loopCarb(syncIdentifier, 30, createdAt), _id: id });

    expect(put.status, put.text).toBe(200);
    expect(put.body).toEqual([]);
    expect(await storedAt(createdAt)).toHaveLength(0);
  });

  it("replaces the treatment stored at the same created_at with the same eventType when the body has no id", async () => {
    const createdAt = minutesAgo(115);
    await tenant.api.ok("POST", "/api/v1/treatments", [{ eventType: "Note", notes: "before", created_at: createdAt }]);

    const put = await loop.put<SavedTreatment>("/api/v1/treatments", { eventType: "Note", notes: "after", created_at: createdAt });

    expect(put.status, put.text).toBe(200);
    const stored = await storedAt(createdAt);
    expect(stored).toHaveLength(1);
    expect(stored[0]).toMatchObject({ eventType: "Note", notes: "after" });
  });
});
