import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TrackerCategory } from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import WithTooltipProvider from "#lib/test-stubs/WithTooltipProvider.test-stub.svelte";

const { createDefinition, updateDefinition, schemaFor } = vi.hoisted(() => ({
  createDefinition: vi.fn(async (request: unknown) => request),
  updateDefinition: vi.fn(async (request: unknown) => request),
  schemaFor: { create: undefined as unknown },
}));

// Built with the `form` the generated module imports, so a submission meets the schema as the
// generated `createDefinition` and `updateDefinition` do.
vi.mock("#lib/api/generated/trackers.generated.remote.js", async () => {
  const { form } = await import("#lib/api/remote-server.js");
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { CreateTrackerDefinitionRequestSchema, UpdateTrackerDefinitionRequestSchema } = await import(
    "#lib/api/generated/schemas.js"
  );
  const { z } = await import("zod");
  const create = formCoerce(CreateTrackerDefinitionRequestSchema);
  return {
    getTriggerEventTypes: () => remoteQuery(() => ["Pod Change", "Pod Activated", "Site Change"]),
    getEndEventTypes: () => remoteQuery(() => ["Sensor Stop", "Pod Deactivated"]),
    getDeviceEventTypes: () => remoteQuery(() => ["Sensor Start", "Sensor Stop", "Pod Change"]),
    createDefinition: form(
      {
        "~standard": {
          vendor: "test",
          version: 1,
          validate: (data: unknown) => ((schemaFor.create ?? create) as typeof create)["~standard"].validate(data),
        },
      },
      createDefinition
    ),
    updateDefinition: form(
      formCoerce(z.object({ id: z.string(), request: UpdateTrackerDefinitionRequestSchema })),
      updateDefinition
    ),
  };
});

import TrackerEditorDialog from "./TrackerEditorDialog.svelte";

const categoryLabels = Object.fromEntries(
  Object.values(TrackerCategory).map((c) => [c, c])
) as Record<TrackerCategory, string>;

function open(formCategory: TrackerCategory, props: Record<string, unknown> = {}) {
  const componentProps = {
    open: true,
    isNewDefinition: true,
    editingDefinition: null,
    formCategory,
    categoryLabels,
    ...props,
  };
  const view = render(WithTooltipProvider, {
    props: { component: TrackerEditorDialog, componentProps },
  });
  const setOpen = (value: boolean) =>
    view.rerender({ componentProps: { ...componentProps, open: value } });
  return { setOpen };
}

const postedCovers = () =>
  [...document.querySelectorAll<HTMLInputElement>('input[name^="covers["]')].map((i) => i.value);

