import { describe, expect, it } from "vitest";
import { render } from "svelte/server";
import Harness from "$lib/test-fixtures/A1cPreferencesProbe.svelte";

describe("A1c preferences under the production translation transform", () => {
  it("keeps persisted preference values and exported stores stable", async () => {
    const { body } = await render(Harness, {
      props: { layers: [{ a1cName: "A1c", a1cUnits: "mmol/mol" }] },
    });
    expect(body).toContain("A1c / eA1c: 53 mmol/mol");
  });
});
