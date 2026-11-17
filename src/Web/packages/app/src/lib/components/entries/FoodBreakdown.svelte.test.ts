import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

let breakdownImpl: () => Promise<unknown>;

vi.mock("#lib/api/generated/nutritions.generated.remote.js", () => ({
  getCarbIntakeFoods: () => createQueryResource(() => breakdownImpl()),
  addCarbIntakeFood: () => Promise.resolve({}),
  deleteCarbIntakeFood: () => Promise.resolve({}),
  updateCarbIntakeFood: () => Promise.resolve({}),
}));

import FoodBreakdown from "./FoodBreakdown.svelte";
import { createQueryResource } from "#lib/test-stubs/remote-resource.js";

function rejection(status: number, message: string) {
  return Promise.reject({ status, body: { message } });
}

describe("FoodBreakdown", () => {
  it("shows the server's reason when the breakdown cannot load", async () => {
    breakdownImpl = () =>
      rejection(
        500,
        "The food database is being rebuilt. Try again in a minute."
      );

    render(FoodBreakdown, {
      props: { carbIntakeId: "carb-1", totalCarbs: 40 },
    });

    await expect
      .element(
        page.getByText(
          "The food database is being rebuilt. Try again in a minute."
        )
      )
      .toBeVisible();
  });

  it("keeps its own sentence when the client wrote the reason", async () => {
    breakdownImpl = () =>
      rejection(500, "An unexpected server error occurred.");

    render(FoodBreakdown, {
      props: { carbIntakeId: "carb-1", totalCarbs: 40 },
    });

    await expect
      .element(page.getByText("Could not load food breakdown."))
      .toBeVisible();
  });
});
