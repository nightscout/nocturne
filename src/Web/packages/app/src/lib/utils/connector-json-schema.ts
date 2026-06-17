export interface JsonSchemaProperty {
  type: string;
  title?: string;
  description?: string;
  default?: unknown;
  enum?: string[];
  minimum?: number;
  maximum?: number;
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  format?: string;
  /** Environment variable name for this property (x-envVar extension) */
  "x-envVar"?: string;
  /** Category for UI grouping (x-category extension) */
  "x-category"?: string;
  /** Whether this property is hidden from the UI (x-hidden extension) */
  "x-hidden"?: boolean;
  /** Whether this property holds a secret (x-secret extension) */
  "x-secret"?: boolean;
}

export interface JsonSchema {
  $schema?: string;
  type: string;
  title?: string;
  description?: string;
  properties: Record<string, JsonSchemaProperty>;
  required?: string[];
  categories?: Record<string, string[]>;
  secrets?: string[];
}

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
  const schema = (result &&
    typeof result === "object" &&
    "rootElement" in (result as any) &&
    (result as any).rootElement != null
    ? (result as any).rootElement
    : result) as Partial<JsonSchema> | null;

  if (
    !schema ||
    typeof schema !== "object" ||
    !schema.properties ||
    typeof schema.properties !== "object" ||
    Object.keys(schema.properties).length === 0
  ) {
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

  return schema as JsonSchema;
}

