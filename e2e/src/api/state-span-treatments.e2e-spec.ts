import { createHash, randomUUID } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { minutesAgo } from "../helpers/data.ts";
import { ApiClient, eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

// Loop and AAPS upload Temporary Override, Temporary Target and Profile Switch as treatments, and
// LoopFollow, LoopCaregiver and AAPS followers read them back through the v1 and v3 treatment
// reads. Nightscout stores one document per upload, keeps its fields as sent, and deletes it by its
// `_id` or by a time window. Synthetic data only.

const MINUTE = 60_000;
const DAY = 24 * 60 * MINUTE;

interface Treatment {
  _id: string;
  identifier?: string;
  eventType: string;
  created_at: string;
  timestamp?: string | number;
  enteredBy?: string;
  reason?: string;
  notes?: string;
  duration?: number;
  durationType?: string;
  profile?: string;
  targetTop?: number;
  targetBottom?: number;
  insulinNeedsScaleFactor?: number;
  correctionRange?: number[];
  remoteAddress?: string;
  syncIdentifier?: string;
  insulin?: number;
}

interface V3Envelope<T> {
  status: number;
  result: T;
}

interface DirectGrant {
  token: string;
}

interface ShareLink {
  url: string | null;
}

type SpanType = "Temporary Override" | "Temporary Target" | "Profile Switch";

const SPAN_TYPES: SpanType[] = ["Temporary Override", "Temporary Target", "Profile Switch"];

/** One upload of each type, in the shape its usual uploader sends. */
function upload(eventType: SpanType, id: string, createdAt: string): Record<string, unknown> {
  switch (eventType) {
    case "Temporary Override":
      // NightscoutKit OverrideTreatment.dictionaryRepresentation.
      return {
        _id: id,
        eventType,
        created_at: createdAt,
        timestamp: createdAt,
        enteredBy: "Loop",
        reason: "Running",
        duration: 60,
        insulinNeedsScaleFactor: 0.8,
        correctionRange: [140, 160],
      };
    case "Temporary Target":
      // AAPS TreatmentMapper temporary target.
      return {
        _id: id,
        eventType,
        created_at: createdAt,
        enteredBy: "AndroidAPS",
        reason: "Activity",
        duration: 45,
        targetTop: 140,
        targetBottom: 140,
        units: "mg/dl",
      };
    case "Profile Switch":
      return { _id: id, eventType, created_at: createdAt, enteredBy: "AndroidAPS", profile: "Weekend", duration: 0 };
  }
}

const v1List = (api: ApiClient, query = "count=100") => api.ok<Treatment[]>("GET", `/api/v1/treatments.json?${query}`);

const v1Find = (api: ApiClient, eventType: string) =>
  v1List(api, `count=100&find[eventType]=${encodeURIComponent(eventType)}`);

async function v1Count(api: ApiClient, query = ""): Promise<number> {
  const rows = await api.ok<{ count: number }[]>("GET", `/api/v1/count/treatments/where${query}`);
  return rows.length === 0 ? 0 : rows[0]!.count;
}

const v3Search = (api: ApiClient) =>
  api.ok<V3Envelope<Treatment[]>>("GET", "/api/v3/treatments?limit=100").then((r) => r.result);

const v3History = (api: ApiClient) =>
  api.ok<V3Envelope<Treatment[]>>("GET", "/api/v3/treatments/history/0?limit=100").then((r) => r.result);

async function v3Get(api: ApiClient, identifier: string): Promise<Treatment> {
  const res = await api.get<V3Envelope<Treatment> | Treatment>(`/api/v3/treatments/${identifier}`);
  expect(res.status, res.text).toBe(200);
  return "result" in res.body ? res.body.result : res.body;
}

async function post(api: ApiClient, treatment: Record<string, unknown>): Promise<Treatment[]> {
  return api.ok<Treatment[]>("POST", "/api/v1/treatments", [treatment]);
}

async function expectNothingServed(api: ApiClient) {
  expect(await v1List(api)).toEqual([]);
  expect(await v1Count(api)).toBe(0);
  expect(await v3Search(api)).toEqual([]);
}

describe.each(SPAN_TYPES)("a %s treatment", (eventType) => {
  let tenant: Tenant;
  let uploadedId: string;
  let createdAt: string;

  beforeAll(async () => {
    tenant = await seedTenant();
    uploadedId = randomUUID().toUpperCase();
    createdAt = minutesAgo(30);
    await post(tenant.api, { ...upload(eventType, uploadedId, createdAt), notes: "Synthetic note" });
  });

  it("is served once, with its notes, by the v1 list, find and count", async () => {
    const [served, ...rest] = await v1List(tenant.api);
    expect(rest).toEqual([]);
    expect(served).toMatchObject({ eventType, notes: "Synthetic note" });
    expect(Date.parse(served!.created_at)).toBe(Date.parse(createdAt));

    expect(await v1Find(tenant.api, eventType)).toHaveLength(1);
    expect(await v1Find(tenant.api, "Note")).toEqual([]);
    expect(await v1Count(tenant.api)).toBe(1);
    expect(await v1Count(tenant.api, `?find[eventType]=${encodeURIComponent(eventType)}`)).toBe(1);
  });

  it("is served once by v3 search, history and get", async () => {
    const [searched, ...rest] = await v3Search(tenant.api);
    expect(rest).toEqual([]);
    expect(searched).toMatchObject({ eventType, notes: "Synthetic note" });

    const history = await v3History(tenant.api);
    expect(history).toHaveLength(1);
    expect(history[0]).toMatchObject({ eventType, notes: "Synthetic note" });

    expect(await v3Get(tenant.api, searched!.identifier!)).toMatchObject({ eventType, notes: "Synthetic note" });
  });
});

describe.each(SPAN_TYPES)("deleting a %s treatment", (eventType) => {
  it("by the id reads serve removes it", async () => {
    const tenant = await seedTenant();
    await post(tenant.api, upload(eventType, randomUUID().toUpperCase(), minutesAgo(20)));
    const [served] = await v1List(tenant.api);

    const del = await tenant.api.delete<{ deletedCount: number }>(`/api/v1/treatments/${served!._id}`);

    expect(del.status, del.text).toBe(200);
    expect(del.body.deletedCount).toBe(1);
    await expectNothingServed(tenant.api);
  });

  it("by the UUID it was uploaded under removes it and its notes", async () => {
    const tenant = await seedTenant();
    const id = randomUUID().toUpperCase();
    await post(tenant.api, { ...upload(eventType, id, minutesAgo(20)), notes: "Synthetic note" });

    const del = await tenant.api.delete<{ deletedCount: number }>(`/api/v1/treatments/${id}`);

    expect(del.status, del.text).toBe(200);
    expect(del.body.deletedCount).toBe(1);
    await expectNothingServed(tenant.api);
  });

  it("by a time window removes the ones inside it", async () => {
    const tenant = await seedTenant();
    await post(tenant.api, { ...upload(eventType, randomUUID().toUpperCase(), minutesAgo(20)), notes: "Inside" });
    await post(tenant.api, upload(eventType, randomUUID().toUpperCase(), minutesAgo(180)));
    const from = encodeURIComponent(minutesAgo(60));
    const to = encodeURIComponent(minutesAgo(0));

    const del = await tenant.api.delete<{ deletedCount: number; n: number }>(
      `/api/v1/treatments?find[created_at][$gte]=${from}&find[created_at][$lte]=${to}`,
    );

    expect(del.status, del.text).toBe(200);
    expect(del.body).toMatchObject({ deletedCount: 1, n: 1 });
    const remaining = await v1List(tenant.api);
    expect(remaining).toHaveLength(1);
    expect(Date.parse(remaining[0]!.created_at)).toBeLessThan(Date.now() - 2 * 60 * MINUTE);
    expect(await v1Count(tenant.api)).toBe(1);
  });

  it("stays deleted when the uploader sends it again", async () => {
    const tenant = await seedTenant();
    const treatment = upload(eventType, randomUUID().toUpperCase(), minutesAgo(25));
    await post(tenant.api, treatment);
    expect((await tenant.api.delete(`/api/v1/treatments/${treatment._id}`)).status).toBe(200);

    const reposted = await post(tenant.api, treatment);

    expect(reposted).toHaveLength(1);
    await expectNothingServed(tenant.api);
  });
});

describe("a Loop override", () => {
  it("is served the correctionRange and remoteAddress it was uploaded with", async () => {
    const tenant = await seedTenant();
    const id = randomUUID().toUpperCase();
    const at = new Date(Date.now() - 10 * MINUTE).toISOString().replace(/\.\d{3}Z$/, "Z");
    await post(tenant.api, {
      ...upload("Temporary Override", id, at),
      remoteAddress: "synthetic-remote-address",
      syncIdentifier: id,
    });

    const [v1] = await v1List(tenant.api);
    const [searched] = await v3Search(tenant.api);
    const [history] = await v3History(tenant.api);
    const got = await v3Get(tenant.api, searched!.identifier!);

    for (const served of [v1, searched, history, got]) {
      // LoopFollow casts correctionRange to [Int] and otherwise draws the override at [0, 0].
      expect(served).toMatchObject({
        eventType: "Temporary Override",
        timestamp: at,
        enteredBy: "Loop",
        reason: "Running",
        duration: 60,
        correctionRange: [140, 160],
        remoteAddress: "synthetic-remote-address",
        syncIdentifier: id,
      });
    }
  });
});

describe("a 24-hour-limited credential", () => {
  let tenant: Tenant;
  let clamped: ApiClient;

  beforeAll(async () => {
    tenant = await seedTenant();
    await post(tenant.api, { ...upload("Profile Switch", randomUUID(), minutesAgo((3 * DAY) / MINUTE)), profile: "Running for days" });
    await post(tenant.api, { ...upload("Temporary Override", randomUUID(), minutesAgo((2 * DAY) / MINUTE)), reason: "Ended two days ago" });
    await post(tenant.api, upload("Temporary Target", randomUUID(), minutesAgo(30)));
    await post(tenant.api, { eventType: "Note", created_at: minutesAgo((3 * DAY) / MINUTE), notes: "Old note" });
    await post(tenant.api, { eventType: "Note", created_at: minutesAgo(60), notes: "Recent note" });

    const grant = await tenant.api.ok<DirectGrant>("POST", "/api/auth/direct-grants", {
      label: "e2e clamped follower",
      scopes: ["treatments.read"],
      limitTo24Hours: true,
    });
    clamped = tenant.anonymous.with({ kind: "api-secret", secret: createHash("sha1").update(grant.token).digest("hex") });
  });

  it("still sees a profile switch running for days, and nothing that ended before the last 24 hours", async () => {
    const served = await v1List(clamped);
    expect(served.map((t) => t.eventType).sort()).toEqual(["Note", "Profile Switch", "Temporary Target"]);
    expect(served.find((t) => t.eventType === "Profile Switch")).toMatchObject({ profile: "Running for days" });
    expect(served.find((t) => t.eventType === "Note")).toMatchObject({ notes: "Recent note" });

    const searched = await v3Search(clamped);
    expect(searched.map((t) => t.eventType).sort()).toEqual(["Note", "Profile Switch", "Temporary Target"]);
  });

  it("does not narrow what the owner sees", async () => {
    expect(await v1List(tenant.api)).toHaveLength(5);
  });
});

describe("a public share granted treatments", () => {
  it("serves boluses but no overrides, temporary targets or profile switches", async () => {
    const tenant = await seedTenant();
    for (const eventType of SPAN_TYPES) await post(tenant.api, upload(eventType, randomUUID(), minutesAgo(40)));
    await post(tenant.api, { eventType: "Correction Bolus", created_at: minutesAgo(15), insulin: 1.5, enteredBy: "e2e" });
    const link = await tenant.api.ok<ShareLink>("POST", "/api/v4/share/rotate");
    await tenant.api.ok("PUT", "/api/v4/share/scopes", { scopes: ["glucose.read", "treatments.read"] });
    const viewer = new ApiClient({ host: new URL(link.url!).host });

    const served = await eventually(
      async () => {
        const res = await viewer.get<Treatment[]>("/api/v1/treatments.json?count=100");
        return res.status === 200 && res.body.length > 0 ? res.body : undefined;
      },
      { what: "treatments on the share host", timeoutMs: 75_000, intervalMs: 1_000 },
    );

    expect(served.map((t) => t.eventType)).toEqual(["Correction Bolus"]);
    expect(await v1List(tenant.api)).toHaveLength(4);
  }, 120_000);
});
