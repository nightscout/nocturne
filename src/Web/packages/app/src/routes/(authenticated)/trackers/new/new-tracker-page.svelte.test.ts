import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import {
  NotificationUrgency,
  PumpCapability,
  TrackerCategory,
  TrackerMode,
  type TrackerTemplateDto,
} from "#lib/api/generated/nocturne-api-client.js";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import WithTooltipProvider from "#lib/test-stubs/WithTooltipProvider.test-stub.svelte";

const { createDefinition, goto } = vi.hoisted(() => ({
  createDefinition: vi.fn(async (request: unknown): Promise<unknown> => request),
  goto: vi.fn(async (_url: string, _options?: unknown) => {}),
}));

const TEMPLATES: TrackerTemplateDto[] = [
  {
    id: "sensor",
    definition: {
      name: "Dexcom G7 sensor",
      category: TrackerCategory.Sensor,
      icon: "radio",
      mode: TrackerMode.Duration,
      lifespanHours: 240,
      triggerEventTypes: ["Sensor Start", "Sensor Change"],
      startEventType: "Sensor Start",
      notificationThresholds: [
        { urgency: NotificationUrgency.Warn, hours: -24, displayOrder: 0 },
        { urgency: NotificationUrgency.Hazard, hours: 240, displayOrder: 1 },
        { urgency: NotificationUrgency.Urgent, hours: 251, displayOrder: 2 },
      ],
    },
  },
  {
    id: "reservoir",
    definition: {
      name: "Reservoir",
      category: TrackerCategory.Reservoir,
      mode: TrackerMode.Duration,
      lifespanHours: 72,
      covers: [PumpCapability.Reservoir],
    },
  },
  {
    id: "appointment",
    definition: {
      name: "",
      category: TrackerCategory.Appointment,
      mode: TrackerMode.Event,
      notificationThresholds: [{ urgency: NotificationUrgency.Info, hours: -24, displayOrder: 0 }],
    },
  },
  { id: "custom", definition: { name: "", category: TrackerCategory.Custom, mode: TrackerMode.Duration } },
  { id: "a_type_this_build_has_no_copy_for", definition: { name: "Later" } },
];

// Built with the `form` the generated module imports, so a submission meets the schema as the
// generated `createDefinition` does.
vi.mock("#lib/api/generated/trackers.generated.remote.js", async () => {
  const { form } = await import("#lib/api/remote-server.js");
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { CreateTrackerDefinitionRequestSchema } = await import("#lib/api/generated/schemas.js");
  return {
    getTemplates: () => remoteQuery(() => TEMPLATES),
    getTriggerEventTypes: () => remoteQuery(() => ["Sensor Start", "Sensor Change", "Site Change"]),
    getEndEventTypes: () => remoteQuery(() => ["Sensor Stop"]),
    getDeviceEventTypes: () => remoteQuery(() => ["Sensor Start", "Sensor Stop"]),
    createDefinition: form(formCoerce(CreateTrackerDefinitionRequestSchema), createDefinition),
  };
});

vi.mock("$app/navigation", async (original) => ({
  ...(await original<typeof import("$app/navigation")>()),
  goto,
}));

import NewTrackerPage from "./+page.svelte";

const next = () => page.getByRole("button", { name: "Next" });
const create = () => page.getByRole("button", { name: "Create tracker" });
const show = () => render(WithTooltipProvider, { props: { component: NewTrackerPage } });

