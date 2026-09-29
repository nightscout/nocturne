import { render } from "vitest-browser-svelte";
import { describe, expect, it } from "vitest";
import type { TreatmentFood } from "$lib/api";
import CarbBreakdownBar from "./CarbBreakdownBar.svelte";

const food = (id: string, carbs: number) => ({ id, foodName: id, carbs }) as unknown as TreatmentFood;

const wash = (container: HTMLElement) => container.querySelector<HTMLElement>("[style*='--share']");
const share = (el: HTMLElement | null) => el?.style.getPropertyValue("--share");

describe("CarbBreakdownBar wash", () => {
  it("unveils the wash to the attributed share of the carbs", () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [food("oats", 10)] });

    expect(share(wash(container))).toBe("0.25");
  });

  it("paints no wash when nothing is attributed", () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [] });

    expect(wash(container)).toBeNull();
  });

  it("caps the share at the whole bar when foods exceed the total", () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 10, foods: [food("oats", 25)] });

    expect(share(wash(container))).toBe("1");
  });
});
