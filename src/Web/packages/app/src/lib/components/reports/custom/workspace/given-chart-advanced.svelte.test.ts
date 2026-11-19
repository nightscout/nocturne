import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { liveCatalogue } from "./catalogue.fixtures.js";
import GivenChartAdvanced from "./GivenChartAdvanced.svelte";
import { Report } from "./report.svelte.js";
import specs from "./specs.fixture.json";

function open() {
  const report = Report.fromSpec(liveCatalogue, specs["template glucose"])!;
  const block = report.blocks[0];
  if (block.kind !== "given") throw new Error("The glucose template opens as a given chart.");
  return { report, block };
}

describe("a calculated line of a given chart", () => {
  it("takes the first signal until the member chooses another", async () => {
    const { report, block } = open();
    render(GivenChartAdvanced, { report, block });
    await page.getByTestId("block-advanced").click();

    const add = page.getByTestId("add-calculated");
    await expect.element(add.getByLabelText("From")).toHaveTextContent("Delivered basal");
    await expect.element(add.getByRole("button", { name: "Add the line" })).toBeEnabled();
  });

  it("forgets a chosen signal that left the chart, and adds no line until another is chosen", async () => {
    const { report, block } = open();
    render(GivenChartAdvanced, { report, block });
    await page.getByTestId("block-advanced").click();
    const add = page.getByTestId("add-calculated");

    await add.getByLabelText("From").click();
    await page.getByRole("option", { name: "IOB" }).click();
    await expect.element(add.getByLabelText("From")).toHaveTextContent("IOB");

    const iob = report.givenLayers(block).find((l) => l.id === "iob")!;
    expect(report.removeGivenLayer(block, iob)).toBeNull();

    await expect.element(add.getByLabelText("From")).toHaveTextContent("Choose a signal");
    await expect.element(add.getByRole("button", { name: "Add the line" })).toBeDisabled();
    await expect.element(add.getByText("Choose the signal to calculate from first.")).toBeVisible();
  });
});
