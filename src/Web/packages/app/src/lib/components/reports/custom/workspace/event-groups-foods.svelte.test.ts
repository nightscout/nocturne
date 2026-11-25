import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import EventGroups from "./EventGroups.svelte";
import { liveCatalogue } from "./catalogue.fixtures.js";
import { starter } from "./starters.js";
import { Workspace } from "./workspace.svelte.js";

function foodsGroup(names: string[]): Workspace {
  const ws = new Workspace(liveCatalogue, starter("one-food")!);
  ws.updateMoment(0, { foods: names.map((name, i) => ({ id: `food-${i}`, name })) });
  return ws;
}

describe("the foods a group is narrowed to", () => {
  it("reads as a list with the last word set apart", async () => {
    render(EventGroups, { ws: foodsGroup(["Apple", "Banana", "Cherry"]), report: null });

    await expect.element(page.getByText("Apple, Banana, or Cherry").first()).toBeInTheDocument();
  });

  it("reads as two foods without a comma", async () => {
    render(EventGroups, { ws: foodsGroup(["Apple", "Banana"]), report: null });

    await expect.element(page.getByText("Apple or Banana").first()).toBeInTheDocument();
  });
});
