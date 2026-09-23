import { z } from "zod";
import { isRecord } from "./type-guards";

/** Malformed optional keywords are dropped rather than failing the whole schema. */
const optional = <T extends z.ZodType>(schema: T) => schema.optional().catch(undefined);

const JsonSchemaPropertySchema = z.looseObject({
  type: z.string(),
  title: optional(z.string()),
  description: optional(z.string()),
  default: z.unknown().optional(),
  enum: optional(z.array(z.string())),
  minimum: optional(z.number()),
  maximum: optional(z.number()),
  minLength: optional(z.number()),
  maxLength: optional(z.number()),
  pattern: optional(z.string()),
  format: optional(z.string()),
  /** Environment variable name for this property (x-envVar extension) */
  "x-envVar": optional(z.string()),
  /** Category for UI grouping (x-category extension) */
  "x-category": optional(z.string()),
  /** Whether this property is hidden from the UI (x-hidden extension) */
  "x-hidden": optional(z.boolean()),
  /** Whether this property holds a secret (x-secret extension) */
  "x-secret": optional(z.boolean()),
});

export type JsonSchemaProperty = z.infer<typeof JsonSchemaPropertySchema>;

const JsonSchemaSchema = z.looseObject({
  $schema: optional(z.string()),
  type: z.string().catch("object"),
  title: optional(z.string()),
  description: optional(z.string()),
  properties: z.record(z.string(), JsonSchemaPropertySchema),
  required: optional(z.array(z.string())),
  categories: optional(z.record(z.string(), z.array(z.string()))),
  secrets: optional(z.array(z.string())),
});

export type JsonSchema = z.infer<typeof JsonSchemaSchema>;

/**
 * The API returns `JsonDocument` for schemas. NSwag sometimes represents this as
 * `{ rootElement: ... }`, and sometimes the object is returned directly.
 *
 * This helper normalizes the response into a JSON schema object, and provides a
 * minimal "not configurable" schema when the payload is empty.
 */
export function normalizeConnectorJsonSchema(
  result: unknown,
  connectorName: string,
): JsonSchema {
  const candidate =
    isRecord(result) && result.rootElement != null ? result.rootElement : result;
  const parsed = JsonSchemaSchema.safeParse(candidate);

  if (!parsed.success || Object.keys(parsed.data.properties).length === 0) {
    return {
      type: "object",
      title: connectorName,
      description:
        "This connector does not support runtime configuration. Configure via environment variables.",
      properties: {},
      required: [],
      categories: {},
      secrets: [],
    };
  }

  return parsed.data;
}
