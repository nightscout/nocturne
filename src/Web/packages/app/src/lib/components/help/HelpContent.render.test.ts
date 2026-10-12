import { beforeAll, describe, expect, it } from "vitest";
import { render } from "svelte/server";
import Harness from "./HelpContentHarness.test.svelte";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../../locales/main.loader.server.svelte.js";
import { locales } from "../../../../../../locales/data.js";

beforeAll(async () => {
  await loadLocales(main.key, main.loadCount, main.loadCatalog, locales);
});
const content = (path: string, locale = "en") =>
  runWithLocale(locale, () => render(Harness, { props: { path } }).body);

describe("translation-aware help factories", () => {
  it("renders report content through the production translation transform", async () => {
    const body = await content("/reports/agp");
    expect(body).toContain("Ambulatory Glucose Profile");
    expect(body).toContain("Where the information comes from");
    expect(body).toContain("percentile");
  });
  it("keeps English Google setup steps available through the transform", async () => {
    const body = await content("/settings/connectors/google-health");
    expect(body).toContain("Create the Web application client");
    expect(body).toContain("Save selection and import");
    expect(body).toContain("required on first setup");
  });
  it("has readable English help fallbacks in another interface language", async () => {
    const body = await content("/settings/connectors/google-health", "nl");
    expect(body).toContain("required on first setup");
    expect(body).toContain("Create the Web application client");
  });
});
