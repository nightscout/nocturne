import { test as base, expect, type Page } from "@playwright/test";
import { seedTenant, type SeedOptions, type Tenant } from "../helpers/tenant.ts";

/** Signs the page in through the tenant's dev login link and waits for the shell. */
export async function signIn(page: Page, tenant: Tenant): Promise<void> {
  await page.goto(tenant.loginLink);
  await expect(page).toHaveURL(`${tenant.webUrl}/`);
  await expect(page.locator('[data-slot="sidebar-wrapper"]')).toBeVisible();
}

export const test = base.extend<{ seed: (opts?: SeedOptions) => Promise<Tenant> }>({
  // eslint-disable-next-line no-empty-pattern
  seed: async ({}, use) => {
    await use((opts) => seedTenant(opts));
  },
});

export { expect };
