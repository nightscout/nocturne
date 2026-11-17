import { describe, expect, it } from "vitest";
import { formCoerce } from "#lib/api/generated/form-utils.generated.js";
import { PatientRecordSchema } from "#lib/api/generated/schemas.js";

// The clinical form posts an unset select as "", which the C# enum would reject.
describe("saving the clinical form", () => {
  it("drops an unset diabetes type instead of sending an empty enum value", () => {
    const parsed = formCoerce(PatientRecordSchema).safeParse({
      diabetesType: "",
      diabetesTypeOther: "",
      sex: "",
      preferredName: "Sam",
    });

    expect(parsed.success).toBe(true);
    expect(parsed.data).toEqual({ preferredName: "Sam" });
  });
});
