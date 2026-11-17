import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";

vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({
    fromDay: "2025-06-01",
    toDay: "2025-06-14",
    startDate: new Date(2025, 5, 1),
    endDate: new Date(2025, 5, 14, 23, 59, 59, 999),
    dayCount: 14,
  }),
}));

vi.mock(
  "./BasalAnalysisContent.svelte",
  () => import("#lib/test-stubs/BasalAnalysisContent.test-stub.svelte")
);

import BasalAnalysisPage from "./+page.svelte";

describe("basal analysis report", () => {
  it("hands the content the picked days, leaving the instants to the server's reading of the patient's zone", async () => {
    render(BasalAnalysisPage);

    await expect
      .element(page.getByTestId("basal-analysis"))
      .toHaveAttribute("data-from", "2025-06-01");
    await expect
      .element(page.getByTestId("basal-analysis"))
      .toHaveAttribute("data-to", "2025-06-14");
  });
});
