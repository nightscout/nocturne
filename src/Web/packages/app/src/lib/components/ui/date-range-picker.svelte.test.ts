import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getLocalTimeZone, today } from "@internationalized/date";

const url = vi.hoisted(() => ({
  params: undefined as unknown as { days?: number; from?: string; to?: string },
}));

vi.mock("sveltekit-search-params", async () => {
  const { reactiveFields } = await import("#lib/test-stubs/reactive-fields.svelte.js");
  url.params = reactiveFields<{ days?: number; from?: string; to?: string }>({
    days: undefined,
    from: undefined,
    to: undefined,
  });
  return { queryParameters: () => url.params };
});

vi.mock("#lib/api/generated/metadatas.generated.remote.js", () => ({
  getV4ApiLimits: () => ({ current: undefined }),
}));

import DateRangePicker from "./date-range-picker.svelte";

const segment = (name: RegExp) => page.getByRole("radio", { name });

describe("DateRangePicker presets", () => {
  beforeEach(() => {
    url.params.days = 7;
    url.params.from = "2020-01-01";
    url.params.to = "2020-01-07";
  });

  it("names each segment by its visible label", async () => {
    render(DateRangePicker);

    await expect.element(segment(/^7d\s*, last 7 days$/)).toBeInTheDocument();
    await expect.element(segment(/^1d\s*, today$/)).toBeInTheDocument();
  });

  it("keeps the selected segment and starts the range again from today when it is clicked again", async () => {
    render(DateRangePicker);
    await expect.element(segment(/^7d/)).toHaveAttribute("data-state", "on");

    await segment(/^7d/).click();

    await expect.element(segment(/^7d/)).toHaveAttribute("data-state", "on");
    const end = today(getLocalTimeZone());
    expect(url.params.days).toBe(7);
    expect(url.params.to).toBe(end.toString());
    expect(url.params.from).toBe(end.subtract({ days: 6 }).toString());
  });

  it("moves to another range when another segment is clicked", async () => {
    render(DateRangePicker);

    await segment(/^30d/).click();

    await expect.element(segment(/^30d/)).toHaveAttribute("data-state", "on");
    await expect.element(segment(/^7d/)).toHaveAttribute("data-state", "off");
    expect(url.params.days).toBe(30);
  });
});
