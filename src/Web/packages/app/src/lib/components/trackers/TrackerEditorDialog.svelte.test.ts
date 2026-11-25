import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TrackerCategory, type TrackerDefinitionDto } from "@nocturne/api-client";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { updateDefinition, schemaFor } = vi.hoisted(() => ({
  updateDefinition: vi.fn(async (request: unknown) => request),
  schemaFor: { update: undefined as unknown },
}));

// Built with the `form` the generated module imports, so a submission meets the schema as the
// generated `updateDefinition` does.
vi.mock("#lib/api/generated/trackers.generated.remote.js", async () => {
  const { form } = await import("#lib/api/remote-server.js");
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { UpdateTrackerDefinitionRequestSchema } = await import("@nocturne/api-client/schemas");
  const { z } = await import("zod");
  const update = formCoerce(z.object({ id: z.string(), request: UpdateTrackerDefinitionRequestSchema }));
  return {
    getTriggerEventTypes: () => remoteQuery(() => ["Pod Change", "Pod Activated", "Site Change"]),
    getEndEventTypes: () => remoteQuery(() => ["Sensor Stop", "Pod Deactivated"]),
    getDeviceEventTypes: () => remoteQuery(() => ["Sensor Start", "Sensor Stop", "Pod Change"]),
    updateDefinition: form(
      {
        "~standard": {
          vendor: "test",
          version: 1,
          validate: (data: unknown) => ((schemaFor.update ?? update) as typeof update)["~standard"].validate(data),
        },
      },
      updateDefinition
    ),
  };
});

import TrackerEditorDialogHost from "./TrackerEditorDialog.test-host.svelte";

const categoryLabels = Object.fromEntries(
  Object.values(TrackerCategory).map((c) => [c, c])
) as Record<TrackerCategory, string>;

const id = "0192f0e4-0000-7000-8000-000000000001";

function open(category: TrackerCategory, definition: TrackerDefinitionDto = {}) {
  const props = {
    open: true,
    definition: { id, name: "G7 Sensor", category, ...definition },
    categoryLabels,
  };
  const view = render(TrackerEditorDialogHost, { props });
  const setOpen = (value: boolean) => view.rerender({ ...props, open: value });
  return { setOpen };
}

const postedCovers = () =>
  [...document.querySelectorAll<HTMLInputElement>('input[name^="request.covers["]')].map((i) => i.value);

async function tooLongAName() {
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { UpdateTrackerDefinitionRequestSchema } = await import("@nocturne/api-client/schemas");
  const { z } = await import("zod");
  schemaFor.update = formCoerce(
    z.object({
      id: z.string(),
      request: UpdateTrackerDefinitionRequestSchema.extend({
        name: z.string().max(3, "The name is too long."),
      }),
    })
  );
}

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
    updateDefinition.mockClear();
    schemaFor.update = undefined;
  });

  async function fillAndSave() {
    open(TrackerCategory.Sensor);
    await page.getByLabelText("Name").fill("G7 Sensor left");
    await page.getByLabelText("Expected lifespan").fill("10d");
    await page.getByRole("button", { name: "Save" }).click();
  }

  it("saves the definition under its id and closes", async () => {
    await fillAndSave();

    await vi.waitFor(() => expect(updateDefinition).toHaveBeenCalledOnce());
    expect(updateDefinition.mock.calls[0][0]).toMatchObject({
      id,
      request: { name: "G7 Sensor left", category: TrackerCategory.Sensor, lifespanHours: 240 },
    });
    await expect.element(page.getByTestId("tracker-editor")).not.toBeInTheDocument();
  });

  it.each(["", "   "])("does not save a name of only %j, and says why before the click", async (name) => {
    open(TrackerCategory.Sensor);
    await expect.element(page.getByRole("button", { name: "Save" })).toBeEnabled();
    await expect.element(page.getByText("Enter a name.")).not.toBeInTheDocument();

    await page.getByLabelText("Name").fill(name);

    await expect.element(page.getByRole("button", { name: "Save" })).toBeDisabled();
    await expect.element(page.getByRole("status")).toHaveTextContent("Enter a name.");
    expect(updateDefinition).not.toHaveBeenCalled();
  });

  it("refuses a lifespan that is not a whole number of hours, which the schema cannot take", async () => {
    open(TrackerCategory.Sensor);

    await page.getByLabelText("Expected lifespan").fill("1.5");

    await expect.element(page.getByText("Use a whole number of hours.")).toBeVisible();
    expect(document.querySelector('input[name="n:request.lifespanHours"]')).toBeNull();
  });

  it("does not save the whole value typed on the way to one that is not whole", async () => {
    open(TrackerCategory.Sensor);

    await userEvent.type(page.getByLabelText("Expected lifespan"), "1.5");
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Use a whole number of hours.")).toBeVisible();
    expect(document.querySelector("form")?.checkValidity()).toBe(false);
    await new Promise((resolve) => setTimeout(resolve, 200));
    expect(updateDefinition).not.toHaveBeenCalled();
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
  });

  it("takes a product that is whole hours up to rounding", async () => {
    open(TrackerCategory.Sensor);

    await page.getByLabelText("Expected lifespan").fill("4.1x30");

    await expect.element(page.getByText("Use a whole number of hours.")).not.toBeInTheDocument();
    expect(document.querySelector<HTMLInputElement>('input[name="n:request.lifespanHours"]')?.value).toBe("123");
  });

  it("shows why the server refused the change, and stays open", async () => {
    updateDefinition.mockRejectedValueOnce({
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
    await tooLongAName();

    await fillAndSave();

    await expect.element(page.getByRole("alert")).toHaveTextContent("The name is too long.");
    await expect.element(page.getByTestId("tracker-editor")).toBeVisible();
    expect(updateDefinition).not.toHaveBeenCalled();
  });

  it("opens a definition without the refusal of the last attempt", async () => {
    await tooLongAName();
    const { setOpen } = open(TrackerCategory.Sensor);
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

describe("TrackerEditorDialog device events", () => {
  const posted = (name: string) =>
    document.querySelector<HTMLInputElement>(`input[name="request.${name}"]`)?.value;

  async function pick(testId: string, name: string) {
    await page.getByTestId(testId).click();
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
      startEventType: "Sensor Start",
      completionEventType: "Sensor Stop",
    });
    expect(posted("startEventType")).toBe("Sensor Start");

    await pick("startEventType", "None");
    await pick("completionEventType", "None");

    expect(posted("startEventType")).toBe("");
    expect(posted("completionEventType")).toBe("");
    await expect.element(page.getByTestId("startEventType")).toHaveTextContent("None");
  });

  it("shows a stored name the server no longer offers, and posts it unchanged", async () => {
    open(TrackerCategory.Sensor, { startEventType: "Meal Bolus" });

    await expect.element(page.getByTestId("startEventType")).toHaveTextContent("Meal Bolus");
    expect(posted("startEventType")).toBe("Meal Bolus");
  });

  it("posts both fields on a save with no event chosen", async () => {
    open(TrackerCategory.Sensor);

    expect(posted("startEventType")).toBe("");
    expect(posted("completionEventType")).toBe("");
  });
});
