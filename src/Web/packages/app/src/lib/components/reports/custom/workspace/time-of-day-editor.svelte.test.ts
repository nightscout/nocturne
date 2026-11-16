import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { minuteLabel } from "./clock.js";
import TimeOfDayEditor from "./TimeOfDayEditor.svelte";

function editor(from: number, to: number) {
  const changes: [number, number][] = [];
  const onclear = vi.fn();
  render(TimeOfDayEditor, { id: "t", from, to, onchange: (f: number, t: number) => changes.push([f, t]), onclear });
  return { changes, onclear };
}

describe("the time of day of a group of events", () => {
  it("sets a part of the day that runs past midnight in one step", async () => {
    const { changes } = editor(360, 600);
    await page.getByRole("radio", { name: "Overnight" }).click();

    expect(changes).toEqual([[1320, 360]]);
  });

  it("says a window that runs past midnight, and marks its part of the day", async () => {
    editor(1320, 360);

    await expect.element(page.getByTestId("time-of-day-window")).toHaveTextContent(`${minuteLabel(1320)} to ${minuteLabel(360)}, past midnight`);
    await expect.element(page.getByRole("radio", { name: "Overnight" })).toHaveAttribute("aria-checked", "true");
  });

  it("sets an end before the start, and cannot set the end to the start", async () => {
    const { changes } = editor(1320, 1380);
    await page.getByLabelText("End", { exact: true }).click();

    await expect.element(page.getByRole("option", { name: minuteLabel(1320), exact: true })).toHaveAttribute("data-disabled");
    await page.getByRole("option", { name: minuteLabel(360), exact: true }).click();
    expect(changes).toEqual([[1320, 360]]);
  });

  it("keeps the start from the end at midnight, the collapse the engine refuses", async () => {
    const { changes } = editor(1080, 0);
    await page.getByLabelText("Start", { exact: true }).click();

    await expect.element(page.getByRole("option", { name: minuteLabel(0), exact: true })).toHaveAttribute("data-disabled");
    expect(changes).toEqual([]);
  });

  it("removes the narrowing for the full day", async () => {
    const { onclear } = editor(360, 600);
    await page.getByRole("radio", { name: "Full day" }).click();

    expect(onclear).toHaveBeenCalledOnce();
  });

  it("moves through the parts of the day with the arrow keys", async () => {
    const { changes } = editor(360, 720);
    page.getByRole("radio", { name: "Morning" }).element().focus();
    await userEvent.keyboard("{ArrowRight}");
    await userEvent.keyboard("{Enter}");

    expect(changes.at(-1)).toEqual([720, 1080]);
  });

  it("gives each control a 44px target on a phone", async () => {
    await page.viewport(390, 844);
    editor(360, 600);

    expect(page.getByRole("radio", { name: "Evening" }).element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
    expect(page.getByLabelText("Start", { exact: true }).element().getBoundingClientRect().height).toBeGreaterThanOrEqual(44);
  });
});
