import { describe, expect, it } from "vitest";
import { z } from "zod";
import { formCoerce } from "#lib/api/generated/form-utils.generated.js";
import { tolerateKitFormKey } from "./kit-form-key";

async function validate(schema: z.ZodType, value: unknown) {
  return tolerateKitFormKey(schema)["~standard"].validate(value);
}

describe("tolerateKitFormKey", () => {
  it("drops the id a keyed form adds when the strict schema has none", async () => {
    const result = await validate(formCoerce(z.strictObject({ name: z.string() })), {
      name: "G7",
      id: "create",
    });

    expect(result.issues).toBeUndefined();
    expect(result).toMatchObject({ value: { name: "G7" } });
    expect((result as { value: object }).value).not.toHaveProperty("id");
  });

  it("keeps the id a schema declares", async () => {
    const result = await validate(
      formCoerce(z.strictObject({ id: z.string(), request: z.strictObject({ name: z.string() }) })),
      { id: "0192f0e4", request: { name: "G7" } }
    );

    expect(result).toMatchObject({ value: { id: "0192f0e4", request: { name: "G7" } } });
  });

  it("still refuses an unknown key other than id", async () => {
    const result = await validate(formCoerce(z.strictObject({ name: z.string() })), {
      name: "G7",
      colour: "blue",
    });

    expect(result.issues).toHaveLength(1);
  });

  it("still refuses an id that comes with another unknown key", async () => {
    const result = await validate(formCoerce(z.strictObject({ name: z.string() })), {
      name: "G7",
      id: "create",
      colour: "blue",
    });

    expect(result.issues).toHaveLength(1);
  });

  it("still refuses an id that comes with another issue", async () => {
    const result = await validate(formCoerce(z.strictObject({ name: z.string() })), {
      id: "create",
    });

    expect(result.issues?.length).toBeGreaterThan(0);
  });
});
