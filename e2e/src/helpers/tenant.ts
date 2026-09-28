import { randomBytes } from "node:crypto";
import { env } from "./env.ts";
import { ApiClient } from "./http.ts";

export interface SeedOptions {
  /** Populate realistic sample history (glucose, treatments, alerts...). */
  sampleData?: boolean;
  sampleDataDays?: number;
  displayName?: string;
}

export interface Tenant {
  tenantId: string;
  subjectId: string;
  slug: string;
  username: string;
  accessToken: string;
  refreshToken: string;
  /** Host the tenant is addressed by. */
  host: string;
  /** Browser origin of the tenant. */
  webUrl: string;
  /** Visiting this in a browser signs in as the owner and lands on the dashboard. */
  loginLink: string;
  /** Owner-authenticated client. */
  api: ApiClient;
  /** The same tenant, no credentials. */
  anonymous: ApiClient;
}

interface SeedTenantResponse {
  tenantId: string;
  subjectId: string;
  accessToken: string;
  refreshToken: string;
}

/** A slug no other spec, worker or earlier run can hold: specs run in parallel. */
export function uniqueSlug(prefix = "e2e"): string {
  return `${prefix}-${randomBytes(4).toString("hex")}`;
}

/** Creates an isolated tenant with an owner and a live session through the dev-only seed API. */
export async function seedTenant(opts: SeedOptions = {}): Promise<Tenant> {
  const slug = uniqueSlug();
  const username = `owner-${slug}`;
  const apex = new ApiClient();
  const body = await apex.ok<SeedTenantResponse>("POST", "/api/v4/dev-only/admin/seed-tenant", {
    slug,
    displayName: opts.displayName ?? `E2E ${slug}`,
    ownerUsername: username,
    sampleData: opts.sampleData ?? false,
    sampleDataDays: opts.sampleDataDays ?? 7,
  });
  const host = env.tenantHost(slug);
  const webUrl = env.tenantWebUrl(slug);
  return {
    ...body,
    slug,
    username,
    host,
    webUrl,
    loginLink: `${webUrl}/api/v4/dev-only/auth/login?redirect=%2F`,
    api: new ApiClient({ host, credentials: { kind: "bearer", token: body.accessToken } }),
    anonymous: new ApiClient({ host }),
  };
}
