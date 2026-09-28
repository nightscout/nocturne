import { createHash } from "node:crypto";
import { beforeAll, describe, expect, it } from "vitest";
import { ApiClient } from "../helpers/http.ts";
import { seedTenant, type Tenant } from "../helpers/tenant.ts";

interface DirectGrant {
  id: string;
  token: string;
  scopes: string[];
}

describe("tenant bootstrap and authentication", () => {
  let tenant: Tenant;

  beforeAll(async () => {
    tenant = await seedTenant();
  });

  it("seeds a tenant whose owner session authenticates against tenant-scoped endpoints", async () => {
    expect(tenant.tenantId).toMatch(/^[0-9a-f-]{36}$/);
    expect(tenant.subjectId).toMatch(/^[0-9a-f-]{36}$/);
    expect(tenant.accessToken).not.toBe("");

    const me = await tenant.api.get<Array<{ slug: string }>>("/api/v4/me/tenants");
    expect(me.status).toBe(200);
    expect(me.body.map((t) => t.slug)).toContain(tenant.slug);
  });

  it("rejects anonymous callers with 401", async () => {
    const me = await tenant.anonymous.get("/api/v4/me/tenants");
    expect(me.status).toBe(401);

    const glucose = await tenant.anonymous.get("/api/v4/glucose/sensor?limit=1");
    expect(glucose.status).toBe(401);
  });

  it("rejects a forged bearer token", async () => {
    const forged = tenant.api.with({ kind: "bearer", token: `${tenant.accessToken.slice(0, -4)}AAAA` });
    expect((await forged.get("/api/v4/me/tenants")).status).toBe(401);
  });

  describe("api-secret", () => {
    let grant: DirectGrant;

    beforeAll(async () => {
      grant = await tenant.api.ok<DirectGrant>("POST", "/api/auth/direct-grants", {
        label: "e2e uploader",
        scopes: ["glucose.readwrite"],
      });
    });

    it("mints a noc_ token with the requested scopes", () => {
      expect(grant.token).toMatch(/^noc_/);
      expect(grant.scopes).toContain("glucose.readwrite");
    });

    it("authenticates the token sent as api-secret", async () => {
      const uploader = tenant.anonymous.with({ kind: "api-secret", secret: grant.token });
      const res = await uploader.get("/api/v1/entries.json?count=1");
      expect(res.status).toBe(200);
    });

    it("authenticates the SHA-1 of the token, as legacy Nightscout clients send it", async () => {
      const sha1 = createHash("sha1").update(grant.token).digest("hex");
      const uploader = tenant.anonymous.with({ kind: "api-secret", secret: sha1 });
      const res = await uploader.get("/api/v1/entries.json?count=1");
      expect(res.status).toBe(200);
    });

    it("refuses a secret the tenant never issued", async () => {
      const wrong = tenant.anonymous.with({ kind: "api-secret", secret: createHash("sha1").update("nope").digest("hex") });
      expect((await wrong.get("/api/v1/entries.json?count=1")).status).toBe(401);
    });

    it("confines the token to its scopes", async () => {
      const uploader = tenant.anonymous.with({ kind: "api-secret", secret: grant.token });
      const treatments = await uploader.get("/api/v4/insulin/boluses?limit=1");
      expect(treatments.status).toBe(403);
    });

    it("stops authenticating once revoked", async () => {
      await tenant.api.ok("DELETE", `/api/auth/direct-grants/${grant.id}`);
      const uploader = tenant.anonymous.with({ kind: "api-secret", secret: grant.token });
      expect((await uploader.get("/api/v1/entries.json?count=1")).status).toBe(401);
    });
  });

  it("does not resolve a tenant that was never created", async () => {
    const ghost = new ApiClient({ host: `no-such-tenant-${Date.now()}.${tenant.host.split(".").slice(1).join(".")}` });
    const res = await ghost.get("/api/v1/status.json");
    expect(res.status).toBeGreaterThanOrEqual(400);
  });
});
