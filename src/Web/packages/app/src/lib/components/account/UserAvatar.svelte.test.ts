import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
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
});
