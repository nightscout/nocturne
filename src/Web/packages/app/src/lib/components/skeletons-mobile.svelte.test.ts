import { afterEach, describe, expect, it } from "vitest";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import type { Component } from "svelte";
import CalendarSkeleton from "#lib/components/calendar/CalendarSkeleton.svelte";
import ReportsSkeleton from "#lib/components/reports/ReportsSkeleton.svelte";
import PageBodyLoading from "#lib/components/reports/loop-analysis/PageBodyLoading.svelte";
import SettingsPageSkeleton from "#lib/components/settings/SettingsPageSkeleton.svelte";

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
  it("fits all seven days in the viewport, as the calendar does", async () => {
    await page.viewport(PHONE_WIDTH, 800);
    const { container } = render(CalendarSkeleton);

    expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(PHONE_WIDTH);
    expect(overflowing(container)).toEqual([]);

    const weeks = container.querySelectorAll<HTMLElement>(".grid-rows-6 > *");
    expect(weeks).toHaveLength(6);
    for (const week of weeks) {
      expect(week.getBoundingClientRect().right).toBeLessThanOrEqual(PHONE_WIDTH);
      expect(week.scrollWidth).toBeLessThanOrEqual(week.clientWidth);
    }
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
