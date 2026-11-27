import { beforeAll, describe, expect, it } from "vitest";
import { loadLocales } from "wuchale/load-utils/server";
import * as main from "../../../../../../../locales/main.loader.server.svelte.js";
import { DayOfWeek } from "#lib/api/index.js";
import type { SupportedLocale } from "#lib/stores/appearance-store.svelte.js";
import { renderInLanguage } from "#lib/test-fixtures/render-in-language.js";
import AppliedFiltersSummary from "./AppliedFiltersSummary.svelte";

const applied = {
  days: [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday],
  excludeExercise: true,
  excludeOpenLoop: true,
  excludeIllness: true,
};

const summary = (language: SupportedLocale) =>
  renderInLanguage(language, { component: AppliedFiltersSummary, viewProps: { applied } });

describe("the applied filters under the production translation transform", () => {
  beforeAll(async () => {
    await loadLocales(main.key, main.loadCount, main.loadCatalog, ["en", "de"]);
  });

  it("joins the days and the time left out with the interface language's own list words", async () => {
    const body = await summary("de");

    expect(body).toContain("Tage: Mo, Di und Mi.");
    expect(body).toContain("Bewegung, Krankheit und Zeit außerhalb des Closed Loop.");
    expect(body).not.toContain(" and ");
  });

  it("keeps English lists in English", async () => {
    expect(await summary("en")).toMatch(/Days: Mon, Tue, and Wed\./);
  });
});
