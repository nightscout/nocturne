import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import supportedLocales from "../../../../../../supportedLocales.json";
import { REGION_FORMATS } from "#lib/stores/appearance-store.svelte.js";
import { firstDayOfWeek } from "./calendar-date";

const fixture: Record<string, number> = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../../../../../../../tests/Fixtures/first-day-of-week.json", import.meta.url)),
    "utf-8"
  )
);

describe("first day of week parity with the backend", () => {
  it("lists every region the app offers and every display language", () => {
    expect(Object.keys(fixture).sort()).toEqual(
      [...REGION_FORMATS.filter((tag) => tag !== ""), ...supportedLocales].sort()
    );
  });

  it.each(Object.entries(fixture))("resolves %s to day %i as the C# table does", (tag, day) => {
    expect(firstDayOfWeek(tag)).toBe(day);
  });
});
