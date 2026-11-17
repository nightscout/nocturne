import { afterEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { Food } from "#lib/api/index.js";

const { foods, query } = vi.hoisted(() => ({
  query: <T>(value: T) => Object.assign(Promise.resolve(value), { refresh: async () => {} }),
  foods: Array.from({ length: 60 }, (_, i) => ({
    _id: `f${i}`,
    name: `Food ${i}`,
    category: "Fruit",
    carbs: i,
    portion: 100,
    unit: "g",
  })) as Food[],
}));

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: vi.fn(() => query(foods)),
  getFavorites: vi.fn(() => query([])),
  createFood: vi.fn(),
  updateFood: vi.fn(),
  addFavorite: vi.fn(),
  removeFavorite: vi.fn(),
  getFoodAttributionCount: vi.fn(),
}));
vi.mock("./data.remote", () => ({ deleteFood: vi.fn() }));

import FoodPage from "./+page.svelte";

const headerTop = () =>
  page.getByRole("button", { name: "Name" }).element().closest(".header-row")!.getBoundingClientRect().top;

describe("food page list header", () => {
  afterEach(() => {
    window.scrollTo(0, 0);
    document.documentElement.style.removeProperty("--app-sticky-top");
  });

  it("stays at the top of the viewport while the list scrolls", async () => {
    render(FoodPage);
    await expect.element(page.getByText("Food 59")).toBeInTheDocument();
    expect(headerTop()).toBeGreaterThan(0);

    window.scrollTo(0, document.documentElement.scrollHeight);
    await new Promise(requestAnimationFrame);

    expect(window.scrollY).toBeGreaterThan(0);
    expect(headerTop()).toBeCloseTo(0, 0);
  });

  it("sticks below the app chrome above it", async () => {
    document.documentElement.style.setProperty("--app-sticky-top", "48px");
    render(FoodPage);
    await expect.element(page.getByText("Food 59")).toBeInTheDocument();

    window.scrollTo(0, document.documentElement.scrollHeight);
    await new Promise((resolve) => setTimeout(resolve, 350));

    expect(headerTop()).toBeCloseTo(48, 0);
  });
});
