import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";

const removeBolus = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
const createBolus = vi.hoisted(() => vi.fn());
const updateBolus = vi.hoisted(() => vi.fn());

vi.mock("svelte-sonner", () => ({
  toast: { success: vi.fn(), error: toastError },
}));
// Built with the `form` the generated module imports, so a submission meets the schema as the
// generated `create` and `update` do.
vi.mock("#lib/api/generated/bolus.generated.remote.js", async () => {
  const { form } = await import("#lib/api/remote-server.js");
  const { formCoerce } = await import("#lib/api/generated/form-utils.generated.js");
  const { CreateBolusRequestSchema, UpdateBolusRequestSchema } = await import("#lib/api/generated/schemas.js");
  const { z } = await import("zod");
  return {
    create: form(formCoerce(CreateBolusRequestSchema), createBolus),
    update: form(formCoerce(z.object({ id: z.string(), request: UpdateBolusRequestSchema })), updateBolus),
    remove: removeBolus,
  };
});
vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => ({ current: [] }),
}));

import type { RealtimeStore } from "#lib/stores/realtime-store.svelte.js";
import Wrapper from "./meal-bolus-dialog-test-wrapper.svelte";

function mount(meal: { correlationId?: string; boluses?: ({ id: string; mills: number; insulin: number } & Record<string, unknown>)[] } = { correlationId: "0192f0e4-0000-7000-8000-000000000001" }) {
  let store!: RealtimeStore;
  const onSave = vi.fn();
  render(Wrapper, {
    props: {
      meal: { boluses: [{ id: "b1", mills: Date.now(), insulin: 4 }], ...meal },
      onstore: (s: RealtimeStore) => (store = s),
      onSave,
    },
  });
  return { store, onSave };
}

async function deleteBolus() {
  await page.getByRole("button", { name: "Delete bolus" }).click();
  await page.getByRole("button", { name: "Delete", exact: true }).click();
}

describe("MealBolusDialog", () => {
  beforeEach(() => {
    toastError.mockClear();
    createBolus.mockReset();
    updateBolus.mockReset();
    createBolus.mockResolvedValue({ id: "new" });
    updateBolus.mockResolvedValue({ id: "new" });
  });

  it("tells the realtime store a treatment changed once a delete succeeds", async () => {
    removeBolus.mockResolvedValue(undefined);
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await deleteBolus();

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before + 1);
  });

  it("leaves the store alone when the delete fails", async () => {
    removeBolus.mockRejectedValue(new Error("nope"));
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await deleteBolus();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before);
    expect(onSave).not.toHaveBeenCalled();
  });

  it("tells the realtime store a treatment changed once a save succeeds", async () => {
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(onSave).toHaveBeenCalled());
    expect(store.treatmentRevision).toBe(before + 1);
  });

  it("leaves the store alone when the save fails", async () => {
    createBolus.mockResolvedValueOnce(undefined);
    const { store } = mount();
    const before = store.treatmentRevision;

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(createBolus).toHaveBeenCalledOnce();
    expect(store.treatmentRevision).toBe(before);
  });

  it("leaves the saving state and says why when the server refuses the save", async () => {
    createBolus.mockRejectedValueOnce({ status: 400, body: { message: "Insulin is too high" } });
    const { store, onSave } = mount();
    const before = store.treatmentRevision;

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(toastError).toHaveBeenCalledWith("Insulin is too high"));
    await expect.element(page.getByRole("button", { name: "Save" })).toBeEnabled();
    expect(store.treatmentRevision).toBe(before);
    expect(onSave).not.toHaveBeenCalled();
  });

  it("posts a new bolus as the create schema takes it", async () => {
    mount({ correlationId: "0192f0e4-0000-7000-8000-000000000001" });

    await page.getByRole("button", { name: "Add bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(createBolus).toHaveBeenCalledOnce());
    const body = createBolus.mock.calls[0][0];
    expect(body).toMatchObject({ insulin: expect.any(Number), kind: "Manual", correlationId: "0192f0e4-0000-7000-8000-000000000001" });
    expect(body).not.toHaveProperty("mills");
  });

  it("posts an edited bolus under request, as the update schema takes it", async () => {
    mount();

    await page.getByRole("button", { name: "Edit bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(updateBolus).toHaveBeenCalledOnce());
    expect(updateBolus.mock.calls[0][0]).toMatchObject({ id: "b1", request: { insulin: 4 } });
  });

  it("keeps the optional fields an edited bolus already holds", async () => {
    const patientInsulinId = "0192f0e4-0000-7000-8000-000000000002";
    mount({
      boluses: [
        {
          id: "b1",
          mills: Date.now(),
          insulin: 4,
          programmed: 4.5,
          delivered: 4,
          duration: 30,
          insulinType: "Fiasp",
          insulinContext: { patientInsulinId },
        },
      ],
    });

    await page.getByRole("button", { name: "Edit bolus" }).click();
    await page.getByRole("button", { name: "Save" }).click();

    await vi.waitFor(() => expect(updateBolus).toHaveBeenCalledOnce());
    expect(updateBolus.mock.calls[0][0]).toMatchObject({
      request: { programmed: 4.5, delivered: 4, duration: 30, insulinType: "Fiasp", patientInsulinId },
    });
  });

  it("does not offer a bolus on a meal with no correlation id", async () => {
    mount({ boluses: [] });

    await expect.element(page.getByText("No insulin records for this meal")).toBeVisible();
    await expect.element(page.getByText("You cannot add insulin here.", { exact: false })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Add bolus" })).not.toBeInTheDocument();
  });
});
