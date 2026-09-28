import { beforeAll, describe, expect, it } from "vitest";
import { eventually } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";
import {
  MIGRATION_DEVICE_STATUSES,
  MIGRATION_ENTRY_COUNT,
  MIGRATION_PAGE_SIZE,
  MIGRATION_PROFILE_NAME,
  MIGRATION_ALL_TREATMENTS,
  MIGRATION_BG_CHECKS,
  MIGRATION_TIED_COUNT,
  MIGRATION_TIED_FIRST_INDEX,
  MIGRATION_TIED_MILLS,
  MIGRATION_TREATMENTS,
  migrationEntryDate,
  sortedByCreatedAt,
} from "../../mocks/vendors/nightscout-migration.ts";

const SOURCE_SECRET = "e2e-fake-nightscout-secret";
const SOURCE_URL = "http://mocks:8080/nightscout-migration";
const BG_CHECKS_SINCE = encodeURIComponent("2026-01-01T00:00:00.000Z");
const COLLECTIONS = ["entries", "treatments", "devicestatus", "profile"];

// MigrationJobState, serialized as its number.
const COMPLETED = 3;
const TERMINAL = new Set([3, 4, 5, 6]);

interface MigrationJobInfo {
  id: string;
}

interface CollectionProgress {
  documentsMigrated: number;
  documentsFailed: number;
  failureReason: string | null;
  isComplete: boolean;
}

interface MigrationJobStatus {
  state: number;
  errorMessage: string | null;
  collectionProgress: Record<string, CollectionProgress>;
}

interface V1Entry {
  date: number;
  sgv: number;
}

interface V1Treatment {
  eventType: string;
  created_at: string;
  insulin?: number;
  carbs?: number;
  notes?: string;
}

interface V1BgCheck {
  created_at: string;
  glucose: number;
}

interface V1DeviceStatus {
  created_at: string;
}

interface V1Profile {
  defaultProfile: string;
  store: Record<string, unknown>;
}

async function migrate(tenant: Tenant, nightscoutUrl = SOURCE_URL, collections = COLLECTIONS): Promise<MigrationJobStatus> {
  const job = await tenant.api.ok<MigrationJobInfo>("POST", "/api/v4/migration/start", {
    mode: 0,
    nightscoutUrl,
    nightscoutApiSecret: SOURCE_SECRET,
    collections,
  });
  return eventually(
    async () => {
      const status = await tenant.api.ok<MigrationJobStatus>("GET", `/api/v4/migration/${job.id}/status`);
      return TERMINAL.has(status.state) ? status : undefined;
    },
    { what: "the migration job to finish", timeoutMs: 180_000, intervalMs: 500 },
  );
}

async function entryCount(tenant: Tenant): Promise<number> {
  // workaround: #1812
  return (await tenant.api.ok<{ count?: number }>("GET", "/api/v1/count/entries/where")).count ?? 0;
}

async function treatments(tenant: Tenant): Promise<V1Treatment[]> {
  return tenant.api.ok<V1Treatment[]>("GET", "/api/v1/treatments.json?count=100");
}

/** Naming created_at lifts the v1 four-day window. */
async function bgCheckCount(tenant: Tenant): Promise<number> {
  const path = `/api/v1/count/treatments/where?find[eventType]=BG%20Check&find[created_at][$gte]=${BG_CHECKS_SINCE}`;
  // workaround: #1812
  return (await tenant.api.ok<{ count?: number }>("GET", path)).count ?? 0;
}

async function deviceStatuses(tenant: Tenant): Promise<V1DeviceStatus[]> {
  return tenant.api.ok<V1DeviceStatus[]>("GET", "/api/v1/devicestatus.json?count=100");
}

async function migratedProfiles(tenant: Tenant): Promise<V1Profile[]> {
  const profiles = await tenant.api.ok<V1Profile[]>("GET", "/api/v1/profile.json");
  return profiles.filter((p) => p.defaultProfile === MIGRATION_PROFILE_NAME);
}

