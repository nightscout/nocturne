import { describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import AddressComposer from "./AddressComposer.svelte";
import AddressUses from "./AddressUses.svelte";

describe("AddressComposer", () => {
  it("frames the subdomain inside the site address", async () => {
    render(AddressComposer, {
      props: { value: "sam", baseDomain: "example.org", availability: { state: "available", slug: "sam" } },
    });

    await expect.element(page.getByRole("textbox")).toHaveValue("sam");
    await expect.element(page.getByText(".example.org", { exact: true })).toBeVisible();
    await expect.element(page.getByText("This address is available.")).toBeVisible();
  });

  it("says why an address was refused, and marks the field invalid", async () => {
    render(AddressComposer, {
      props: {
        value: "dev",
        baseDomain: "example.org",
        availability: { state: "refused", message: "This name is reserved" },
      },
    });

    await expect.element(page.getByText("This name is reserved")).toBeVisible();
    await expect.element(page.getByRole("textbox")).toHaveAttribute("aria-invalid", "true");
  });

  it("shows the rule before anything is typed, and the check while it runs", async () => {
    const view = render(AddressComposer, {
      props: { value: "", baseDomain: "example.org", availability: { state: "empty" } },
    });
    await expect
      .element(page.getByText("Use 3 to 63 lowercase letters, numbers, and hyphens."))
      .toBeVisible();

    await view.rerender({ value: "sa", baseDomain: "example.org", availability: { state: "checking" } });
    await expect.element(page.getByText("Checking the address...")).toBeVisible();
  });

  it("names the server address so it is not used by mistake", async () => {
    render(AddressComposer, {
      props: { value: "", baseDomain: "example.org", availability: { state: "empty" } },
    });

    await expect
      .element(page.getByText("That is the address of the server, and many sites share it.", { exact: false }))
      .toBeVisible();
  });
});

describe("AddressUses", () => {
  it("shows the full address in each place it is used", async () => {
    render(AddressUses, { props: { slug: "sam-taylor", baseDomain: "example.org" } });

    await expect.element(page.getByText("To sign in")).toBeVisible();
    await expect.element(page.getByText("In your apps")).toBeVisible();
    await expect.element(page.getByText("For followers")).toBeVisible();
    await expect.element(page.getByText("sam-taylor", { exact: true }).first()).toBeVisible();
    await expect.element(page.getByText(".example.org", { exact: true }).nth(2)).toBeVisible();
  });
});
