import { test, expect, signIn } from "./fixtures.ts";
import { googleConsent, googleOptions, googleReadings, waitForGoogleSync } from "../helpers/google-health.ts";
import { env } from "../helpers/env.ts";
import type { Page } from "@playwright/test";
import type { Tenant } from "../helpers/tenant.ts";

async function connectGoogle(page: Page, seeded: Tenant) {
  const webUrl = env.tenantSecureWebUrl(seeded.slug);
  const tenant = { ...seeded, webUrl, loginLink: seeded.loginLink.replace(seeded.webUrl, webUrl) };
  const options = googleOptions(tenant);
  const authorizations: URL[] = [];
  // Only the external consent page is intercepted; callbacks and all Nocturne requests are real.
  await page.route("https://accounts.google.com/o/oauth2/v2/auth**", async (route) => {
    authorizations.push(new URL(route.request().url()));
    const callback = await googleConsent(route.request().url());
    expect(callback.origin).toBe(tenant.webUrl);
    await route.fulfill({ status: 302, headers: { location: callback.href }, body: "" });
  });
  await signIn(page, tenant);
  await page.goto(`${tenant.webUrl}/settings/connectors/google-health`);
  await page.getByLabel("Google client ID", { exact: true }).fill(options.clientId);
  await page.getByLabel("Client secret", { exact: true }).fill(options.clientSecret);
  await page.getByLabel("Callback URL", { exact: true }).fill(options.callbackUrl);
  await page.getByRole("button", { name: "Save and connect", exact: true }).click();
  await expect(page).toHaveURL(/google-health\?connection=connected$/);
  const connected = await tenant.api.ok<{ historyDays: number; importFrom: string | null }>("GET", "/api/v4/google-health");
  expect(connected.historyDays).toBe(7);
  expect(connected.importFrom).toBeNull();
  return { tenant, authorizations };
}

test("Google OAuth callback, preview, paginated import, repeat sync and disconnect", async ({ page, seed }) => {
  test.setTimeout(120_000);
  const { tenant } = await connectGoogle(page, await seed());
  await expect(page.getByRole("checkbox", { name: "Import Steps", exact: true })).toBeEnabled();
  expect(await googleReadings(tenant)).toEqual({ heart: [], steps: [], weight: [], sleep: [] });
  await page.getByRole("button", { name: "Save selection and import", exact: true }).click();
  const firstStatus = await waitForGoogleSync(tenant);
  const first = await googleReadings(tenant);
  for (const readings of Object.values(first)) expect(readings).toHaveLength(2);
  expect(first.heart.map((row) => row.bpm).sort()).toEqual([72, 73]);
  expect(first.steps.map((row) => row.metric).sort()).toEqual([123, 124]);
  expect(first.weight.map((row) => row.weightKg).sort()).toEqual([75, 75.1]);
  expect(first.sleep.every((session) => session.durationMs === 1_800_000)).toBe(true);
  for (const session of first.sleep) {
    const detail = await tenant.api.ok<{ stages: unknown[] }>("GET", `/api/v4/sleep/sessions/${session.id}`);
    expect(detail.stages).toHaveLength(1);
  }
  await page.reload();
  await expect(page.getByRole("button", { name: "Sync now", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Sync now", exact: true }).click();
  await waitForGoogleSync(tenant, firstStatus.lastSync);
  const repeated = await googleReadings(tenant);
  for (const key of ["heart", "steps", "weight"] as const) {
    expect(first[key].every((row) => typeof row._id === "string" && row._id.length > 0)).toBe(true);
    expect(repeated[key].map((row) => row._id).sort()).toEqual(first[key].map((row) => row._id).sort());
  }
  expect(repeated.sleep.map((row) => row.id).sort()).toEqual(first.sleep.map((row) => row.id).sort());
  await page.getByRole("button", { name: "Disconnect", exact: true }).click();
  await expect(page.getByRole("button", { name: "Sign in with Google", exact: true })).toBeVisible();
  expect((await tenant.api.ok<{ connected: boolean }>("GET", "/api/v4/google-health")).connected).toBe(false);
  for (const readings of Object.values(await googleReadings(tenant))) expect(readings).toHaveLength(2);
});

test("disconnect Google Health, sign in again and sync without losing or duplicating records", async ({ page, seed }) => {
  test.setTimeout(120_000);
  const { tenant, authorizations } = await connectGoogle(page, await seed());
  await expect(page.getByRole("checkbox", { name: "Import Steps", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Save selection and import", exact: true }).click();
  await waitForGoogleSync(tenant);
  const first = await googleReadings(tenant);
  for (const readings of Object.values(first)) expect(readings).toHaveLength(2);

  await page.getByRole("button", { name: "Disconnect", exact: true }).click();
  await expect(page.getByRole("button", { name: "Sign in with Google", exact: true })).toBeVisible();
  expect((await tenant.api.ok<{ connected: boolean }>("GET", "/api/v4/google-health")).connected).toBe(false);
  expect(await googleReadings(tenant)).toEqual(first);

  // Clear the old callback query so the URL assertion waits for the new OAuth round trip.
  await page.goto(`${tenant.webUrl}/settings/connectors/google-health`);
  await page.getByRole("button", { name: "Sign in with Google", exact: true }).click();
  await expect(page).toHaveURL(/google-health\?connection=connected$/);
  const connected = await tenant.api.ok<{ connected: boolean; lastSync: string | null }>("GET", "/api/v4/google-health");
  expect(connected.connected).toBe(true);
  expect(authorizations).toHaveLength(2);
  for (const parameter of ["state", "code_challenge"]) {
    expect(authorizations[0]!.searchParams.get(parameter)).toBeTruthy();
    expect(authorizations[1]!.searchParams.get(parameter)).toBeTruthy();
    expect(authorizations[1]!.searchParams.get(parameter)).not.toBe(authorizations[0]!.searchParams.get(parameter));
  }
  await expect(page.getByRole("button", { name: "Sync now", exact: true })).toBeEnabled();
  await page.getByRole("button", { name: "Sync now", exact: true }).click();
  await waitForGoogleSync(tenant, connected.lastSync);
  const reconnected = await googleReadings(tenant);
  for (const key of ["heart", "steps", "weight"] as const) {
    expect(reconnected[key].map((row) => row._id).sort()).toEqual(first[key].map((row) => row._id).sort());
  }
  expect(reconnected.sleep.map((row) => row.id).sort()).toEqual(first.sleep.map((row) => row.id).sort());
});
