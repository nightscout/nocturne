import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { EntryRecord } from "#lib/constants/entry-categories.js";

const toastError = vi.hoisted(() => vi.fn());
vi.mock("svelte-sonner", () => ({ toast: { success: vi.fn(), error: toastError } }));

// What each form's handler received once its schema accepted the post, keyed by record kind and
// `create` or `update`.
const handlers = vi.hoisted(() => {
  const names = ["bolus", "carbs", "bgCheck", "note", "deviceEvent", "basalInjection"];
  return Object.fromEntries(
    names.flatMap((name) => [
      [`${name}.create`, vi.fn(async (_request: unknown) => ({ id: "saved" }))],
      [`${name}.update`, vi.fn(async (_request: unknown) => ({ id: "saved" }))],
    ]),
  );
});

// Built with the `form` the generated module imports, so a submission meets the schema as the
// generated `create` and `update` forms do.
async function kit() {
  const { form } = await import("#lib/api/remote-server.js");
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { z } = await import("zod");
  const schemas = await import("#lib/api/generated/schemas.js");
  return { form, formCoerce, z, schemas };
}

vi.mock("#lib/api/generated/bolus.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    create: form(formCoerce(schemas.CreateBolusRequestSchema), handlers["bolus.create"]),
    update: form(formCoerce(z.object({ id: z.string(), request: schemas.UpdateBolusRequestSchema })), handlers["bolus.update"]),
  };
});
vi.mock("#lib/api/generated/nutritions.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    createCarbIntake: form(formCoerce(schemas.CreateCarbIntakeRequestSchema), handlers["carbs.create"]),
    updateCarbIntake: form(formCoerce(z.object({ id: z.string(), request: schemas.UpdateCarbIntakeRequestSchema })), handlers["carbs.update"]),
  };
});
vi.mock("#lib/api/generated/bgChecks.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    create: form(formCoerce(schemas.UpsertBGCheckRequestSchema), handlers["bgCheck.create"]),
    update: form(formCoerce(z.object({ id: z.string(), request: schemas.UpsertBGCheckRequestSchema })), handlers["bgCheck.update"]),
  };
});
vi.mock("#lib/api/generated/notes.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    create: form(formCoerce(schemas.UpsertNoteRequestSchema), handlers["note.create"]),
    update: form(formCoerce(z.object({ id: z.string(), request: schemas.UpsertNoteRequestSchema })), handlers["note.update"]),
  };
});
vi.mock("#lib/api/generated/deviceEvents.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    create: form(formCoerce(schemas.UpsertDeviceEventRequestSchema), handlers["deviceEvent.create"]),
    update: form(formCoerce(z.object({ id: z.string(), request: schemas.UpsertDeviceEventRequestSchema })), handlers["deviceEvent.update"]),
  };
});
vi.mock("#lib/api/generated/basalInjections.generated.remote.js", async (importOriginal) => {
  const { form, formCoerce, z, schemas } = await kit();
  return {
    ...(await importOriginal<object>()),
    create: form(formCoerce(schemas.CreateBasalInjectionRequestSchema), handlers["basalInjection.create"]),
    update: form(formCoerce(z.object({ id: z.string(), request: schemas.UpdateBasalInjectionRequestSchema })), handlers["basalInjection.update"]),
  };
});
vi.mock("#lib/api/generated/patientRecords.generated.remote.js", async () => {
  const { remoteQuery } = await import("#lib/test-stubs/remote-resource.js");
  return { getInsulins: () => remoteQuery(() => []) };
});

// Closing the dialog steps browser history back, which in the test runner leaves the test page.
vi.mock("#lib/hooks/dialog-history.svelte.js", () => ({ useDialogHistory: () => {} }));

import EntryEditDialog from "./EntryEditDialog.svelte";

const at = Date.UTC(2026, 9, 27, 7, 30);
const id = (n: number) => `0192f0e4-0000-7000-8000-00000000000${n}`;

