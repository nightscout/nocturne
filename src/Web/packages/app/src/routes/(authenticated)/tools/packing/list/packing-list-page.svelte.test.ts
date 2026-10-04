import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { encodeBase64Utf8 } from "$lib/utils";

vi.mock("$app/navigation", () => ({
  goto: vi.fn((url: string) => {
    pageState.url = new URL(url) as typeof pageState.url;
    return Promise.resolve();
  }),
}));

import PackingListPage from "./+page.svelte";

function listUrl(items: Array<{ c: string; l: string; q: number; p?: 1 }>) {
  const encoded = encodeURIComponent(encodeBase64Utf8(JSON.stringify(items)));
  return new URL(`http://localhost/tools/packing/list?d=${encoded}`) as typeof pageState.url;
}

const packed = (label: string) => page.getByRole("checkbox", { name: `Packed: ${label}` });

describe("packing list page", () => {
  beforeEach(() => {
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2 },
      { c: "Supplies", l: "Pen needles", q: 10 },
    ]);
  });

  it("keeps a packed item packed after the page is loaded again", async () => {
    const first = render(PackingListPage, {});
    await packed("Test strips").click();
    await expect.element(page.getByText("1/2 packed")).toBeVisible();
    first.unmount();

    render(PackingListPage, {});

    await expect.element(packed("Test strips")).toBeChecked();
    await expect.element(packed("Pen needles")).not.toBeChecked();
    await expect.element(page.getByText("1/2 packed")).toBeVisible();
  });

  it("agrees with the count on any packed flag a link carries", async () => {
    pageState.url = listUrl([
      { c: "Supplies", l: "Test strips", q: 2, p: true as unknown as 1 },
      { c: "Supplies", l: "Pen needles", q: 10 },
    ]);
    render(PackingListPage, {});

    await expect.element(packed("Test strips")).toBeChecked();
    await expect.element(page.getByText("1/2 packed")).toBeVisible();
  });

  it("keeps packed items with their own row when another item is removed", async () => {
    render(PackingListPage, {});
    await packed("Pen needles").click();

    await page.getByRole("button", { name: "Remove Test strips" }).click();

    await expect.element(packed("Pen needles")).toBeChecked();
    await expect.element(page.getByText("All packed")).toBeVisible();
  });

  it("says everything is packed with the suitcase alone, and no wash behind the title", async () => {
    const header = page.getByTestId("packing-header");
    render(PackingListPage, {});
    await expect.poll(() => header.element().querySelectorAll("canvas").length).toBe(1);

    await packed("Test strips").click();
    await packed("Pen needles").click();

    await expect.element(page.getByText("All packed")).toBeVisible();
    expect(header.element().querySelectorAll("canvas")).toHaveLength(1);
  });

  it("pins the header so the suitcase stays in view while ticking", async () => {
    render(PackingListPage, {});

    const header = page.getByTestId("packing-header").element();
    expect(getComputedStyle(header).position).toBe("sticky");
    expect(header.className).toContain("top-(--app-sticky-top,0px)");
    expect(header.className).toContain("lg:top-[calc(var(--app-sticky-top,0px)_+_1.5rem)]");
  });
});