describe("Nightscout migration", { timeout: 420_000 }, () => {
  let tenant: Tenant;
  let first: MigrationJobStatus;

  beforeAll(async () => {
    tenant = await seedTenant();
    first = await migrate(tenant);
  }, 240_000);

  it("completes every requested collection without failures", () => {
    expect(first.state).toBe(COMPLETED);
    expect(first.errorMessage).toBeNull();
    for (const name of COLLECTIONS) {
      expect(first.collectionProgress[name], name).toMatchObject({ isComplete: true, documentsFailed: 0, failureReason: null });
    }
    expect(first.collectionProgress.entries!.documentsMigrated).toBe(MIGRATION_ENTRY_COUNT);
    expect(first.collectionProgress.treatments!.documentsMigrated).toBe(MIGRATION_ALL_TREATMENTS.length);
    expect(first.collectionProgress.devicestatus!.documentsMigrated).toBe(MIGRATION_DEVICE_STATUSES.length);
  });

  it("imports every entry once, across the page boundary", async () => {
    expect(await entryCount(tenant)).toBe(MIGRATION_ENTRY_COUNT);

    // The oldest reading of the first page, the newest of the second, and the oldest overall.
    for (const index of [MIGRATION_PAGE_SIZE - 1, MIGRATION_PAGE_SIZE, MIGRATION_ENTRY_COUNT - 1]) {
      const date = migrationEntryDate(index);
      const found = await tenant.api.ok<V1Entry[]>("GET", `/api/v1/entries.json?find[date][$eq]=${date}`);
      expect(found.filter((e) => e.date === date), `entry ${index}`).toHaveLength(1);
    }
  });

  it("imports every treatment once, across a page boundary inside one millisecond", async () => {
    const served = sortedByCreatedAt(MIGRATION_ALL_TREATMENTS);
    expect(served.length).toBeGreaterThan(MIGRATION_PAGE_SIZE);
    for (const index of [MIGRATION_TIED_FIRST_INDEX, MIGRATION_PAGE_SIZE - 1, MIGRATION_PAGE_SIZE, MIGRATION_TIED_FIRST_INDEX + MIGRATION_TIED_COUNT - 1]) {
      expect(served[index]!.expectedMills, `served treatment ${index} is a tied check`).toBe(MIGRATION_TIED_MILLS);
    }

    expect(await bgCheckCount(tenant)).toBe(MIGRATION_BG_CHECKS.length);

    const from = encodeURIComponent(new Date(MIGRATION_TIED_MILLS - 1000).toISOString());
    const to = encodeURIComponent(new Date(MIGRATION_TIED_MILLS + 1000).toISOString());
    const stored = await tenant.api.ok<V1BgCheck[]>(
      "GET",
      `/api/v1/treatments.json?count=100&find[eventType]=BG%20Check&find[created_at][$gte]=${from}&find[created_at][$lte]=${to}`,
    );
    const key = (mills: number, glucose: number) => `${mills} ${glucose}`;
    const expected = MIGRATION_BG_CHECKS.filter((c) => c.expectedMills === MIGRATION_TIED_MILLS).map((c) => key(c.expectedMills, c.glucose!));
    expect(expected).toHaveLength(MIGRATION_TIED_COUNT + 1);
    expect(stored.map((c) => key(Date.parse(c.created_at), c.glucose)).sort()).toEqual(expected.sort());
  });

  it("reads treatment times with no offset as UTC and honours an explicit offset", async () => {
    const stored = await treatments(tenant);
    for (const source of MIGRATION_TREATMENTS) {
      const matches = stored.filter((t) => t.eventType === source.eventType);
      expect(matches, source.eventType).toHaveLength(1);
      expect(Date.parse(matches[0]!.created_at), `${source.eventType} ${source.created_at}`).toBe(source.expectedMills);
    }
    expect(stored.find((t) => t.eventType === "Meal Bolus")).toMatchObject({ insulin: 4, carbs: 40 });
    expect(stored.find((t) => t.eventType === "Note")?.notes).toBe("e2e migrated note");
  });

  it("reads device status times with no offset as UTC and honours an explicit offset", async () => {
    const stored = (await deviceStatuses(tenant)).map((d) => Date.parse(d.created_at));
    for (const source of MIGRATION_DEVICE_STATUSES) {
      expect(stored.filter((t) => t === source.expectedMills), `${source.device} ${source.created_at}`).toHaveLength(1);
    }
    expect(stored).toHaveLength(MIGRATION_DEVICE_STATUSES.length);
  });

  it("imports the profile once", async () => {
    const profiles = await migratedProfiles(tenant);
    expect(profiles).toHaveLength(1);
    expect(Object.keys(profiles[0]!.store)).toContain(MIGRATION_PROFILE_NAME);
  });

  it("adds nothing when the same source is migrated again", async () => {
    const second = await migrate(tenant);
    expect(second.state).toBe(COMPLETED);
    expect(second.errorMessage).toBeNull();

    expect(await entryCount(tenant)).toBe(MIGRATION_ENTRY_COUNT);
    expect(await bgCheckCount(tenant)).toBe(MIGRATION_BG_CHECKS.length);
    const stored = await treatments(tenant);
    for (const source of MIGRATION_TREATMENTS) {
      expect(stored.filter((t) => t.eventType === source.eventType), source.eventType).toHaveLength(1);
    }
    expect(await deviceStatuses(tenant)).toHaveLength(MIGRATION_DEVICE_STATUSES.length);
    expect(await migratedProfiles(tenant)).toHaveLength(1);
  });

  // Bug #1812: the count answers `{count}`, and `{}` at zero. Flip to `it` once fixed.
  it.fails("counts in the legacy [{_id, count}] shape, and [] when nothing matches", async () => {
    const counted = await tenant.api.ok<unknown>("GET", "/api/v1/count/entries/where");
    expect(counted).toEqual([{ _id: null, count: MIGRATION_ENTRY_COUNT }]);

    const empty = await seedTenant();
    expect(await empty.api.ok<unknown>("GET", "/api/v1/count/entries/where")).toEqual([]);
  });
});

describe("Nightscout migration from a sub-path", { timeout: 240_000 }, () => {
  it("reads under the path when the Nightscout URL ends in a slash", async () => {
    const tenant = await seedTenant();
    const status = await migrate(tenant, `${SOURCE_URL}/`, ["profile"]);
    expect(status.errorMessage).toBeNull();
    expect(status.state).toBe(COMPLETED);
    expect(await migratedProfiles(tenant)).toHaveLength(1);
  });
});
