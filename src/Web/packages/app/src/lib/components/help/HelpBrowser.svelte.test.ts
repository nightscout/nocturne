import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it } from "vitest";
import { page as pageState } from "$app/state";
import Harness from "./HelpHarness.test.svelte";

beforeEach(() => {
  localStorage.removeItem("nocturne-help-visibility");
  pageState.url.pathname = "/reports/steps";
  pageState.url.search = "?range=week";
});

describe("documentation help", () => {
  it("opens current-page help, follows connector guides, searches, and closes without navigation", async () => {
    render(Harness);
    await page
      .getByRole("button", { name: "Open page help", exact: true })
      .click();
    await expect
      .element(page.getByRole("heading", { name: "Steps", exact: true }).last())
      .toBeVisible();
    await page
      .getByRole("button", { name: "Google Health", exact: true })
      .click();
    await expect
      .element(
        page.getByRole("heading", { name: "Google Health", exact: true })
      )
      .toBeVisible();
    await page
      .getByRole("textbox", { name: "Search all documentation" })
      .fill("missing");
    await expect
      .element(page.getByRole("heading", { name: "Search results" }))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: /^Steps This guide/ }))
      .toBeVisible();
    await page
      .getByRole("button", { name: "Close page help", exact: true })
      .click();
    await expect.element(page.getByRole("dialog")).not.toBeInTheDocument();
    expect(pageState.url.pathname + pageState.url.search).toBe(
      "/reports/steps?range=week"
    );
    await page
      .getByRole("button", { name: "Open page help", exact: true })
      .click();
    await expect
      .element(page.getByRole("textbox", { name: "Search all documentation" }))
      .toHaveValue("");
    await expect
      .element(page.getByRole("heading", { name: "Steps", exact: true }).last())
      .toBeVisible();
  });

  it("minimizes to an icon and persists hiding and restoring from settings", async () => {
    localStorage.setItem("nocturne-help-visibility", "bar");
    render(Harness);
    await page
      .getByRole("button", { name: "Minimize help to library icon" })
      .click();
    await expect
      .element(page.getByRole("button", { name: "Open help", exact: true }))
      .not.toBeInTheDocument();
    await page.getByRole("button", { name: "Open page help" }).click();
    await expect
      .element(page.getByRole("region", { name: "Page documentation" }))
      .toBeVisible();
    await page
      .getByRole("button", { name: "Close page help", exact: true })
      .click();
    await page.getByLabelText("Help visibility").click();
    await page.getByRole("option", { name: "Hidden", exact: true }).click();
    await expect
      .element(page.getByRole("button", { name: "Open page help" }))
      .not.toBeInTheDocument();
    expect(localStorage.getItem("nocturne-help-visibility")).toBe("hidden");
    await page.getByLabelText("Help visibility").click();
    await page
      .getByRole("option", { name: "Full help bar", exact: true })
      .click();
    await expect
      .element(
        page.getByRole("button", { name: "Open page help", exact: true })
      )
      .toBeVisible();
    expect(localStorage.getItem("nocturne-help-visibility")).toBe("bar");
  });

  it("restores a hidden preference on mount and accepts changes from another tab", async () => {
    localStorage.setItem("nocturne-help-visibility", "hidden");
    render(Harness);
    await expect
      .element(page.getByLabelText("Help visibility"))
      .toHaveTextContent("Hidden");
    window.dispatchEvent(
      new StorageEvent("storage", {
        key: "nocturne-help-visibility",
        newValue: "icon",
      })
    );
    await expect
      .element(page.getByRole("button", { name: "Open page help" }))
      .toBeVisible();
  });
});
