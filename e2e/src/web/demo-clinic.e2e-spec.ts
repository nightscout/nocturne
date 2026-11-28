import { demoPeopleCount, demoWebUrl, provisionDemo } from "../helpers/demo.ts";
import { expect, test } from "./fixtures.ts";

const SESSION = `${demoWebUrl}/api/v4/demo/session`;

test.describe("demo clinic persona", () => {
  test.beforeAll(async () => {
    await provisionDemo();
  });

  test("the clinic link lands on People with the clinic's people and group tabs", async ({ page }) => {
    await page.goto(`${SESSION}?as=clinic&redirect=/people`);
    await expect(page).toHaveURL(`${demoWebUrl}/people`);
    await expect(page.getByTestId("demo-banner-persona")).toHaveText("Demo, Demotown Diabetes Clinic, synthetic data");

    await expect(page.getByTestId("person-row")).toHaveCount(demoPeopleCount, { timeout: 60_000 });
    for (const group of ["Everyone", "Paediatrics", "My list"]) {
      await expect(page.getByRole("tab", { name: group })).toBeVisible();
    }
  });

  test("the clinic persona can save a view", async ({ page }) => {
    await page.goto(`${SESSION}?as=clinic&redirect=/people`);
    await expect(page.getByTestId("person-row")).toHaveCount(demoPeopleCount, { timeout: 60_000 });

    await page.getByRole("button", { name: "Add a filter" }).click();
    await page.getByTestId("people-filter-kind").click();
    await page.getByRole("option", { name: "Flagged only", exact: true }).click();
    await page.getByRole("button", { name: "Add filter" }).click();

    const name = `Flagged ${Date.now()}`;
    await page.getByRole("button", { name: "Save as new view" }).click();
    await page.getByRole("dialog").getByRole("textbox", { name: "Name" }).fill(name);
    await page.getByRole("dialog").getByRole("button", { name: "Save as new view" }).click();

    await expect(page.getByRole("tab", { name })).toBeVisible();
  });

  test("the banner switches between the personas without a login page", async ({ page }) => {
    await page.goto(`${SESSION}?as=clinic&redirect=/people`);
    await expect(page.getByTestId("demo-banner-persona")).toHaveText("Demo, Demotown Diabetes Clinic, synthetic data");

    await page.getByTestId("demo-banner-switch").click();
    await expect(page).toHaveURL(`${demoWebUrl}/`);
    await expect(page.getByTestId("demo-banner-persona")).toHaveText("Demo, synthetic data");

    await page.getByTestId("demo-banner-switch").click();
    await expect(page).toHaveURL(`${demoWebUrl}/people`);
    await expect(page.getByTestId("demo-banner-persona")).toHaveText("Demo, Demotown Diabetes Clinic, synthetic data");
  });

  test("a bare visit to the demo host signs in the personal persona", async ({ page }) => {
    await page.goto(`${demoWebUrl}/`);
    await expect(page.getByTestId("demo-banner-persona")).toHaveText("Demo, synthetic data");
    await expect(page).not.toHaveURL(/\/auth\/login/);
  });
});
