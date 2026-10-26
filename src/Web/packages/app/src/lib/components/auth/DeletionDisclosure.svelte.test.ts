import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import { ScopeDeletionAccess } from "$lib/api";
import DeletionDisclosure from "./DeletionDisclosure.svelte";

const FULL = "This app is requesting full access, including the ability to delete data.";
const READ_WRITE = "This app can change and delete the data in the categories above.";
const NONE = "This app cannot delete your data.";

describe("DeletionDisclosure", () => {
  it.each([
    [ScopeDeletionAccess.Full, FULL],
    [ScopeDeletionAccess.ReadWrite, READ_WRITE],
    [ScopeDeletionAccess.None, NONE],
  ])("for %s says only what that tier allows", async (access, shown) => {
    render(DeletionDisclosure, { props: { access } });

    await expect.element(page.getByText(shown)).toBeVisible();
    for (const other of [FULL, READ_WRITE, NONE].filter((line) => line !== shown)) {
      await expect.element(page.getByText(other)).not.toBeInTheDocument();
    }
  });

  it.each([undefined, null])("makes no claim when the access is %s", async (access) => {
    const { container } = render(DeletionDisclosure, { props: { access } });

    expect(container.textContent?.trim()).toBe("");
  });
});
