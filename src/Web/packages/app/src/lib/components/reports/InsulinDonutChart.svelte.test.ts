import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import InsulinDonutChart from "./InsulinDonutChart.svelte";

const boluses = [{ insulin: 4.5 }, { insulin: 3.25 }, { insulin: 2 }];

const props = {
  boluses,
  totalBasal: 6.35,
  totalBolus: 12.75,
  manualBolus: 9.75,
  automaticBolus: 3,
  scheduledBasal: 4,
  additionalBasal: 2.35,
  totalInsulin: 19.1,
};

async function clickArc(name: string) {
  const arc = page.getByRole("button", { name }).element();
  arc.dispatchEvent(new MouseEvent("click", { bubbles: true }));
}

function slices(container: HTMLElement) {
  return container.querySelectorAll("path[role='button'], path[class*='cursor-pointer']");
}

describe("InsulinDonutChart", () => {
  it("opens on basal against bolus with the backend total", async () => {
    const { container } = render(InsulinDonutChart, { props });

    await expect.element(page.getByText("19.1U")).toBeVisible();
    await expect.element(page.getByText("Basal 6.3U")).toBeVisible();
    await expect.element(page.getByText("Bolus 12.8U")).toBeVisible();
    expect(slices(container)).toHaveLength(2);
  });

  it("splits bolus into manual and automatic from the backend figures", async () => {
    render(InsulinDonutChart, { props });

    await clickArc("Bolus 12.8U");

    await expect.element(page.getByText("Manual Bolus 9.8U")).toBeVisible();
    await expect.element(page.getByText("Automatic Bolus 3.0U")).toBeVisible();
    await expect.element(page.getByText("12.8U", { exact: true })).toBeVisible();
  });

  it("splits basal into scheduled and additional", async () => {
    render(InsulinDonutChart, { props });

    await clickArc("Basal 6.3U");

    await expect.element(page.getByText("Scheduled Basal 4.0U")).toBeVisible();
    await expect.element(page.getByText("Additional Basal 2.4U")).toBeVisible();
  });

  it("draws one slice per manual bolus and opens the clicked bolus", async () => {
    const onBolusClick = vi.fn();
    const { container } = render(InsulinDonutChart, { props: { ...props, onBolusClick } });

    await clickArc("Bolus 12.8U");
    await clickArc("Manual Bolus 9.8U");

    await expect.element(page.getByRole("button", { name: "Bolus 3.3U" })).toBeVisible();
    expect(slices(container)).toHaveLength(3);

    await clickArc("Bolus 3.3U");
    expect(onBolusClick).toHaveBeenCalledExactlyOnceWith(boluses[1]);
  });

  it("drills from the keyboard", async () => {
    render(InsulinDonutChart, { props });

    (page.getByRole("button", { name: "Bolus 12.8U" }).element() as SVGElement).dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", bubbles: true })
    );

    await expect.element(page.getByText("Automatic Bolus 3.0U")).toBeVisible();
  });

  it("steps back to any ancestor from the breadcrumb", async () => {
    const { container } = render(InsulinDonutChart, { props });

    await clickArc("Bolus 12.8U");
    await clickArc("Manual Bolus 9.8U");
    await expect.element(page.getByText("Manual:")).toBeVisible();

    await page.getByRole("navigation").getByRole("button", { name: "Bolus" }).click();
    await expect.element(page.getByText("Bolus:")).toBeVisible();
    await expect.element(page.getByText("Manual:")).not.toBeInTheDocument();
    expect(slices(container)).toHaveLength(1);

    await page.getByRole("navigation").getByRole("button", { name: "All" }).click();
    await expect.element(page.getByText("Total:")).toBeVisible();
    await expect.element(page.getByText("19.1U")).toBeVisible();
  });

  it("lists only manual bolus in the key at the manual level", async () => {
    render(InsulinDonutChart, { props });

    await clickArc("Bolus 12.8U");
    await clickArc("Manual Bolus 9.8U");

    await expect.element(page.getByText("Manual:")).toBeVisible();
    await expect.element(page.getByText("Automatic Bolus 3.0U")).not.toBeInTheDocument();
    await expect.element(page.getByText("Manual Bolus 9.8U", { exact: true })).toBeVisible();
  });

  it("returns to All when the drilled level has nothing left to show", async () => {
    const { rerender } = render(InsulinDonutChart, { props });

    await clickArc("Bolus 12.8U");
    await expect.element(page.getByText("Bolus:")).toBeVisible();

    await rerender({
      ...props,
      boluses: [],
      totalBolus: 0,
      manualBolus: 0,
      automaticBolus: 0,
      totalInsulin: 6.35,
    });

    await expect.element(page.getByText("Total:")).toBeVisible();
    await expect.element(page.getByText("6.3U", { exact: true }).first()).toBeVisible();
  });

  it("alternates bolus shades across the slices it draws", async () => {
    const { container } = render(InsulinDonutChart, {
      props: {
        ...props,
        boluses: [{ insulin: 1 }, { insulin: 0 }, { insulin: 2 }, { insulin: 3 }],
        onBolusClick: vi.fn(),
      },
    });

    await clickArc("Bolus 12.8U");
    await clickArc("Manual Bolus 9.8U");
    await expect.element(page.getByText("Manual:")).toBeVisible();

    const fills = [...slices(container)].map((arc) => arc.getAttribute("fill"));
    expect(fills).toHaveLength(3);
    expect(fills[0]).not.toBe(fills[1]);
    expect(fills[1]).not.toBe(fills[2]);
    expect(fills[0]).toBe(fills[2]);
  });

  it("does not drill into automatic boluses", async () => {
    const { container } = render(InsulinDonutChart, { props });

    await clickArc("Bolus 12.8U");
    await expect.element(page.getByText("Automatic Bolus 3.0U")).toBeVisible();

    expect(slices(container)).toHaveLength(1);
    const automatic = container.querySelectorAll("path:not([role='button'])");
    expect(automatic.length).toBeGreaterThan(0);
    automatic.forEach((arc) => (arc as SVGElement).dispatchEvent(new MouseEvent("click", { bubbles: true })));

    await expect.element(page.getByText("Bolus:")).toBeVisible();
    await expect.element(page.getByText("Automatic Bolus 3.0U")).toBeVisible();
  });

  it("draws the manual arc from the total when the backend sends no bolus records", async () => {
    const { container } = render(InsulinDonutChart, { props: { ...props, boluses: [] } });

    await clickArc("Bolus 12.8U");
    await clickArc("Manual Bolus 9.8U");

    await expect.element(page.getByText("Manual:")).toBeVisible();
    await expect.element(page.getByText("9.8U", { exact: true })).toBeVisible();
    expect(container.querySelectorAll("path.lc-arc-line")).toHaveLength(1);
    expect(slices(container)).toHaveLength(0);
  });

  it("shows no total when the backend reports no insulin", async () => {
    const { container } = render(InsulinDonutChart, {
      props: {
        boluses: [],
        totalBasal: 0,
        totalBolus: 0,
        manualBolus: 0,
        automaticBolus: 0,
        scheduledBasal: 0,
        additionalBasal: 0,
        totalInsulin: 0,
      },
    });

    await expect.element(page.getByText("—")).toBeVisible();
    expect(container.textContent).not.toContain("Total:");
  });
});
