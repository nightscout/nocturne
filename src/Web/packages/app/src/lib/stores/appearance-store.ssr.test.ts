import { describe, it, expect } from "vitest";
import { render } from "svelte/server";
import type { UserDisplayPreferences } from "$lib/api";
import Harness from "./appearance-ssr-harness.svelte";
import { glucoseUnits } from "./appearance-store.svelte";

const mmol: UserDisplayPreferences = { glucoseUnits: "mmol", timeFormat: "24" };

async function ssr(layers: UserDisplayPreferences[]): Promise<string> {
  return (await render(Harness, { props: { layers } })).body;
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