describe("new tracker wizard", () => {
  beforeEach(async () => {
    await page.viewport(1280, 900);
    pageState.data = { effectivePermissions: ["alerts.readwrite"] };
    pageState.state = {};
    createDefinition.mockClear();
    createDefinition.mockImplementation(async (request) => request);
    goto.mockClear();
  });

  it("starts by asking what to track, and lists the types it has copy for", async () => {
    show();

    await expect
      .element(page.getByRole("heading", { name: "What do you want to track?" }))
      .toBeVisible();
    await expect.element(page.getByTestId("tracker-type-sensor")).toHaveTextContent("CGM sensor");
    expect(document.querySelectorAll('[data-testid^="tracker-type-"]')).toHaveLength(4);
    await expect.element(next()).not.toBeInTheDocument();
  });

  it("opens the settings of the chosen type with what the template has", async () => {
    show();

    await page.getByTestId("tracker-type-sensor").click();

    await expect.element(page.getByRole("heading", { name: "Name and settings" })).toBeVisible();
    await expect.element(page.getByLabelText("Name")).toHaveValue("Dexcom G7 sensor");
    await expect.element(page.getByLabelText("How long it lasts")).toHaveValue("240");
    await expect
      .element(page.getByTestId("tracker-restart-summary"))
      .toHaveTextContent("Sensor start, Sensor change");
    await expect.element(page.getByTestId("tracker-triggers")).not.toBeVisible();
  });

  it("creates the tracker of a template in three steps, and hands it over to be started", async () => {
    createDefinition.mockResolvedValueOnce({ id: "new-id" });
    show();

    await page.getByTestId("tracker-type-sensor").click();
    await next().click();
    await expect
      .element(page.getByRole("heading", { name: "When does it remind you?" }))
      .toBeVisible();
    await create().click();

    await vi.waitFor(() => expect(createDefinition).toHaveBeenCalledOnce());
    expect(createDefinition.mock.calls[0][0]).toMatchObject({
      name: "Dexcom G7 sensor",
      category: TrackerCategory.Sensor,
      mode: TrackerMode.Duration,
      lifespanHours: 240,
      triggerEventTypes: ["Sensor Start", "Sensor Change"],
      startEventType: "Sensor Start",
      notificationThresholds: [
        { urgency: NotificationUrgency.Warn, hours: -24 },
        { urgency: NotificationUrgency.Hazard, hours: 240 },
        { urgency: NotificationUrgency.Urgent, hours: 251 },
      ],
    });
    await vi.waitFor(() =>
      expect(goto).toHaveBeenLastCalledWith("/trackers", { state: { startTrackerId: "new-id" } }),
    );
  });

  it("moves the thresholds at the end of the lifespan with it", async () => {
    show();

    await page.getByTestId("tracker-type-sensor").click();
    await page.getByLabelText("How long it lasts").fill("14d");
    await next().click();
    await create().click();

    await vi.waitFor(() => expect(createDefinition).toHaveBeenCalledOnce());
    expect(createDefinition.mock.calls[0][0]).toMatchObject({
      lifespanHours: 336,
      notificationThresholds: [{ hours: -24 }, { hours: 336 }, { hours: 347 }],
    });
  });

  it("does not go on without a name, and says why", async () => {
    show();

    await page.getByTestId("tracker-type-appointment").click();

    await expect.element(next()).toBeDisabled();
    await expect.element(page.getByText("Enter a name.")).toBeVisible();
    await expect.element(page.getByLabelText("How long it lasts")).not.toBeInTheDocument();

    await page.getByLabelText("Name").fill("Endocrinologist");
    await expect.element(next()).toBeEnabled();
  });

  it("does not leave the settings on a lifespan it cannot read", async () => {
    show();

    await page.getByTestId("tracker-type-sensor").click();
    await page.getByLabelText("How long it lasts").fill("1.5");
    await next().click();

    await expect.element(page.getByRole("heading", { name: "Name and settings" })).toBeVisible();
  });

  it("offers the low reservoir alert for a tracker that holds insulin", async () => {
    show();

    await page.getByTestId("tracker-type-reservoir").click();

    await expect.element(page.getByLabelText("Low reservoir alert (units)")).toBeVisible();
  });

  it("lets a tracker with no template choose how it counts", async () => {
    show();

    await page.getByTestId("tracker-type-custom").click();
    await page.getByLabelText("Name").fill("Dentist");
    await page.getByTestId("tracker-mode-Event").click();
    await next().click();
    await create().click();

    await vi.waitFor(() => expect(createDefinition).toHaveBeenCalledOnce());
    expect(createDefinition.mock.calls[0][0]).toMatchObject({
      name: "Dentist",
      category: TrackerCategory.Custom,
      mode: TrackerMode.Event,
    });
    expect(createDefinition.mock.calls[0][0]).not.toHaveProperty("lifespanHours");
  });

  it("keeps the events that restart a tracker behind More settings", async () => {
    show();

    await page.getByTestId("tracker-type-sensor").click();
    await page.getByTestId("tracker-more-settings").click();
    await page.getByRole("checkbox", { name: "Sensor change" }).click();

    await expect
      .element(page.getByTestId("tracker-restart-summary"))
      .toHaveTextContent("one of these events: Sensor start.");
  });

  it("shows why the server refused the tracker, and stays on the last step", async () => {
    createDefinition.mockRejectedValueOnce({
      status: 400,
      body: { message: "Negative threshold -300 exceeds tracker lifespan of 240 hours" },
    });
    show();

    await page.getByTestId("tracker-type-sensor").click();
    await next().click();
    await create().click();

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Negative threshold -300 exceeds tracker lifespan of 240 hours");
    await expect.element(create()).toBeVisible();
    expect(goto).not.toHaveBeenCalledWith("/trackers", expect.anything());
  });
});
