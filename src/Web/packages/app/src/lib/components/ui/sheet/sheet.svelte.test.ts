import { render } from "vitest-browser-svelte";
import { afterEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import SheetHarness from "./SheetHarness.test.svelte";

describe("Sheet", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("logs no derived_inert warning when it is open as its parent unmounts", async () => {
    const warnings: string[] = [];
    const record = (...args: unknown[]) => warnings.push(args.map(String).join(" "));
    vi.spyOn(console, "warn").mockImplementation(record);
    vi.spyOn(console, "error").mockImplementation(record);

    const screen = await render(SheetHarness);
    await vi.waitFor(() => expect(document.querySelector("[data-slot=sheet-content]")).not.toBeNull());

    await screen.rerender({ mounted: false });
    flushSync();
    await new Promise((resolve) => setTimeout(resolve, 600));

    expect(warnings.filter((w) => w.includes("derived_inert"))).toEqual([]);
  });

  it("logs no derived_inert warning when the test tears down with the sheet open", async () => {
    const warnings: string[] = [];
    const record = (...args: unknown[]) => warnings.push(args.map(String).join(" "));
    vi.spyOn(console, "warn").mockImplementation(record);

    const screen = await render(SheetHarness);
    await vi.waitFor(() => expect(document.querySelector("[data-slot=sheet-content]")).not.toBeNull());

    screen.unmount();
    await new Promise((resolve) => setTimeout(resolve, 600));

    expect(warnings.filter((w) => w.includes("derived_inert"))).toEqual([]);
  });
});
