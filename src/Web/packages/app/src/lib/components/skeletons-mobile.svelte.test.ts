import { afterEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { Component } from "svelte";
import CalendarSkeleton from "$lib/components/calendar/CalendarSkeleton.svelte";
import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
import PageBodyLoading from "$lib/components/reports/loop-analysis/PageBodyLoading.svelte";
import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";

const PHONE_WIDTH = 360;
const original = [window.innerWidth, window.innerHeight] as const;

afterEach(async () => {
  await page.viewport(original[0], original[1]);
});

function inScrollport(el: HTMLElement, frame: HTMLElement) {
  for (let node = el.parentElement; node && node !== frame; node = node.parentElement) {
    if (getComputedStyle(node).overflowX !== "visible") return true;
  }
  return false;
}

function overflowing(frame: HTMLElement) {
  const limit = frame.getBoundingClientRect().right;
  return [...frame.querySelectorAll<HTMLElement>("*")]
    .filter((el) => !inScrollport(el, frame))
    .filter((el) => el.getBoundingClientRect().right > limit + 0.5)
    .map((el) => el.className.toString());
}

describe("calendar skeleton on a phone", () => {
  it("fits the viewport and scrolls its grid like the calendar does", async () => {
    await page.viewport(PHONE_WIDTH, 800);
    const { container } = render(CalendarSkeleton);

    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(PHONE_WIDTH);
    expect(overflowing(container)).toEqual([]);

    const scroller = container.querySelector<HTMLElement>(".overflow-x-auto")!;
    expect(scroller.getBoundingClientRect().right).toBeLessThanOrEqual(PHONE_WIDTH);
    expect(scroller.scrollWidth).toBeGreaterThan(scroller.clientWidth);
    expect(container.querySelectorAll(".grid-rows-6 > *")).toHaveLength(6);
  });
});

describe("other page skeletons on a phone", () => {
  it.each([
    ["ReportsSkeleton", ReportsSkeleton],
    ["PageBodyLoading", PageBodyLoading],
    ["SettingsPageSkeleton", SettingsPageSkeleton],
  ] as [string, Component][])("%s fits the viewport", async (_name, Skeleton) => {
    await page.viewport(PHONE_WIDTH, 800);
    const { container } = render(Skeleton);

    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(PHONE_WIDTH);
    expect(overflowing(container)).toEqual([]);
  });
});
