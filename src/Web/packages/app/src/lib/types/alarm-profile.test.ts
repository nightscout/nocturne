import { describe, expect, it } from "vitest";
import { UserAlarmConfigurationSchema } from "$lib/api/generated/schemas";
import { createDefaultUserAlarmConfiguration } from "$lib/types/alarm-profile";

/**
 * The settings store sends this factory's output as the alarm configuration, so any field it
 * declares that the server model lacks is dropped on save and reverted by the server's echo.
 */
describe("createDefaultUserAlarmConfiguration", () => {
  it("builds a payload the server model accepts in full", () => {
    const payload = JSON.parse(
      JSON.stringify(createDefaultUserAlarmConfiguration())
    );

    const result = UserAlarmConfigurationSchema.safeParse(payload);

    expect(result.error?.issues ?? []).toEqual([]);
  });
});
