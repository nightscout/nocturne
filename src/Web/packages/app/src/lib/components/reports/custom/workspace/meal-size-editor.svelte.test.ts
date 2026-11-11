import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import MealSizeEditor from "./MealSizeEditor.svelte";

describe("a range of one value", () => {
  it("keeps a number typed with a decimal point while it is still being typed", async () => {
    const onchange = vi.fn();
    render(MealSizeEditor, { id: "g", min: undefined, max: undefined, onchange, step: 0.1, unit: "U", lowest: 0, highest: 30 });
    const from = page.getByLabelText("From");

    await userEvent.type(from, "2.5");

    expect(onchange).not.toHaveBeenCalledWith({ min: undefined, max: undefined });
    expect(onchange).toHaveBeenLastCalledWith({ min: 2.5, max: undefined });
    await expect.element(from).toHaveValue(2.5);
  });

  it("keeps a value outside the limits until the field is left, then brings it within them", async () => {
    const onchange = vi.fn();
    render(MealSizeEditor, { id: "g", min: undefined, max: undefined, onchange, lowest: 20, highest: 200 });
    const from = page.getByLabelText("From");

    await userEvent.type(from, "2");
    expect(onchange).not.toHaveBeenCalled();
    await userEvent.type(from, "5");
    expect(onchange).toHaveBeenLastCalledWith({ min: 25, max: undefined });
    await userEvent.clear(from);
    await userEvent.type(from, "5");
    await userEvent.tab();

    expect(onchange).toHaveBeenLastCalledWith({ min: 20, max: undefined });
  });
});
