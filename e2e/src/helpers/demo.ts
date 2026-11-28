import { env } from "./env.ts";
import { ApiClient } from "./http.ts";
import { instanceKeyHeaders } from "./instance-key.ts";

/** Slug of the demo host, which the admin provisioning endpoint fixes. */
export const demoSlug = "demo";

/** The `Key` of each person in `DemoClinic.People`, which the e2e cannot import; the demo host's own person has none. */
export const demoPeopleKeys = ["john", "katie", "lena", "tom", "rory", "paula", "liam", "maddie", "sam", "gus"];

/** Everyone the clinic follows: its synthetic people and the demo host's own person. */
export const demoPeopleCount = demoPeopleKeys.length + 1;

export const demoWebUrl = env.tenantWebUrl(demoSlug);

async function admin(path: string, body?: unknown): Promise<void> {
  const response = await new ApiClient().request("POST", `/api/v4/admin/demo/${path}`, body ?? {}, instanceKeyHeaders);
  if (response.status < 200 || response.status >= 300) {
    throw new Error(`POST /api/v4/admin/demo/${path} -> HTTP ${response.status}: ${response.text.slice(0, 500)}`);
  }
}

/**
 * Provisions the demo host and the demo clinic, and seeds the host's person and each of the clinic's
 * people from their archetype, as the demo service does. Idempotent: one demo exists on a stack, and a
 * person who is already seeded is left as they are.
 */
export async function provisionDemo(): Promise<void> {
  await admin("provision");
  await admin("seed-extras", { days: 7 });
  for (const person of demoPeopleKeys) await admin("seed-extras", { days: 30, includeGlucose: true, person });
}
