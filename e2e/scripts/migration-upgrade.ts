// Upgrade test: the latest release writes a realistic database, then the API built from this
// checkout starts on that same database, has to apply its migrations and come up healthy, and
// must still read everything back. This is the path that crash-loops a self-hosted instance when
// a migration trips over data an older version wrote (see "Unique indexes over existing data" in
// CLAUDE.md), which a fresh-database suite never exercises.
//
//   node scripts/migration-upgrade.ts
//
// E2E_PREVIOUS_API_IMAGE picks the starting image (default: the latest release, :latest).
// E2E_SKIP_BUILD=1 reuses an existing nocturne-api:e2e instead of building it.

import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import { capture, compose, E2E_DIR, ensureImages, run } from "./lib.ts";

const PROJECT = "nocturne-e2e-upgrade";
const PREVIOUS = process.env.E2E_PREVIOUS_API_IMAGE ?? "ghcr.io/nightscout/nocturne/nocturne-api:latest";
// Its own ports, so it can run beside a live `pnpm e2e:up` stack.
const ports = { E2E_API_PORT: "1640", E2E_WEB_PORT: "1641", E2E_POSTGRES_PORT: "1643", E2E_MOCKS_PORT: "1644" };
const API = `http://127.0.0.1:${ports.E2E_API_PORT}`;
const BASE_DOMAIN = `nocturne.localhost:${ports.E2E_WEB_PORT}`;
const TENANTS = 3;

interface Seeded {
  slug: string;
  accessToken: string;
  apiToken: string;
  expected: Snapshot;
}

interface Snapshot {
  entries: number[];
  treatments: string[];
  alertRules: string[];
  share: { enabled: boolean; fullHistory: boolean; scopes: string[] } | null;
  connectorUrl: unknown;
  labResults: number | null;
}

async function call<T>(method: string, path: string, opts: { host?: string; token?: string; secret?: string; body?: unknown } = {}): Promise<T> {
  const headers: Record<string, string> = { accept: "application/json" };
  if (opts.host) headers["x-forwarded-host"] = opts.host;
  if (opts.token) headers.authorization = `Bearer ${opts.token}`;
  if (opts.secret) headers["api-secret"] = opts.secret;
  if (opts.body !== undefined) headers["content-type"] = "application/json";
  const res = await fetch(API + path, { method, headers, body: opts.body === undefined ? undefined : JSON.stringify(opts.body) });
  const text = await res.text();
  if (!res.ok) throw new HttpError(res.status, `${method} ${path} -> HTTP ${res.status}: ${text.slice(0, 400)}`);
  return (text ? JSON.parse(text) : undefined) as T;
}

