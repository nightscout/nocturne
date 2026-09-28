import { postEntries, sgvSeries } from "../helpers/data.ts";
import { expect, test } from "./fixtures.ts";

test.describe("public share host", () => {
  test("renders the shared view to an anonymous visitor", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 24, valueAt: (i) => (i === 0 ? 163 : 120) }));
    const link = await tenant.api.ok<{ url: string }>("POST", "/api/v4/share/rotate");
    // The API reports links on https; the e2e stack serves plain http on the same host.
    const shareUrl = `http://${new URL(link.url).host}/`;

    const response = await page.goto(shareUrl);
    expect(response?.status()).toBe(200);
    await expect(page).toHaveURL(shareUrl);
    await expect(page.locator("body")).toContainText("163");
    expect(await page.context().cookies(shareUrl)).not.toContainEqual(expect.objectContaining({ name: ".Nocturne.AccessToken" }));
  });

  test("a disabled link no longer renders the data", async ({ page, seed }) => {
    const tenant = await seed();
    await postEntries(tenant.api, sgvSeries({ count: 6, valueAt: () => 171 }));
    const link = await tenant.api.ok<{ url: string }>("POST", "/api/v4/share/rotate");
    await tenant.api.ok("DELETE", "/api/v4/share");

    await page.goto(`http://${new URL(link.url).host}/`);
    await expect(page.locator("body")).not.toContainText("171");
  });
});
