import { beforeAll, describe, expect, it } from "vitest";
import { minutesAgo } from "../helpers/data.ts";
import { ApiClient } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface TreatmentLogStats {
  counts: { all: number; bolus: number; carbs: number; bgCheck: number; note: number; deviceEvent: number; basalInjection: number };
  treatmentSummary: unknown | null;
}

interface ProblemDocument {
  status: number;
  title?: string;
  detail?: string;
}

interface Role {
  id: string;
  slug: string;
}

const HOUR = 60 * 60 * 1000;

/** Joins `member`'s owner to `tenant` through an invite for `roleSlug`, and signs them in there. */
async function joinAs(tenant: Tenant, member: Tenant, roleSlug: string): Promise<ApiClient> {
  const roles = await tenant.api.ok<Role[]>("GET", "/api/v4/roles");
  const role = roles.find((r) => r.slug === roleSlug);
  expect(role, `role ${roleSlug}`).toBeDefined();
  const invite = await tenant.api.ok<{ token: string }>("POST", "/api/v4/member-invites", {
    roleIds: [role!.id],
    label: `e2e ${roleSlug}`,
    expiresInDays: 1,
    maxUses: 1,
  });

  const invitee = tenant.anonymous.with({ kind: "bearer", token: member.accessToken });
  await invitee.ok("POST", `/api/v4/member-invites/${encodeURIComponent(invite.token)}/accept`);

  const session = await new ApiClient().ok<{ accessToken: string }>("POST", "/api/v4/dev-only/auth/login", {
    tenant: tenant.slug,
    username: member.username,
  });
  return tenant.anonymous.with({ kind: "bearer", token: session.accessToken });
}

describe("Treatment Log stats", () => {
  let tenant: Tenant;
  let viewer: ApiClient;
  const from = new Date(Date.now() - 6 * HOUR).toISOString();
  const to = new Date(Date.now() + HOUR).toISOString();
  const stats = (client: ApiClient, query = "") =>
    client.ok<TreatmentLogStats>("GET", `/api/v4/treatment-log/stats?from=${from}&to=${to}&dayCount=1${query}`);

  beforeAll(async () => {
    const [owner, follower] = await Promise.all([seedTenant(), seedTenant()]);
    tenant = owner;
    await tenant.api.ok("POST", "/api/v1/treatments", [
      { eventType: "Meal Bolus", insulin: 3.5, carbs: 42, created_at: minutesAgo(120), enteredBy: "e2e" },
      { eventType: "Note", notes: "e2e log note", created_at: minutesAgo(90), enteredBy: "e2e" },
      { eventType: "BG Check", glucose: 104, glucoseType: "Finger", units: "mg/dl", created_at: minutesAgo(60), enteredBy: "e2e" },
      { eventType: "Site Change", created_at: minutesAgo(30), enteredBy: "e2e" },
    ]);
    viewer = await joinAs(tenant, follower, "viewer");
  });

  it("gives the owner every record kind and a treatment summary", async () => {
    const owner = await stats(tenant.api);
    expect(owner.counts).toMatchObject({ bolus: 1, carbs: 1, note: 1, bgCheck: 1, deviceEvent: 1 });
    expect(owner.counts.all).toBe(5);
    expect(owner.treatmentSummary).not.toBeNull();
  });

  it("filters to one kind", async () => {
    const boluses = await stats(tenant.api, "&category=Bolus");
    expect(boluses.counts.all).toBe(1);
    expect(boluses.counts.bolus).toBe(1);
  });

  it("gives a Viewer no treatment or device records, only the glucose checks its role reads", async () => {
    const seen = await stats(viewer);
    expect(seen.counts).toMatchObject({ bolus: 0, carbs: 0, note: 0, deviceEvent: 0, basalInjection: 0, bgCheck: 1 });
    expect(seen.counts.all).toBe(1);
    expect(seen.treatmentSummary).toBeNull();
  });

  it("refuses a Viewer the treatment list itself", async () => {
    expect((await viewer.get("/api/v1/treatments.json?count=5")).status).toBe(403);
  });
});

describe("analytics refusals", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("names why a range that ends before it starts is refused", async () => {
    const res = await tenant.api.get<ProblemDocument>(
      `/api/v4/treatment-log/stats?from=${new Date().toISOString()}&to=${new Date(Date.now() - HOUR).toISOString()}`,
    );
    expect(res.body).toMatchObject({ status: 400, title: "Bad Request", detail: "to must be after from." });
  });

  it("names the cap a range past it is refused for", async () => {
    const res = await tenant.api.get<ProblemDocument>(
      `/api/v4/treatment-log/stats?from=${new Date(Date.now() - 400 * 24 * HOUR).toISOString()}&to=${new Date().toISOString()}`,
    );
    expect(res.body).toMatchObject({ status: 400, title: "Bad Request" });
    expect(res.body.detail).toMatch(/^Date range must not exceed \d+ days\.$/);
  });
});