describe("TrackerEditorDialog covers", () => {
  it("offers what a pump part replaces, and a low reservoir alert once it replaces the insulin", async () => {
    open(TrackerCategory.Consumable);

    await expect.element(page.getByTestId("tracker-covers")).toBeVisible();
    expect(postedCovers()).toEqual([""]);
    expect(document.getElementById("lowReservoirUnits")).toBeNull();

    await page.getByRole("checkbox", { name: "Insulin" }).click();

    await expect.element(page.getByLabelText("Low reservoir alert (units)")).toBeVisible();
    expect(postedCovers()).toEqual(["Reservoir"]);
  });

  it("ticks the site, the insulin and the battery when the category becomes Pod", async () => {
    open(TrackerCategory.Consumable);

    await page.getByTestId("tracker-category-trigger").click();
    await page.getByRole("option", { name: "Pod", exact: true }).click();

    await expect.element(page.getByRole("checkbox", { name: "Infusion site" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Insulin" })).toBeChecked();
    await expect.element(page.getByRole("checkbox", { name: "Pump battery" })).toBeChecked();
    expect(postedCovers()).toEqual(["Site", "Reservoir", "Power"]);
  });

  it("does not offer it for a sensor", async () => {
    open(TrackerCategory.Sensor);

    await expect.element(page.getByTestId("tracker-ends")).toBeVisible();
    expect(document.querySelector('[data-testid="tracker-covers"]')).toBeNull();
    expect(postedCovers()).toEqual([""]);
  });
});

describe("TrackerEditorDialog save", () => {
  beforeEach(() => {
    createDefinition.mockClear();
    updateDefinition.mockClear();
    schemaFor.create = undefined;
  });

  async function fillAndSave() {
    open(TrackerCategory.Sensor);
    await page.getByLabelText("Name").fill("G7 Sensor");
    await page.getByLabelText("Expected lifespan").fill("10d");
    await page.getByRole("button", { name: "Save" }).click();
  }

  it("saves a new definition and closes", async () => {
    await fillAndSave();

    await vi.waitFor(() => expect(createDefinition).toHaveBeenCalledOnce());
    expect(createDefinition.mock.calls[0][0]).toMatchObject({
      name: "G7 Sensor",
      category: TrackerCategory.Sensor,
      lifespanHours: 240,
    });
    await expect.element(page.getByTestId("tracker-editor")).not.toBeInTheDocument();
  });

  it("refuses a lifespan that is not a whole number of hours, which the schema cannot take", async () => {
    open(TrackerCategory.Sensor);

    await page.getByLabelText("Expected lifespan").fill("1.5");

    await expect.element(page.getByText("Use a whole number of hours.")).toBeVisible();
    expect(document.querySelector('input[name="n:lifespanHours"]')).toBeNull();
  });

  it("does not save the whole value typed on the way to one that is not whole", async () => {
    open(TrackerCategory.Sensor);
    await page.getByLabelText("Name").fill("G7 Sensor");

    await userEvent.type(page.getByLabelText("Expected lifespan"), "1.5");
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Use a whole number of hours.")).toBeVisible();
    expect(document.querySelector("form")?.checkValidity()).toBe(false);
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(createDefinition).not.toHaveBeenCalled();
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
  });

  it("takes a product that is whole hours up to rounding", async () => {
    open(TrackerCategory.Sensor);

    await page.getByLabelText("Expected lifespan").fill("4.1x30");

    await expect.element(page.getByText("Use a whole number of hours.")).not.toBeInTheDocument();
    expect(document.querySelector<HTMLInputElement>('input[name="n:lifespanHours"]')?.value).toBe("123");
  });

  it("shows why the server refused the definition, and stays open", async () => {
    createDefinition.mockRejectedValueOnce({
      status: 400,
      body: { message: "Negative threshold -300 exceeds tracker lifespan of 240 hours" },
    });

    await fillAndSave();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Negative threshold -300 exceeds tracker lifespan of 240 hours");
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
  });

  it("shows why the definition failed validation, and stays open", async () => {
    const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
    const { CreateTrackerDefinitionRequestSchema } = await import("#lib/api/generated/schemas.js");
    const { z } = await import("zod");
    schemaFor.create = formCoerce(
      CreateTrackerDefinitionRequestSchema.extend({ name: z.string().max(3, "The name is too long.") })
    );

    await fillAndSave();

    await expect.element(page.getByRole("alert")).toHaveTextContent("The name is too long.");
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
    expect(createDefinition).not.toHaveBeenCalled();
  });

  it("opens a new definition without the refusal of the last attempt", async () => {
    const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
    const { CreateTrackerDefinitionRequestSchema } = await import("#lib/api/generated/schemas.js");
    const { z } = await import("zod");
    schemaFor.create = formCoerce(
      CreateTrackerDefinitionRequestSchema.extend({ name: z.string().max(3, "The name is too long.") })
    );
    const { setOpen } = open(TrackerCategory.Sensor);
    await page.getByLabelText("Name").fill("G7 Sensor");
    await page.getByRole("button", { name: "Save" }).click();
    await expect.element(page.getByRole("alert")).toHaveTextContent("The name is too long.");

    await page.getByRole("button", { name: "Cancel" }).click();
    await expect.element(page.getByTestId("tracker-editor")).not.toBeInTheDocument();
    await setOpen(false);
    await setOpen(true);

    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
    await expect.element(page.getByRole("alert")).not.toBeInTheDocument();
  });
});

describe("TrackerEditorDialog edit", () => {
  const id = "0192f0e4-0000-7000-8000-000000000001";

  beforeEach(() => updateDefinition.mockClear());

  async function editAndSave() {
    open(TrackerCategory.Sensor, {
      isNewDefinition: false,
      editingDefinition: { id, name: "G7 Sensor" },
      formName: "G7 Sensor",
    });
    await page.getByLabelText("Name").fill("G7 Sensor left");
    await page.getByRole("button", { name: "Save" }).click();
  }

  it("saves the definition under its id and closes", async () => {
    await editAndSave();

    await vi.waitFor(() => expect(updateDefinition).toHaveBeenCalledOnce());
    expect(updateDefinition.mock.calls[0][0]).toMatchObject({
      id,
      request: { name: "G7 Sensor left", category: TrackerCategory.Sensor },
    });
    await expect.element(page.getByTestId("tracker-editor")).not.toBeInTheDocument();
  });

  it("shows why the server refused the change, and stays open", async () => {
    updateDefinition.mockRejectedValueOnce({
      status: 400,
      body: { message: "Event mode trackers should not have a lifespan" },
    });

    await editAndSave();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Event mode trackers should not have a lifespan");
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
  });
});

describe("TrackerEditorDialog device events", () => {
  const posted = (name: string) =>
    document.querySelector<HTMLInputElement>(`input[name="${name}"]`)?.value;

  async function pick(id: string, name: string) {
    await page.getByTestId(id).click();
    await page.getByRole("option", { name, exact: true }).click();
    await expect.element(page.getByRole("listbox")).not.toBeInTheDocument();
  }

  it("offers None and only the names the server lists", async () => {
    open(TrackerCategory.Sensor);

    await page.getByTestId("startEventType").click();
    await expect.element(page.getByRole("listbox")).toBeVisible();

    const options = [...document.querySelectorAll('[role="option"]')].map((o) => o.textContent?.trim());
    expect(options).toEqual(["None", "Sensor start", "Sensor stop", "Pod change"]);
  });

  it("posts the chosen start and completion events", async () => {
    open(TrackerCategory.Sensor);

    await pick("startEventType", "Sensor start");
    await pick("completionEventType", "Sensor stop");

    expect(posted("startEventType")).toBe("Sensor Start");
    expect(posted("completionEventType")).toBe("Sensor Stop");
  });

  it("posts both fields blank once None clears a stored event, so the server clears it", async () => {
    open(TrackerCategory.Sensor, {
      formStartEventType: "Sensor Start",
      formCompletionEventType: "Sensor Stop",
    });
    expect(posted("startEventType")).toBe("Sensor Start");

    await pick("startEventType", "None");
    await pick("completionEventType", "None");

    expect(posted("startEventType")).toBe("");
    expect(posted("completionEventType")).toBe("");
    await expect.element(page.getByTestId("startEventType")).toHaveTextContent("None");
  });

  it("shows a stored name the server no longer offers, and posts it unchanged", async () => {
    open(TrackerCategory.Sensor, { formStartEventType: "Meal Bolus" });

    await expect.element(page.getByTestId("startEventType")).toHaveTextContent("Meal Bolus");
    expect(posted("startEventType")).toBe("Meal Bolus");
  });

  it("posts both fields on a save with no event chosen", async () => {
    open(TrackerCategory.Sensor);

    expect(posted("startEventType")).toBe("");
    expect(posted("completionEventType")).toBe("");
  });
});
