import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it } from "vitest";

import PathChoice from "./PathChoice.svelte";

describe("PathChoice", () => {
  it("announces the paths as a radio group named by its heading", async () => {
    render(PathChoice, { path: "fresh" });

    const group = page.getByRole("radiogroup", {
      name: /How are you arriving/,
    });
    await expect.element(group).toBeVisible();
    expect(group.getByRole("radio").elements()).toHaveLength(2);
    await expect
      .element(page.getByRole("radio", { name: /Start with a blank slate/ }))
      .toHaveAttribute("aria-checked", "true");
  });

  it("moves the selection with the arrow keys", async () => {
    render(PathChoice, { path: "fresh" });

    const fresh = page.getByRole("radio", { name: /Start with a blank slate/ });
    const migration = page.getByRole("radio", {
      name: /Migrate my Nightscout data/,
    });

    await fresh.click();
    await userEvent.keyboard("{ArrowDown}");

    await expect.element(migration).toHaveAttribute("aria-checked", "true");
    await expect.element(migration).toHaveFocus();
    await expect.element(fresh).toHaveAttribute("aria-checked", "false");

    await userEvent.keyboard("{ArrowUp}");

    await expect.element(fresh).toHaveAttribute("aria-checked", "true");
  });

  it("promises only steps the wizard has", async () => {
    render(PathChoice, { path: "fresh" });

    await expect.element(page.getByText(/target range/i)).not.toBeInTheDocument();
    await expect.element(page.getByText(/glucose units/i)).not.toBeInTheDocument();
  });
});
