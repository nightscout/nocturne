import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import FormActions from "./FormActions.svelte";

describe("FormActions", () => {
  it("renders the painted submit as the button itself, tied to its form", async () => {
    render(FormActions, { submitLabel: "Save Changes", formId: "clinical-form", saved: 0 });

    const submit = page.getByRole("button", { name: "Save Changes" });
    await expect.element(submit).toHaveAttribute("type", "submit");
    await expect.element(submit).toHaveAttribute("form", "clinical-form");
    expect(submit.element().querySelector("button")).toBeNull();
  });

  it("keeps the submit disabled while the form is in flight", async () => {
    render(FormActions, { submitLabel: "Save", pendingLabel: "Saving", form: { pending: 1 } });

    await expect.element(page.getByRole("button", { name: "Saving" })).toBeDisabled();
  });

  it("stays a working button after a save", async () => {
    const screen = render(FormActions, { submitLabel: "Save", saved: 0 });
    await screen.rerender({ submitLabel: "Save", saved: 1 });

    await expect.element(page.getByRole("button", { name: "Save" })).toBeEnabled();
  });
});