describe("EntryEditDialog saves", () => {
  beforeEach(() => {
    for (const handler of Object.values(handlers)) handler.mockClear();
    toastError.mockClear();
  });

  it("creates a meal bolus as the create schemas take it", async () => {
    render(EntryEditDialog, { open: true, entry: null, onClose: () => {} });

    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => {
      expect(handlers["bolus.create"]).toHaveBeenCalledOnce();
      expect(handlers["carbs.create"]).toHaveBeenCalledOnce();
    });
    expect(handlers["bolus.create"].mock.calls[0][0]).not.toHaveProperty("mills");
  });

  it("starts a new entry at the time it is given", async () => {
    render(EntryEditDialog, { open: true, entry: null, initialMills: at, onClose: () => {} });

    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(handlers["bolus.create"]).toHaveBeenCalledOnce());
    expect(handlers["bolus.create"].mock.calls[0][0]).toMatchObject({
      timestamp: new Date(at).toISOString(),
    });
  });

  it("stops saving and stays open when the server refuses a record", async () => {
    handlers["bolus.create"].mockRejectedValueOnce({ status: 400, body: { message: "Insulin is too high" } });
    const onClose = vi.fn();
    render(EntryEditDialog, { open: true, entry: null, onClose });

    await page.getByRole("button", { name: "Save", exact: true }).click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalledWith("Insulin is too high"));
    await expect.element(page.getByRole("button", { name: "Save", exact: true })).toBeEnabled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it("saves each kind of record under request, as its update schema takes it", async () => {
    const entry: EntryRecord = { kind: "bolus", data: { id: id(1), mills: at, insulin: 2 } };
    const correlatedRecords: EntryRecord[] = [
      { kind: "carbs", data: { id: id(2), mills: at, carbs: 30 } },
      { kind: "bgCheck", data: { id: id(3), mills: at, glucose: 120 } },
      { kind: "note", data: { id: id(4), mills: at, text: "Lunch" } },
      { kind: "deviceEvent", data: { id: id(5), mills: at } },
      { kind: "basalInjection", data: { id: id(6), mills: at, units: 10 } },
    ];
    render(EntryEditDialog, { open: true, entry, correlatedRecords, onClose: () => {} });

    await page.getByRole("button", { name: "Save changes" }).click();

    const kinds = ["bolus", "carbs", "bgCheck", "note", "deviceEvent", "basalInjection"];
    await vi.waitFor(() => {
      for (const kind of kinds) expect(handlers[`${kind}.update`], kind).toHaveBeenCalledOnce();
    });

    expect(handlers["bolus.update"].mock.calls[0][0]).toMatchObject({
      id: id(1),
      request: { insulin: 2, timestamp: new Date(at).toISOString() },
    });
  });

  it("keeps the optional fields a record already holds", async () => {
    const entry: EntryRecord = {
      kind: "bolus",
      data: {
        id: id(1),
        mills: at,
        insulin: 2,
        duration: 30,
        programmed: 2.5,
        delivered: 2,
        insulinType: "Fiasp",
        unabsorbed: 0.5,
      },
    };
    const correlatedRecords: EntryRecord[] = [
      { kind: "carbs", data: { id: id(2), mills: at, carbs: 30, carbTime: 15, absorptionTime: 180 } },
      { kind: "deviceEvent", data: { id: id(5), mills: at, notes: "New site" } },
      {
        kind: "basalInjection",
        data: { id: id(6), mills: at, units: 10, notes: "Evening", insulinContext: { patientInsulinId: id(7) } },
      },
    ];
    render(EntryEditDialog, { open: true, entry, correlatedRecords, onClose: () => {} });

    await page.getByRole("button", { name: "Save changes" }).click();

    const kinds = ["bolus", "carbs", "deviceEvent", "basalInjection"];
    await vi.waitFor(() => {
      for (const kind of kinds) expect(handlers[`${kind}.update`], kind).toHaveBeenCalledOnce();
    });

    const received = (kind: string) => handlers[`${kind}.update`].mock.calls[0][0];
    expect(received("bolus")).toMatchObject({
      request: { duration: 30, programmed: 2.5, delivered: 2, insulinType: "Fiasp", unabsorbed: 0.5 },
    });
    expect(received("carbs")).toMatchObject({ request: { carbTime: 15, absorptionTime: 180 } });
    expect(received("deviceEvent")).toMatchObject({ request: { notes: "New site" } });
    expect(received("basalInjection")).toMatchObject({
      request: { notes: "Evening", patientInsulinId: id(7) },
    });
  });
});
