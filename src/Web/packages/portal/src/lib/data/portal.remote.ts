import { query, command } from "$app/server";
import { z } from "zod";

// Schema definitions
const emptySchema = z.object({});

const generateRequestSchema = z.object({
  setupType: z.enum(["fresh", "migrate", "compatibility-proxy"]),
  migration: z.object({
    nightscoutUrl: z.string(),
    nightscoutApiSecret: z.string(),
  }).optional(),
  compatibilityProxy: z.object({
    nightscoutUrl: z.string(),
    nightscoutApiSecret: z.string(),
  }).optional(),
  postgres: z.object({
    useContainer: z.boolean(),
    connectionString: z.string().optional(),
  }),
  optionalServices: z.object({
    watchtower: z.boolean(),
  }),
  connectors: z.array(z.object({
    type: z.string(),
    config: z.record(z.string(), z.string()),
  })),
});

// Infer types from schemas - no casting
export type GenerateRequest = z.infer<typeof generateRequestSchema>;

// Response schemas for type safety
const connectorFieldSchema = z.object({
  name: z.string(),
  envVar: z.string(),
  type: z.enum(["string", "password", "boolean", "select", "number"]),
  required: z.boolean(),
  description: z.string(),
  default: z.string().optional(),
  options: z.array(z.string()).optional(),
});

const connectorMetadataSchema = z.object({
  type: z.string(),
  displayName: z.string(),
  category: z.string(),
  description: z.string(),
  icon: z.string(),
  fields: z.array(connectorFieldSchema),
});

const connectorsResponseSchema = z.object({
  connectors: z.array(connectorMetadataSchema),
});

export type ConnectorField = z.infer<typeof connectorFieldSchema>;
export type ConnectorMetadata = z.infer<typeof connectorMetadataSchema>;

// Remote functions
export const getConnectors = query(emptySchema, async () => {
  const response = await fetch("/api/connectors");
  if (!response.ok) {
    throw new Error(`Failed to fetch connectors: ${response.statusText}`);
  }
  const data: unknown = await response.json();
  const parsed = connectorsResponseSchema.parse(data);
  return parsed.connectors;
});

export const generateConfig = command(generateRequestSchema, async (request) => {
  const response = await fetch("/api/generate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    const error = await response.text();
    throw new Error(`Failed to generate config: ${error}`);
  }

  return response.blob();
});
