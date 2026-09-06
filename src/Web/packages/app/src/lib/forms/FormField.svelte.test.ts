import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import FormFieldHarness from "./FormField.test-harness.svelte";

describe("FormField", () => {
  it("pairs the label with the control via a generated id", async () => {
    render(FormFieldHarness, { label: "Username" });

    const input = page.getByLabelText("Username");
    await expect.element(input).toBeInTheDocument();

    const el = input.element() as HTMLInputElement;
    expect(el.id).not.toBe("");
    expect(document.querySelector(`label[for="${el.id}"]`)).not.toBeNull();
  });

  it("uses an explicit id when given one", async () => {
    render(FormFieldHarness, { label: "Username", id: "login-username" });

    const el = page.getByLabelText("Username").element() as HTMLInputElement;
    expect(el.id).toBe("login-username");
  });

  it("generates a distinct id per instance", async () => {
    render(FormFieldHarness, { label: "Username", second: "Display name" });

    const first = page.getByLabelText("Username").element() as HTMLInputElement;
    const second = page
      .getByLabelText("Display name")
      .element() as HTMLInputElement;

    expect(first.id).not.toBe(second.id);
  });

  it("marks the control required and says so to assistive tech", async () => {
    render(FormFieldHarness, { label: "Username", required: true });

    const el = page.getByLabelText("Username").element() as HTMLInputElement;
    expect(el.required).toBe(true);
    await expect.element(page.getByText("(required)")).toBeInTheDocument();
  });

  it("describes the control with its hint when valid", async () => {
    render(FormFieldHarness, {
      label: "Username",
      description: "3-32 characters.",
    });

    const el = page.getByLabelText("Username").element() as HTMLInputElement;
    const describedBy = el.getAttribute("aria-describedby");
    expect(describedBy).not.toBeNull();
    expect(document.getElementById(describedBy!)?.textContent).toContain(
      "3-32 characters."
    );
    expect(el.getAttribute("aria-invalid")).toBeNull();
  });

  it("marks the control invalid and describes it with the message", async () => {
    render(FormFieldHarness, {
      label: "Username",
      description: "3-32 characters.",
      issues: ["That username is taken"],
    });

    const el = page.getByLabelText("Username").element() as HTMLInputElement;
    expect(el.getAttribute("aria-invalid")).toBe("true");

    const describedBy = el.getAttribute("aria-describedby");
    const message = document.getElementById(describedBy!);
    expect(message?.textContent).toContain("That username is taken");
    // The error replaces the hint rather than stacking under it.
    expect(message?.textContent).not.toContain("3-32 characters.");
  });

  it("announces the message as an alert", async () => {
    render(FormFieldHarness, {
      label: "Username",
      issues: ["That username is taken"],
    });

    const alert = document.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain("That username is taken");
  });

  it("accepts SvelteKit form issue objects", async () => {
    render(FormFieldHarness, {
      label: "Username",
      issues: [{ message: "Required" }],
    });

    await expect.element(page.getByText("Required")).toBeInTheDocument();
  });
});