class HttpError extends Error {
  readonly status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

/**
 * A step the previous release may predate (its endpoint answers 404): skipped there, and the
 * matching snapshot field is then left out of the comparison.
 */
async function optional<T>(step: () => Promise<T>): Promise<T | null> {
  try {
    return await step();
  } catch (err) {
    if (err instanceof HttpError && err.status === 404) return null;
    throw err;
  }
}

function psql(sql: string): string {
  return capture("docker", ["compose", "-p", PROJECT, "-f", join(E2E_DIR, "docker-compose.yml"), "exec", "-T", "postgres",
    "psql", "-U", "postgres", "-d", "nocturne", "-tAc", sql]).trim();
}

const host = (slug: string) => `${slug}.${BASE_DOMAIN}`;

async function snapshot(slug: string, auth: { token?: string; secret?: string }): Promise<Snapshot> {
  const h = { host: host(slug), ...auth };
  const entries = await call<Array<{ date: number }>>("GET", "/api/v1/entries.json?count=5000", h);
  const treatments = await call<Array<{ eventType: string; created_at: string }>>("GET", "/api/v1/treatments.json?count=5000", h);
  const rules = await call<Array<{ name: string }>>("GET", "/api/v4/alert-rules", h);
  const share = await optional(() => call<NonNullable<Snapshot["share"]>>("GET", "/api/v4/share", h));
  const connector = await call<{ configuration: { url?: unknown } }>("GET", "/api/v4/connectors/config/nightscout", h);
  const labs = await optional(() => call<unknown[]>("GET", "/api/v4/lab-results/hba1c", h));
  return {
    entries: entries.map((e) => e.date).sort((a, b) => a - b),
    treatments: treatments.map((t) => `${t.eventType}@${Date.parse(t.created_at)}`).sort(),
    alertRules: rules.map((r) => r.name).sort(),
    share: share && { enabled: share.enabled, fullHistory: share.fullHistory, scopes: [...share.scopes].sort() },
    connectorUrl: connector.configuration.url,
    labResults: labs && labs.length,
  };
}

async function seedTenant(i: number): Promise<Omit<Seeded, "expected">> {
  const slug = `upgrade-${i}-${createHash("sha1").update(`${Date.now()}-${i}`).digest("hex").slice(0, 6)}`;
  const seeded = await call<{ accessToken: string }>("POST", "/api/v4/dev-only/admin/seed-tenant", {
    body: { slug, displayName: `Upgrade ${i}`, ownerUsername: `owner-${i}`, sampleData: true, sampleDataDays: 2 },
  });
  const h = { host: host(slug), token: seeded.accessToken };
  const now = Date.now();
  await call("POST", "/api/v1/entries", {
    ...h,
    body: Array.from({ length: 36 }, (_, k) => ({ type: "sgv", sgv: 90 + i * 10 + k, date: now - (k + 1) * 150_000 - 3_600_000, direction: "Flat", device: `upgrade-uploader-${i}` })),
  });
  await call("POST", "/api/v1/treatments", {
    ...h,
    body: [
      { eventType: "Meal Bolus", created_at: new Date(now - 2 * 3_600_000).toISOString(), insulin: 3 + i, carbs: 40 + i, enteredBy: "upgrade" },
      { eventType: "Note", created_at: new Date(now - 3 * 3_600_000).toISOString(), notes: `upgrade tenant ${i}`, enteredBy: "upgrade" },
    ],
  });
  await optional(() => call("POST", "/api/v4/lab-results/hba1c", { ...h, body: { measuredAt: new Date(now - 10 * 86_400_000).toISOString(), valuePercent: 6 + i / 10, note: "upgrade" } }));
  if (await optional(() => call("POST", "/api/v4/share/rotate", h)) && i % 2 === 0) {
    await call("PUT", "/api/v4/share/full-history", { ...h, body: { fullHistory: true } });
  }
  await call("PUT", "/api/v4/connectors/config/nightscout", { ...h, body: { url: "http://mocks:8080/nightscout", isActive: false } });
  await call("PUT", "/api/v4/connectors/config/nightscout/secrets", { ...h, body: { apiSecret: "e2e-fake-nightscout-secret" } });
  const grant = await call<{ token: string }>("POST", "/api/auth/direct-grants", { ...h, body: { label: "upgrade", scopes: ["*"] } });
  return { slug, accessToken: seeded.accessToken, apiToken: grant.token };
}

/** Drops the fields the previous release could not report, then compares. */
function assertSame(actual: Snapshot, expected: Snapshot, what: string) {
  const known = Object.fromEntries(Object.entries(actual).filter(([k]) => expected[k as keyof Snapshot] !== null));
  const before = Object.fromEntries(Object.entries(expected).filter(([, v]) => v !== null));
  assertEqual(known, before, what);
}

function assertEqual(actual: unknown, expected: unknown, what: string) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) throw new Error(`${what} changed across the upgrade:\n  before ${e.slice(0, 600)}\n  after  ${a.slice(0, 600)}`);
}

