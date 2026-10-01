import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

const { copyToClipboard } = vi.hoisted(() => ({ copyToClipboard: vi.fn() }));

vi.mock("$lib/utils", () => ({ copyToClipboard }));

import RecoveryCodes from "./RecoveryCodes.svelte";

const codes = ["AAAA-BBBB", "CCCC-DDDD", "EEEE-FFFF", "GGGG-HHHH"];

describe("RecoveryCodes save gate", () => {
  beforeEach(() => {
    copyToClipboard.mockReset();
  });

  it("keeps Done disabled until the codes are confirmed written down, then continues", async () => {
    const onContinue = vi.fn();
    render(RecoveryCodes, {
      props: { codes, onContinue, continueLabel: "Done" },
    });

    const done = page.getByRole("button", { name: "Done" });
    await expect.element(done).toBeVisible();
    await expect.element(done).toBeDisabled();

    await page.getByRole("checkbox").click();

    await expect.element(done).toBeEnabled();
    await done.click();
    expect(onContinue).toHaveBeenCalledOnce();
  });

  it("does not open the gate on a failed clipboard write", async () => {
    copyToClipboard.mockResolvedValue(false);
    const onContinue = vi.fn();
    render(RecoveryCodes, {
      props: { codes, onContinue, continueLabel: "Done" },
    });

    await page.getByRole("button", { name: "Copy recovery codes" }).click();

    await expect
      .element(page.getByText(/browser blocked clipboard access/))
      .toBeVisible();
    await expect
      .element(page.getByRole("button", { name: "Done" }))
      .toBeDisabled();
  });
});
