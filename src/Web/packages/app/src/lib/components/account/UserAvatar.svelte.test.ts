import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import UserAvatar, { initialsOf } from "./UserAvatar.svelte";

describe("initialsOf", () => {
  it("takes the first letter of the first two words", () => {
    expect(initialsOf("alex carer smith")).toBe("AC");
  });

  it("tolerates repeated spaces", () => {
    expect(initialsOf("  Sam   Lee ")).toBe("SL");
  });

  it("falls back to a question mark when there is no name", () => {
    expect(initialsOf(undefined)).toBe("?");
    expect(initialsOf("   ")).toBe("?");
  });
});

describe("UserAvatar", () => {
  it("draws the initials over a wash when there is no picture", async () => {
    const { container } = render(UserAvatar, { props: { name: "Sam Lee" } });

    await expect.element(page.getByText("SL")).toBeVisible();
    expect(container.querySelector("[data-slot='avatar-fallback'] canvas")).not.toBeNull();
  });
  it("paints no wash behind a picture that loaded", async () => {
    const pixel =
      "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    const { container } = render(UserAvatar, { props: { name: "Sam Lee", src: pixel } });

    await expect.element(page.getByRole("img")).toBeInTheDocument();
    expect(container.querySelector("canvas")).toBeNull();
  });

  it("falls back to the wash when the picture fails to load", async () => {
    const { container } = render(UserAvatar, { props: { name: "Sam Lee", src: "data:image/png;base64,AAAA" } });

    await expect.element(page.getByText("SL")).toBeVisible();
    await vi.waitFor(() => expect(container.querySelector("[data-slot='avatar-fallback'] canvas")).not.toBeNull());
  });
});
