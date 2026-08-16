import { describe, it, expect } from "vitest";
import { render } from "svelte/server";
import type { UserDisplayPreferences } from "$lib/api";
import Harness from "./appearance-ssr-harness.svelte";
import {
  glucoseUnits,
  preferredLanguage,
  resolveLanguage,
  type SupportedLocale,
} from "./appearance-store.svelte";

const mmol: UserDisplayPreferences = { glucoseUnits: "mmol", timeFormat: "24" };

async function ssr(
  layers: UserDisplayPreferences[],
  language?: SupportedLocale
): Promise<string> {
  return (await render(Harness, { props: { layers, language } })).body;
}

describe("appearance-store SSR resolution", () => {
  it("renders the request's units, not the module default", async () => {
    const body = await ssr([mmol]);

    expect(body).toContain("5.6");
    expect(body).toContain("mmol/L");
    expect(body).toContain("3.9-10 mmol/L");
    expect(body).not.toContain("mg/dL");
  });

  it("falls back to defaults when the request carries no preferences", async () => {
    const body = await ssr([]);

    expect(body).toContain("100");
    expect(body).toContain("mg/dL");
    expect(body).toContain("70-180 mg/dL");
    expect(body).not.toContain("mmol");
  });

  it("keeps concurrent requests isolated", async () => {
    const [mmolBody, defaultBody] = await Promise.all([ssr([mmol]), ssr([])]);

    expect(mmolBody).toContain("mmol/L");
    expect(defaultBody).toContain("mg/dL");
    expect(defaultBody).not.toContain("mmol");
    // The shared module state is never written to by a server render.
    expect(glucoseUnits.current).toBe("mg/dl");
  });

  it("resolves each field from the highest-precedence layer that defines it", async () => {
    const body = await ssr([{ timeFormat: "24" }, { glucoseUnits: "mmol", timeFormat: "12" }]);

    expect(body).toContain("mmol/L");
    expect(body).toContain(">24<");
  });

  it("applies every preference layer's own fields, not just the first layer's", async () => {
    const body = await ssr([{ glucoseUnits: "mmol" }, { timeFormat: "24" }]);

    expect(body).toContain("mmol/L");
    expect(body).toContain(">24<");
  });
});

describe("language SSR resolution", () => {
  it("formats dates and numbers in the request's language, not the module default", async () => {
    const body = await ssr([], "de");

    expect(body).toContain("Do., 31. Dez.");
    expect(body).toContain("vor 5 Min.");
    expect(body).not.toContain("Thu, Dec 31");
  });

  it("falls back to English when the request carries no language", async () => {
    const body = await ssr([]);

    expect(body).toContain("Thu, Dec 31");
    expect(body).toContain("5 min. ago");
  });

  it("keeps concurrent requests isolated", async () => {
    const [german, fallback] = await Promise.all([ssr([], "de"), ssr([])]);

    expect(german).toContain("Do., 31. Dez.");
    expect(fallback).toContain("Thu, Dec 31");
    // The shared module state is never written to by a server render.
    expect(preferredLanguage.current).toBe("en");
  });

  it("lets a regional format outrank the language for date ordering", async () => {
    const body = await ssr([{ regionFormat: "en-GB" }], "de");

    expect(body).toContain(">en-GB<");
    expect(body).toContain("Thu 31 Dec");
    // The language still shows through as the user's own choice.
    expect(body).toContain(">de<");
  });
});

describe("resolveLanguage", () => {
  it("prefers a saved subject preference over the mirrored cookie", () => {
    expect(resolveLanguage("fr", "de")).toBe("fr");
  });

  it("skips unset and unsupported candidates", () => {
    expect(resolveLanguage(null, "klingon", undefined, "de")).toBe("de");
  });

  it("falls back to English when nothing usable is offered", () => {
    expect(resolveLanguage(null, undefined, "")).toBe("en");
  });
});
