import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Food } from "$api";
import Composer from "$routes/(authenticated)/food/Composer.svelte";
import InlineEditor from "$routes/(authenticated)/food/InlineEditor.svelte";
import FoodStateHost from "$lib/test-stubs/FoodStateHost.test-stub.svelte";
import AddFoodDialog from "./AddFoodDialog.svelte";
import { energyUnit } from "$lib/stores/appearance-store.svelte";

const remote = vi.hoisted(() => ({
  createFood: vi.fn((food: Food) => Promise.resolve({ ...food, _id: "new" })),
  updateFood: vi.fn(({ request }: { request: Food }) => Promise.resolve(request)),
}));

vi.mock("$api/generated/foods.generated.remote", () => remote);

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
const selectFields = [/^Category$/];
const groupFields = [/^Unit$/, /^Glycemic index$/];

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
  const minimumGroupWidth = { Unit: 200, "Glycemic index": 180 };
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

describe("food energy", () => {
  afterEach(() => {
    energyUnit.current = null;
  });

  it("shows stored kilojoules in kilocalories and stores an entry as kilojoules", async () => {
    energyUnit.current = "kcal";
    const onsave = vi.fn();
    render(FoodStateHost, {
      Form: InlineEditor,
      formProps: { food: { ...food, energy: 1046 }, onsave, oncancel: vi.fn() },
    });

    const energy = page.getByLabelText(/^Energy/);
    await expect.element(energy).toHaveValue(250);
    await expect.element(page.getByText("kcal", { exact: true })).toBeVisible();

    await energy.fill("100");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onsave.mock.calls[0][0].energy).toBeCloseTo(418.4, 6);
  });

  it("keeps what is typed while the field has focus", async () => {
    energyUnit.current = "kcal";
    const onsave = vi.fn();
    render(FoodStateHost, {
      Form: InlineEditor,
      formProps: { food: { ...food, energy: 1046 }, onsave, oncancel: vi.fn() },
    });

    const energy = page.getByLabelText(/^Energy/);
    await energy.fill("1.5");

    await expect.element(energy).toHaveFocus();
    await expect.element(energy).toHaveValue(1.5);

    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onsave.mock.calls[0][0].energy).toBeCloseTo(6.276, 6);
  });

  it("shows and stores kilojoules unchanged for a kilojoule user", async () => {
    energyUnit.current = "kJ";
    const onsave = vi.fn();
    render(FoodStateHost, {
      Form: InlineEditor,
      formProps: { food: { ...food, energy: 1046 }, onsave, oncancel: vi.fn() },
    });

    const energy = page.getByLabelText(/^Energy/);
    await expect.element(energy).toHaveValue(1046);
    await expect.element(page.getByText("kJ", { exact: true })).toBeVisible();

    await energy.fill("500");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onsave.mock.calls[0][0].energy).toBe(500);
  });

  it("saves an energy the user did not touch exactly as stored", async () => {
    energyUnit.current = "kcal";
    const onsave = vi.fn();
    render(FoodStateHost, {
      Form: InlineEditor,
      formProps: { food: { ...food, energy: 1500 }, onsave, oncancel: vi.fn() },
    });

    await page.getByLabelText(/^Name/).fill("Steel-cut oats");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onsave.mock.calls[0][0].energy).toBe(1500);
  });
});

describe("add food dialog", () => {
  afterEach(() => {
    energyUnit.current = null;
    remote.createFood.mockClear();
    remote.updateFood.mockClear();
  });

  it("renders the food fields once and updates an existing food in kilojoules", async () => {
    energyUnit.current = "kcal";
    render(AddFoodDialog, {
      open: true,
      onOpenChange: vi.fn(),
      initialFood: { ...food, energy: 418.4 },
      categories: { Dairy: {} },
    });

    await expect.element(page.getByLabelText(/^Name/)).toBeVisible();
    expect(page.getByLabelText(/^Name/).elements()).toHaveLength(1);
    expect(page.getByLabelText(/^Energy/).elements()).toHaveLength(1);
    await expect.element(page.getByLabelText(/^Energy/)).toHaveValue(100);

    await page.getByLabelText(/^Energy/).fill("250");
    await page.getByRole("button", { name: "Update" }).click();

    expect(remote.updateFood).toHaveBeenCalledOnce();
    const { foodId, request } = remote.updateFood.mock.calls[0][0] as { foodId: string; request: Food };
    expect(foodId).toBe("f1");
    expect(request).toMatchObject({ _id: "f1", name: "Oats", carbs: 30 });
    expect(request.energy).toBeCloseTo(1046, 6);
  });

  it("creates a new food", async () => {
    render(AddFoodDialog, {
      open: true,
      onOpenChange: vi.fn(),
      categories: {},
    });

    await page.getByLabelText(/^Name/).fill("Rice");
    await page.getByLabelText(/^Carbs/).fill("28");
    await page.getByRole("button", { name: "Create" }).click();

    expect(remote.createFood).toHaveBeenCalledOnce();
    expect(remote.createFood.mock.calls[0][0]).toMatchObject({
      name: "Rice",
      carbs: 28,
      portion: 100,
      unit: "g",
      type: "food",
    });
    expect(remote.createFood.mock.calls[0][0]).not.toHaveProperty("_id");
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
