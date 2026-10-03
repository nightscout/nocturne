import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { env } from "../helpers/env.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import { ACTIVITY_ID, BACKFILLED_ACTIVITY_ID, BACKFILLED_HEART_RATE_ID } from "../../mocks/vendors/nightscout.ts";

// Mirrors e2e/mocks/vendors/nightscout.ts.
const FAKE_SECRET = "e2e-fake-nightscout-secret";

interface SyncResult {
  success: boolean;
  errors: string[];
}

interface V1Activity {
  _id: string;
}

// The fake instance scopes its backfill to this base path, so parallel specs never see it.
const scopePath = (tenant: Tenant) => `/nightscout/scope/${tenant.slug}`;

async function setActivityBackfilled(tenant: Tenant, uploaded: boolean): Promise<void> {
  const res = await fetch(`${env.mocksUrl}${scopePath(tenant)}/__activity-backfill`, { method: uploaded ? "POST" : "DELETE" });
  expect(res.status).toBe(204);
}

async function sync(tenant: Tenant): Promise<void> {
  const result = await tenant.api.ok<SyncResult>("POST", "/api/v4/services/connectors/nightscout/sync", {});
  expect(result.errors).toEqual([]);
  expect(result.success).toBe(true);
}

async function activityIds(tenant: Tenant): Promise<string[]> {
  const res = await tenant.api.get<V1Activity[]>("/api/v1/activity.json?count=100");
  expect(res.status).toBe(200);
  return res.body.map((a) => a._id);
}

// The activity catch-up resumes from the newest stored activity. One the source receives later but
// dates earlier (#1778) sits behind that cursor and is reached only through the reconcile window.
describe("Nightscout connector: activity uploaded behind the cursor", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout", {
      url: `${env.mocksUrlFromApi}${scopePath(tenant)}`,
      isActive: true,
    });
    await tenant.api.ok("PUT", "/api/v4/connectors/config/nightscout/secrets", { apiSecret: FAKE_SECRET });
  });

  afterAll(async () => {
    if (tenant) await setActivityBackfilled(tenant, false);
  });

  it("imports the activity the source holds", async () => {
    await sync(tenant);

    const ids = await activityIds(tenant);
    expect(ids).toContain(ACTIVITY_ID);
    expect(ids).not.toContain(BACKFILLED_ACTIVITY_ID);
    expect(ids).not.toContain(BACKFILLED_HEART_RATE_ID);
  });

  it("fetches an activity uploaded later but dated behind the cursor", async () => {
    await setActivityBackfilled(tenant, true);

    await sync(tenant);

    expect(await activityIds(tenant)).toEqual(
      expect.arrayContaining([ACTIVITY_ID, BACKFILLED_ACTIVITY_ID, BACKFILLED_HEART_RATE_ID]),
    );
  });

  it("does not bring back a backfilled activity or heart rate the user deleted", async () => {
    await tenant.api.ok("DELETE", `/api/v1/activity/${BACKFILLED_ACTIVITY_ID}`);
    await tenant.api.ok("DELETE", `/api/v1/activity/${BACKFILLED_HEART_RATE_ID}`);
    const afterDelete = await activityIds(tenant);
    expect(afterDelete).not.toContain(BACKFILLED_ACTIVITY_ID);
    expect(afterDelete).not.toContain(BACKFILLED_HEART_RATE_ID);

    await sync(tenant);

    const ids = await activityIds(tenant);
    expect(ids).toContain(ACTIVITY_ID);
    expect(ids).not.toContain(BACKFILLED_ACTIVITY_ID);
    expect(ids).not.toContain(BACKFILLED_HEART_RATE_ID);
  });
});
