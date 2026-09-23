import type { z } from "zod";

/**
 * Types a generated request schema's output as the NSwag DTO it validates.
 *
 * The generated schemas are built with `z.fromJSONSchema`, which types its
 * output as `unknown`. They check the same OpenAPI shape the NSwag interface is
 * emitted from. One known difference: a `date-time` field is an ISO string at
 * runtime but `Date` on the interface. The client's `JSON.stringify` sends
 * both identically.
 */
export function dtoSchema<T>(schema: z.ZodType): z.ZodType<T, unknown> {
  // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- z.fromJSONSchema infers unknown; the schema validates T's OpenAPI shape at runtime
  return schema as z.ZodType<T, unknown>;
}
