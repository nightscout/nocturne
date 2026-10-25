import { test, expect, signIn } from "./fixtures.ts";

test("a tenant owner cannot open instance administration", async ({ page, seed }) => {
  const tenant = await seed();
  await signIn(page, tenant);
  await page.goto(`${tenant.webUrl}/settings/admin`);
  await expect(page).toHaveURL(`${tenant.webUrl}/settings`);
});
