import { describe, expect, it } from "vitest";
import type { Food } from "#lib/api/index.js";
import { newFoodFrom } from "./food-constants";

describe("newFoodFrom", () => {
  const source: Food = {
    _id: "f1",
    type: "food",
    name: "Pasta bake",
    category: "Dinner",
    subcategory: "Pasta",
    portion: 300,
    unit: "g",
    carbs: 52,
    fat: 13,
    protein: 18,
    energy: 1674,
    gi: 2,
    hidden: true,
    hideafteruse: true,
    position: 7,
    foods: [{ name: "Cheese", carbs: 1, portion: 20, unit: "g", portions: 1 }],
    created_at: "2026-01-01T00:00:00Z",
    srvCreated: 1767225600000,
    srvModified: 1767225600000,
  };

  it("keeps what the food is and contains", () => {
    expect(newFoodFrom(source)).toEqual({
      type: "food",
      name: "Pasta bake",
      category: "Dinner",
      subcategory: "Pasta",
      portion: 300,
      unit: "g",
      carbs: 52,
      fat: 13,
      protein: 18,
      energy: 1674,
      gi: 2,
    });
  });

  it("drops the source's identity, visibility and quickpick list", () => {
    const copy = newFoodFrom(source);

    for (const field of [
      "_id",
      "hidden",
      "hideafteruse",
      "position",
      "foods",
      "created_at",
      "srvCreated",
      "srvModified",
    ]) {
      expect(copy).not.toHaveProperty(field);
    }
  });
});
