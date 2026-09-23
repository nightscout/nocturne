import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { createRawSnippet } from "svelte";
import { Badge } from "$lib/components/ui/badge";

const children = createRawSnippet(() => ({ render: () => "<span>Banana</span>" }));

describe("Badge", () => {
  it("names its remove button and calls onremove", async () => {
    const onremove = vi.fn();
    render(Badge, { onremove, removeLabel: "Remove Banana filter", children });

    await page.getByRole("button", { name: "Remove Banana filter" }).click();

    expect(onremove).toHaveBeenCalledOnce();
  });

  it("has no remove button without onremove", async () => {
    render(Badge, { children });

    await expect.element(page.getByText("Banana")).toBeVisible();
    expect(page.getByRole("button").elements()).toHaveLength(0);
  });
});