async function main() {
  const started = Date.now();
  const env = { ...ports, NOCTURNE_API_IMAGE: PREVIOUS, E2E_API_ENVIRONMENT: "Development" };

  if (process.env.E2E_SKIP_BUILD !== "1") await ensureImages();
  if ((await run("docker", ["pull", "--quiet", PREVIOUS])) !== 0) {
    console.warn(`[upgrade] could not pull ${PREVIOUS}; using the local copy if there is one`);
  }

  await compose(PROJECT, ["down", "--volumes", "--remove-orphans", "--timeout", "5"], env);
  try {
    // 1. The previous release on a fresh database. It predates NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS,
    // so it runs as Development, the only way it exposes the seed endpoints.
    console.log(`[upgrade] starting ${PREVIOUS}`);
    if ((await compose(PROJECT, ["up", "-d", "--wait", "--wait-timeout", "300", "api"], env)) !== 0) {
      throw new Error(`${PREVIOUS} did not become healthy on a fresh database`);
    }
    const migrationsBefore = Number(psql(`SELECT count(*) FROM "__EFMigrationsHistory"`));
    console.log(`[upgrade] previous release applied ${migrationsBefore} migrations; seeding ${TENANTS} tenants`);

    const tenants: Seeded[] = [];
    for (let i = 0; i < TENANTS; i++) {
      const t = await seedTenant(i);
      tenants.push({ ...t, expected: await snapshot(t.slug, { token: t.accessToken }) });
      console.log(`[upgrade]   ${t.slug}: ${tenants.at(-1)!.expected.entries.length} entries, ${tenants.at(-1)!.expected.treatments.length} treatments`);
    }

    // 2. Swap the API image in place; Postgres and its data directory stay up untouched.
    console.log("[upgrade] swapping to nocturne-api:e2e (this checkout)");
    const swapped = Date.now();
    const code = await compose(PROJECT, ["up", "-d", "--wait", "--wait-timeout", "300", "--no-deps", "api"], {
      ...env,
      NOCTURNE_API_IMAGE: "nocturne-api:e2e",
      E2E_API_ENVIRONMENT: "Production",
    });
    if (code !== 0) throw new Error("the API built from this checkout did not become healthy on the upgraded database");
    const migrationsAfter = Number(psql(`SELECT count(*) FROM "__EFMigrationsHistory"`));
    console.log(`[upgrade] healthy after ${Math.round((Date.now() - swapped) / 1000)}s; ${migrationsAfter - migrationsBefore} new migrations applied (${migrationsAfter} total)`);
    if (migrationsAfter < migrationsBefore) throw new Error("migration history shrank across the upgrade");

    // 3. Everything written by the previous release reads back the same, through a credential
    // it issued (the api-secret survives) and through a session the new API issues.
    for (const t of tenants) {
      assertSame(await snapshot(t.slug, { secret: t.apiToken }), t.expected, `${t.slug} (api-secret)`);
      const login = await call<{ accessToken: string }>("POST", "/api/v4/dev-only/auth/login", { host: host(t.slug), body: { tenant: t.slug } });
      assertSame(await snapshot(t.slug, { token: login.accessToken }), t.expected, `${t.slug} (new session)`);
    }
    console.log(`[upgrade] all ${TENANTS} tenants read back unchanged; total ${Math.round((Date.now() - started) / 1000)}s`);
  } catch (err) {
    const logs = capture("docker", ["compose", "-p", PROJECT, "-f", join(E2E_DIR, "docker-compose.yml"), "logs", "--no-color", "--timestamps"]);
    writeFileSync(join(E2E_DIR, "docker-compose-logs.txt"), logs);
    throw err;
  } finally {
    if (process.env.E2E_KEEP_STACK !== "1") await compose(PROJECT, ["down", "--volumes", "--remove-orphans", "--timeout", "5"], env);
  }
}

main().catch((err) => {
  console.error(`[upgrade] FAILED: ${err instanceof Error ? err.message : err}`);
  process.exit(1);
});

