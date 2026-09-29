import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { createRawSnippet } from "svelte";
import { describe, it, expect } from "vitest";
import SuccessBanner from "./SuccessBanner.svelte";

const text = (message: string) =>
  createRawSnippet(() => ({ render: () => `<span>${message}</span>` }));

describe("SuccessBanner", () => {
  it("paints the confirmation wash behind a save", async () => {
    render(SuccessBanner, { children: text("Passkey added successfully.") });

    const banner = page.getByRole("status");
    await expect.element(banner).toHaveTextContent("Passkey added successfully.");
    expect(banner.element().querySelector("canvas")).not.toBeNull();
  });

  it("keeps a removal plain, with no wash", async () => {
    render(SuccessBanner, { wash: false, children: text("Passkey removed.") });

    const banner = page.getByRole("status");
    await expect.element(banner).toHaveTextContent("Passkey removed.");
    expect(banner.element().querySelector("canvas")).toBeNull();
  });
});
