import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import PaintedMoment from "./PaintedMoment.svelte";

const moment = () => page.getByTestId("painted-moment");

describe("PaintedMoment", () => {
  it("stays out of sight on mount, whatever the count", async () => {
    render(PaintedMoment, { count: 3, artwork: "apple" });

    await expect.element(moment()).not.toBeInTheDocument();
  });

  it("plays when the count rises, then leaves", async () => {
    const screen = render(PaintedMoment, { count: 0, artwork: "apple", holdMs: 300 });
    await screen.rerender({ count: 1, artwork: "apple", holdMs: 300 });

    await expect.element(moment()).toBeInTheDocument();
    await expect.poll(() => moment().query(), { timeout: 2000 }).toBeNull();
  });

  it("does not play when the count falls", async () => {
    const screen = render(PaintedMoment, { count: 2, artwork: "linked-rings" });
    await screen.rerender({ count: 0, artwork: "linked-rings" });

    await expect.element(moment()).not.toBeInTheDocument();
  });
});
