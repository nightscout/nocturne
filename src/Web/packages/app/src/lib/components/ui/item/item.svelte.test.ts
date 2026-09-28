import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { Item } from "$lib/components/ui/item";

const children = createRawSnippet(() => ({ render: () => "<span>Dexcom</span>" }));

describe("Item", () => {
  it("is a button when it has onclick", async () => {
    const onclick = vi.fn();
    render(Item, { onclick, children });

    await page.getByRole("button", { name: "Dexcom" }).click();

    expect(onclick).toHaveBeenCalledOnce();
  });

  it("is a link when it has href", async () => {
    render(Item, { href: "/connectors", children });

    await expect.element(page.getByRole("link", { name: "Dexcom" })).toHaveAttribute("href", "/connectors");
  });

  it("is static content with neither", async () => {
    render(Item, { children });

    await expect.element(page.getByText("Dexcom")).toBeVisible();
    expect(page.getByRole("button").elements()).toHaveLength(0);
  });

  it("disables its button", async () => {
    const onclick = vi.fn();
    render(Item, { onclick, disabled: true, children });

    await expect.element(page.getByRole("button", { name: "Dexcom" })).toBeDisabled();
  });
});
