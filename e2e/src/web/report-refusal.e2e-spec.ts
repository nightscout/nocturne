import { expect, signIn, test } from "./fixtures.ts";

test.describe("report refusals", () => {
  test("show the API's reason for refusing a range past its cap", async ({ page, seed }) => {
    const tenant = await seed();
    await signIn(page, tenant);

    // Two years: past the actogram read's cap, so the API refuses the range rather than trimming it.
    await page.goto(`${tenant.webUrl}/reports/steps?from=2024-01-01&to=2025-12-31`);

    await expect(page.locator("main")).toContainText(/Date range must not exceed \d+ days\./);
  });
});
