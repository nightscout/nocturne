import { expect, signIn, test } from "./fixtures.ts";

test.describe("sign-in and the authenticated shell", () => {
  test("the login link signs the owner in and lands on the dashboard", async ({ page, seed }) => {
    const tenant = await seed();
    await signIn(page, tenant);

    const cookies = await page.context().cookies(tenant.webUrl);
    const session = cookies.find((c) => c.name === ".Nocturne.AccessToken");
    expect(session?.secure).toBe(true);
    expect(session?.httpOnly).toBe(true);
  });

  test("an anonymous visitor is sent to sign in", async ({ page, seed }) => {
    const tenant = await seed();
    await page.goto(`${tenant.webUrl}/`);
    await expect(page).not.toHaveURL(`${tenant.webUrl}/`);
    await expect(page.locator('[data-slot="sidebar-wrapper"]')).toHaveCount(0);
  });

  // Every other suite exercises the app below the rendering layer, and `pnpm build` compiles it
  // without rendering a page, so a build can still throw on the first server render. This asks
  // the real server for a page behind the session with JavaScript off, so what arrives is the
  // server render alone, and refuses SvelteKit's error shell.
  test("the server renders the authenticated shell", async ({ browser, seed }) => {
    const tenant = await seed();
    const context = await browser.newContext({ javaScriptEnabled: false });
    const page = await context.newPage();

    const login = await page.goto(tenant.loginLink);
    expect(login?.status()).toBe(200);
    const response = await page.goto(`${tenant.webUrl}/`);
    expect(response?.status()).toBe(200);

    const html = await page.content();
    expect(html).not.toContain("An error occurred while processing your request");
    expect(html).toContain('data-slot="sidebar-wrapper"');
    await context.close();
  });
});
