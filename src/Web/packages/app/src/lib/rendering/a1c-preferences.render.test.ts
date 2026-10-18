import { beforeAll, describe, expect, it } from "vitest";
import { render } from "svelte/server";
import Harness from "$lib/test-fixtures/A1cPreferencesProbe.svelte";
import { loadLocales, runWithLocale } from "wuchale/load-utils/server";
import * as main from "../../../../../locales/main.loader.server.svelte.js";
import supportedLocales from "../../../../../supportedLocales.json";

describe("A1c preferences under the production translation transform", () => {
  beforeAll(async () => {
    await loadLocales(
      main.key,
      main.loadCount,
      main.loadCatalog,
      supportedLocales
    );
  });
  it("keeps persisted preference values and exported stores stable", async () => {
    const body = await runWithLocale("en", async () => {
      const result = await render(Harness, {
        props: { layers: [{ a1cName: "A1c", a1cUnits: "mmol/mol" }] },
      });
      return result.body;
    });
    expect(body).toContain("A1c / eA1c: 53 mmol/mol");
  });
  it.each(supportedLocales)(
    "renders measured and estimated names and navigation in %s",
    async (locale) => {
      for (const a1cName of ["HbA1c", "A1c"] as const) {
        for (const a1cUnits of ["percent", "mmol/mol"] as const) {
          const body = await runWithLocale(locale, async () => {
            const result = await render(Harness, {
              props: { layers: [{ a1cName, a1cUnits }] },
            });
            return result.body;
          });
          expect(body).toContain(
            `${a1cName} / e${a1cName}: ${a1cUnits === "percent" ? "7.0%" : "53 mmol/mol"}`
          );
          expect(body).toContain(`<nav>e${a1cName}</nav>`);
          expect(body).toMatch(new RegExp(`<h1>[^<]*${a1cName}[^<]*</h1>`));
          expect(body).not.toContain(`<h1>${a1cName}</h1>`);
          expect(body).toMatch(new RegExp(`<p>[^<]*e${a1cName}[^<]+</p>`));
        }
      }
    }
  );
});
