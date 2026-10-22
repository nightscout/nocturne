import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import type { Food } from "$api";
import Composer from "./Composer.svelte";
import InlineEditor from "./InlineEditor.svelte";
import FoodStateHost from "$lib/test-stubs/FoodStateHost.test-stub.svelte";

const food: Food = {
  _id: "f1",
  name: "Oats",
  carbs: 30,
  portion: 100,
  unit: "g",
  gi: 2,
  type: "food",
};

const textFields = [
  /^Name/,
  /^Carbs/,
  /^Portion/,
  /^Fat/,
  /^Protein/,
  /^Energy/,
];
const selectFields = [/^Category$/, /^Subcategory$/];
const groupFields = [/^Unit$/, /^Glycemic Index$/];

async function expectAllFields() {
  for (const label of textFields) {
    await expect.element(page.getByLabelText(label)).toBeVisible();
  }
  for (const label of selectFields) {
    await expect.element(page.getByLabelText(label)).toBeVisible();
  }
  for (const label of groupFields) {
    await expect
      .element(page.getByRole("group", { name: label }))
      .toBeVisible();
  }
}

function overflowingElements(form: Element) {
  const right = form.getBoundingClientRect().right;
  return [...form.querySelectorAll("*")].filter(
    (el) => el.getBoundingClientRect().right > right + 0.5
  );
}

function crushedToggles(form: Element) {
  const minimumGroupWidth = { Unit: 200, "Glycemic Index": 180 };
  return [...form.querySelectorAll("[data-toggle-group-root]")].flatMap(
    (group) => {
      const box = group.getBoundingClientRect();
      const label = document.getElementById(
        group.getAttribute("aria-labelledby") ?? ""
      )?.textContent;
      const minimum =
        minimumGroupWidth[label?.trim() as keyof typeof minimumGroupWidth];
      const spilling = [
        ...group.querySelectorAll('[data-slot="toggle-group-item"]'),
      ].filter((item) => {
        const rect = item.getBoundingClientRect();
        return rect.left < box.left - 0.5 || rect.right > box.right + 0.5;
      });
      return box.width < minimum || spilling.length > 0 ? [group] : [];
    }
  );
}

describe("food form fields", () => {
  it("the composer renders every field and edits its draft", async () => {
    const onadd = vi.fn();
    render(FoodStateHost, {
      Form: Composer,
      formProps: { onadd, onclose: vi.fn() },
    });

    await page.getByTestId("food-composer-details").click();
    await expectAllFields();

    await page.getByLabelText(/^Name/).fill("Yogurt");
    await page.getByLabelText(/^Carbs/).fill("6");
    await page.getByLabelText(/^Fat/).fill("4");
    await page.getByRole("radio", { name: "Low" }).click();
    await page.getByLabelText(/^Category$/).click();
    await page.getByRole("option", { name: "Dairy" }).click();
    await page.getByRole("button", { name: "Save", exact: true }).click();

    expect(onadd).toHaveBeenCalledOnce();
    expect(onadd.mock.calls[0][0]).toMatchObject({
      name: "Yogurt",
      carbs: 6,
      fat: 4,
      gi: 1,
      category: "Dairy",
      portion: 100,
      unit: "g",
    });
  });

  it("the inline editor renders every field and edits its draft", async () => {
    const onsave = vi.fn();
    render(FoodStateHost, {
      Form: InlineEditor,
      formProps: { food, onsave, oncancel: vi.fn() },
    });

    await expectAllFields();

    await page.getByLabelText(/^Name/).fill("Rolled oats");
    await page.getByLabelText(/^Fat/).fill("7");
    await page.getByRole("radio", { name: "ml" }).click();
    await page.getByRole("radio", { name: "High" }).click();
    await page.getByLabelText(/^Category$/).click();
    await page.getByRole("option", { name: "Dairy" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onsave).toHaveBeenCalledOnce();
    expect(onsave.mock.calls[0][0]).toMatchObject({
      _id: "f1",
      name: "Rolled oats",
      fat: 7,
      carbs: 30,
      unit: "ml",
      gi: 3,
      category: "Dairy",
    });
  });
});

describe("food form layout", () => {
  // The shell leaves about 410px for the form at a 768px viewport (16rem sidebar).
  const containerWidths = [410, 600, 1000, 1300];

  for (const width of containerWidths) {
    it(`the composer fits a ${width}px container`, async () => {
      render(FoodStateHost, {
        Form: Composer,
        formProps: { onadd: vi.fn(), onclose: vi.fn() },
        width,
      });
      await page.getByTestId("food-composer-details").click();
      await expectAllFields();

      const form = page.getByTestId("food-composer").element();
      expect(overflowingElements(form)).toEqual([]);
      expect(crushedToggles(form)).toEqual([]);
    });

    it(`the inline editor fits a ${width}px container`, async () => {
      render(FoodStateHost, {
        Form: InlineEditor,
        formProps: { food, onsave: vi.fn(), oncancel: vi.fn() },
        width,
      });
      await expectAllFields();

      const form = document.querySelector("form")!;
      expect(overflowingElements(form)).toEqual([]);
      expect(crushedToggles(form)).toEqual([]);
    });
  }
});
