import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import Harness from "./InputOTP.test-harness.svelte";

describe("InputOTP", () => {
  it("runs onComplete once when the callback reads and writes its own state", async () => {
    const onCompleteCalled = vi.fn();
    render(Harness, { onCompleteCalled });

    await page.getByRole("textbox").fill("1234");

    await expect.element(page.getByTestId("completions")).toHaveTextContent("1");
    expect(onCompleteCalled).toHaveBeenCalledTimes(1);
    expect(onCompleteCalled).toHaveBeenCalledWith(1);
  });
});
