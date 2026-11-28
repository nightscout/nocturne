import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import DemoBanner from "./DemoBanner.svelte";

const IN_TWENTY_MINUTES = new Date(
  Date.now() + 20 * 60_000 + 30_000
).toISOString();
const IN_TWO_HOURS = new Date(
  Date.now() + 2 * 60 * 60_000 + 30_000
).toISOString();

describe("DemoBanner", () => {
  it("names the personal persona and links to the clinic view through the session endpoint", async () => {
    render(DemoBanner, {
      props: {
        persona: "personal",
        nextResetAt: IN_TWO_HOURS,
        nextVisitorResetAt: null,
      },
    });

    await expect
      .element(page.getByText("Demo, synthetic data", { exact: true }))
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "See it as a clinic" }))
      .toHaveAttribute(
        "href",
        "/api/v4/demo/session?as=clinic&redirect=/people"
      );
    await expect.element(page.getByText("Resets in 2h 0m")).toBeVisible();
  });

  it("names the clinic persona and links to the personal view through the session endpoint", async () => {
    render(DemoBanner, {
      props: {
        persona: "clinic",
        nextResetAt: IN_TWO_HOURS,
        nextVisitorResetAt: IN_TWENTY_MINUTES,
      },
    });

    await expect
      .element(
        page.getByText("Demo, Demotown Diabetes Clinic, synthetic data", {
          exact: true,
        })
      )
      .toBeVisible();
    await expect
      .element(page.getByRole("link", { name: "See it as a person" }))
      .toHaveAttribute("href", "/api/v4/demo/session?redirect=/");
    await expect.element(page.getByText("Resets in 20m")).toBeVisible();
  });

  it("hides the clinic countdown when the visitor reset is not scheduled", async () => {
    render(DemoBanner, {
      props: {
        persona: "clinic",
        nextResetAt: IN_TWO_HOURS,
        nextVisitorResetAt: null,
      },
    });

    await expect
      .element(page.getByText("Demo, Demotown Diabetes Clinic, synthetic data"))
      .toBeVisible();
    expect(page.getByText(/Resets in/).elements()).toHaveLength(0);
  });

  it("offers no other persona on a site that cannot sign one in", async () => {
    render(DemoBanner, {
      props: {
        persona: "clinic",
        nextResetAt: null,
        nextVisitorResetAt: null,
        canSwitch: false,
      },
    });

    await expect
      .element(page.getByText("Demo, Demotown Diabetes Clinic, synthetic data"))
      .toBeVisible();
    expect(page.getByRole("link").elements()).toHaveLength(0);
  });
});
