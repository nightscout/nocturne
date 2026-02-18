export type RemoteType = 'query' | 'command';

export interface ParameterInfo {
  name: string;
  in: 'query' | 'path' | 'header';
  required: boolean;
  type: string;
  enumName?: string;
}

/**
 * Represents an inline request body that doesn't have a named $ref schema.
 * Used for types like Dictionary<string, string> which become { type: "object", additionalProperties: ... }.
 */
export interface InlineRequestBody {
  /** Zod schema expression (e.g., "z.record(z.string(), z.string())") */
  zodSchema: string;
  /** TypeScript type for the NSwag client cast (e.g., "{ [key: string]: string; }") */
  tsType: string;
}

export interface OperationInfo {
  operationId: string;
  tag: string;
  method: string;
  path: string;
  remoteType: RemoteType;
  invalidates: string[];
  parameters: ParameterInfo[];
  requestBodySchema?: string;
  requestBodyRequired?: boolean;
  isArrayBody?: boolean;
  /** Inline request body for schemas that don't have a named $ref (e.g., Dictionary<string, string>) */
  inlineRequestBody?: InlineRequestBody;
  responseSchema?: string;
  isVoidResponse: boolean;
  summary?: string;
  clientPropertyName?: string;
}

export interface ParsedSpec {
  operations: OperationInfo[];
  tags: string[];
}
