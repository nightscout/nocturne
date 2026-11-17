import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, it, expect } from "vitest";
import FormGuardHarness from "./FormGuard.test-harness.svelte";

describe("FormGuard error display", () => {
  it("shows no error on an empty form while the form is still invalid", async () => {
    render(FormGuardHarness);

    await new Promise((resolve) => setTimeout(resolve, 400));

    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();
    await expect.element(page.getByText("City is required")).not.toBeInTheDocument();
    await expect.element(page.getByTestId("valid")).toHaveTextContent("false");
  });

  it("leaves a field alone that was left without an edit", async () => {
    render(FormGuardHarness);

    await userEvent.click(page.getByLabelText("Name"));
    await userEvent.click(page.getByLabelText("City"));

    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();
  });

  it("shows a field's error once it was edited and left, and no other", async () => {
    render(FormGuardHarness);

    await userEvent.fill(page.getByLabelText("Name"), "A");
    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();

    await userEvent.click(page.getByLabelText("City"));

    await expect.element(page.getByText("Name is too short")).toBeInTheDocument();
    await expect.element(page.getByText("City is required")).not.toBeInTheDocument();
  });

  it("shows every error after a submit attempt and keeps them live", async () => {
    render(FormGuardHarness);

    await userEvent.click(page.getByRole("button", { name: "Save" }));

    await expect.element(page.getByText("Name is too short")).toBeInTheDocument();
    await expect.element(page.getByText("City is required")).toBeInTheDocument();

    await userEvent.fill(page.getByLabelText("Name"), "Ada");

    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();
    await expect.element(page.getByText("City is required")).toBeInTheDocument();
  });

  it("moves focus to the first invalid field on a failed submit", async () => {
    render(FormGuardHarness);

    await userEvent.click(page.getByRole("button", { name: "Save" }));

    await expect.element(page.getByLabelText("Name")).toHaveFocus();
  });

  it("shows the error of a field edited, reverted and left", async () => {
    render(FormGuardHarness);

    await userEvent.fill(page.getByLabelText("Name"), "A");
    await userEvent.clear(page.getByLabelText("Name"));
    await userEvent.click(page.getByLabelText("City"));

    await expect.element(page.getByText("Name is too short")).toBeInTheDocument();
  });

  it("hides an error shown after a blur once the form is reset", async () => {
    render(FormGuardHarness);

    await userEvent.fill(page.getByLabelText("Name"), "A");
    await userEvent.click(page.getByLabelText("City"));
    await expect.element(page.getByText("Name is too short")).toBeInTheDocument();

    await userEvent.click(page.getByRole("button", { name: "Reset" }));
    await new Promise((resolve) => setTimeout(resolve, 400));

    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();
  });

  it("hides an error shown after a blur once the form saves", async () => {
    render(FormGuardHarness);

    await userEvent.fill(page.getByLabelText("Name"), "A");
    await userEvent.click(page.getByLabelText("City"));
    await expect.element(page.getByText("Name is too short")).toBeInTheDocument();
    await userEvent.fill(page.getByLabelText("Name"), "Ada");
    await userEvent.fill(page.getByLabelText("City"), "Perth");

    await userEvent.click(page.getByRole("button", { name: "Save" }));
    await new Promise((resolve) => setTimeout(resolve, 400));

    await expect.element(page.getByText("Name is too short")).not.toBeInTheDocument();
  });
});
