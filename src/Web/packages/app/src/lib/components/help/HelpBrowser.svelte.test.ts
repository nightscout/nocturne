import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import Harness from "./HelpHarness.test.svelte";

beforeEach(() => {
  localStorage.removeItem("nocturne-help-visibility");
  localStorage.setItem("nocturne-help-layout", "page");
  pageState.url.pathname = "/reports/steps";
  pageState.url.search = "?range=week";
});

describe("documentation help", () => {
  it("restores focus to the book after opening from the help bar", async () => {
    localStorage.setItem("nocturne-help-layout", "right");
    localStorage.setItem("nocturne-help-visibility", "bar");
    render(Harness);
    await page.getByRole("button", { name: "Open help", exact: true }).click();
    await page
      .getByRole("button", { name: "Close documentation", exact: true })
      .click();
    await vi.waitFor(() =>
      expect(document.activeElement).toBe(
        page
          .getByRole("button", { name: "Open page help", exact: true })
          .element()
      )
    );
  });

  it("keeps focus in blocking help while allowing the book to close it", async () => {
    localStorage.setItem("nocturne-help-layout", "right");
    render(Harness);
    await page
      .getByRole("button", { name: "Open page help", exact: true })
      .click();
    await page
      .getByRole("textbox", { name: "Search all documentation" })
      .fill("steps");
    const input = document.querySelector<HTMLInputElement>(
      '[aria-label="Report notes"]'
    )!;
    input.focus();
    expect(document.activeElement).not.toBe(input);
    expect(input.closest("[inert]")).not.toBeNull();
    for (let index = 0; index < 15; index++) {
      window.dispatchEvent(
        new KeyboardEvent("keydown", { key: "Tab", cancelable: true })
      );
      expect(
        document.activeElement?.closest("[data-help-panel], [data-help-toggle]")
      ).not.toBeNull();
    }
    await page
      .getByRole("button", { name: "Close page help", exact: true })
      .click();
    await expect
      .element(page.getByRole("textbox", { name: "Report notes" }))
      .toBeVisible();
  });

  it("reserves room for the returning mobile header when opened from the bar", async () => {
    await page.viewport(390, 844);
    localStorage.setItem("nocturne-help-layout", "drawer");
    localStorage.setItem("nocturne-help-visibility", "bar");
    render(Harness, { hiddenMobileHeader: true });
    await page.getByRole("button", { name: "Open help", exact: true }).click();
    const panel = page.getByRole("region", { name: "Documentation panel" });
    await vi.waitFor(() => {
      const book = page
        .getByRole("button", { name: "Close page help", exact: true })
        .element()
        .getBoundingClientRect();
      expect(
        panel.element().getBoundingClientRect().top
      ).toBeGreaterThanOrEqual(book.bottom);
    });
    await page
      .getByRole("button", { name: "Close page help", exact: true })
      .click();
    await page.viewport(1280, 900);
  });
  it("keeps navigation, the guide and the close book reachable on a phone", async () => {
    await page.viewport(390, 844);
    localStorage.setItem("nocturne-help-layout", "drawer");
    render(Harness);
    await page
      .getByRole("button", { name: "Open page help", exact: true })
      .click();
    await expect
      .element(
        page.getByRole("navigation", { name: "Documentation navigation" })
      )
      .toBeVisible();
    await expect
      .element(page.getByRole("textbox", { name: "Search all documentation" }))
      .toBeVisible();
    await vi.waitFor(
      () => {
        const bounds = page
          .getByRole("region", { name: "Documentation panel" })
          .element()
          .getBoundingClientRect();
        expect(bounds.left).toBeGreaterThanOrEqual(0);
        expect(bounds.right).toBeLessThanOrEqual(390);
      },
      { timeout: 4000 }
    );
    await page
      .getByRole("button", { name: "Close page help", exact: true })
      .click();
    await expect
      .element(page.getByRole("textbox", { name: "Report notes" }))
      .toBeVisible();
    await page.viewport(1280, 900);
  });
  it.each(["drawer", "right", "alongside"])(
    "keeps the page and its inputs intact with the %s layout",
    async (layout) => {
      localStorage.setItem("nocturne-help-layout", layout);
      render(Harness);
      await page
        .getByRole("textbox", { name: "Report notes" })
        .fill("Keep my work");
      await page
        .getByRole("button", { name: "Open page help", exact: true })
        .click();
      await expect
        .element(page.getByRole("region", { name: "Documentation panel" }))
        .toBeVisible();
      if (layout !== "drawer")
        await page.getByText("Browse documentation", { exact: true }).click();
      await page
        .getByRole("navigation", { name: "Documentation navigation" })
        .getByRole("button", { name: "Heart Rate", exact: true })
        .click();
      await expect
        .element(page.getByRole("heading", { name: "Heart Rate", exact: true }))
        .toBeVisible();
      expect(pageState.url.pathname + pageState.url.search).toBe(
        "/reports/steps?range=week"
      );
      await page
        .getByRole("button", { name: "Close documentation", exact: true })
        .click();
      await expect
        .element(page.getByRole("textbox", { name: "Report notes" }))
        .toHaveValue("Keep my work");
      await expect
        .element(
          page.getByRole("button", { name: "Open page help", exact: true })
        )
        .toBeVisible();
    }
  );

  it("persists layout selection and uses the navigation drawer by default", async () => {
    localStorage.removeItem("nocturne-help-layout");
    render(Harness);
    await expect
      .element(page.getByLabelText("Documentation layout"))
      .toHaveTextContent("Navigation drawer");
    await page.getByLabelText("Documentation layout").click();
    await page
      .getByRole("option", { name: "Right drawer", exact: true })
      .click();
    expect(localStorage.getItem("nocturne-help-layout")).toBe("right");
    await page
      .getByRole("button", { name: "Open page help", exact: true })
      .click();
    await expect
      .element(page.getByRole("region", { name: "Documentation panel" }))
      .toBeVisible();
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    await expect
      .element(page.getByLabelText("Documentation layout"))
      .toHaveTextContent("Right drawer");
  });
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
