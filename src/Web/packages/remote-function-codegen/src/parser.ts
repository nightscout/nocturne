import type { OpenAPIV3 } from 'openapi-types';
import type { OperationInfo, ParsedSpec, ParameterInfo, RemoteType } from './types.js';

// Extended operation type to include our custom extensions
type OperationWithExtensions = OpenAPIV3.OperationObject & {
  'x-remote-type'?: RemoteType;
  'x-remote-invalidates'?: string[];
  'x-client-property'?: string;
};

export function parseOpenApiSpec(spec: OpenAPIV3.Document): ParsedSpec {
  const operations: OperationInfo[] = [];
  const tagsSet = new Set<string>();

  for (const [path, pathItem] of Object.entries(spec.paths ?? {})) {
    if (!pathItem) continue;

    const methods = ['get', 'post', 'put', 'patch', 'delete'] as const;

    for (const method of methods) {
      const operation = pathItem[method] as OperationWithExtensions | undefined;
      if (!operation) continue;

      const remoteType = operation['x-remote-type'];
      if (!remoteType) continue;

      const tag = operation.tags?.[0] ?? 'Default';
      tagsSet.add(tag);

      const invalidates = operation['x-remote-invalidates'] ?? [];

      const parameters = parseParameters(
        operation.parameters ?? [],
        pathItem.parameters ?? [],
        spec.components
      );
      const requestBodyResult = parseRequestBody(operation.requestBody as OpenAPIV3.RequestBodyObject | undefined);
      const requestBodySchema = requestBodyResult?.schema;
      const isArrayBody = requestBodyResult?.isArray ?? false;

      // Check success response codes in priority order (200, 201, 202)
      const responseSchema =
        parseResponse(operation.responses?.['200'] as OpenAPIV3.ResponseObject | undefined) ??
        parseResponse(operation.responses?.['201'] as OpenAPIV3.ResponseObject | undefined) ??
        parseResponse(operation.responses?.['202'] as OpenAPIV3.ResponseObject | undefined);

      // Detect void response: commands with no JSON response body (204 No Content, or 200 with no body)
      const isVoidResponse = !responseSchema && remoteType === 'command';

      operations.push({
        operationId: operation.operationId ?? `${method}_${path}`,
        tag,
        method,
        path,
        remoteType,
        invalidates,
        parameters,
        requestBodySchema,
        requestBodyRequired: requestBodyResult?.required,
        isArrayBody,
        responseSchema,
        isVoidResponse,
        summary: operation.summary,
        clientPropertyName: operation['x-client-property'],
      });
    }
  }

  return {
    operations,
    tags: Array.from(tagsSet),
  };
}

function parseParameters(
  opParams: (OpenAPIV3.ParameterObject | OpenAPIV3.ReferenceObject)[],
  pathParams: (OpenAPIV3.ParameterObject | OpenAPIV3.ReferenceObject)[],
  components?: OpenAPIV3.ComponentsObject
): ParameterInfo[] {
  const allParams = [...pathParams, ...opParams];
  const result: ParameterInfo[] = [];

  for (const param of allParams) {
    if ('$ref' in param) continue;

    const schema = param.schema as OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject | undefined;
    const resolved = resolveSchema(schema, components);

    const paramInfo: ParameterInfo = {
      name: param.name,
      in: param.in as 'query' | 'path' | 'header',
      required: param.required ?? false,
      type: getSchemaType(resolved),
    };

    // Check if the resolved schema is an enum
    if (resolved?.enum) {
      const enumName = findEnumName(schema, components);
      if (enumName) {
        paramInfo.enumName = enumName;
      }
    }

    result.push(paramInfo);
  }

  return result;
}

/**
 * Resolve a schema through $ref and oneOf to get the underlying schema object.
 */
function resolveSchema(
  schema: OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject | undefined,
  components?: OpenAPIV3.ComponentsObject
): OpenAPIV3.SchemaObject | undefined {
  if (!schema) return undefined;

  if ('$ref' in schema) {
    const refName = schema.$ref.split('/').pop();
    if (refName && components?.schemas?.[refName]) {
      return components.schemas[refName] as OpenAPIV3.SchemaObject;
    }
    return undefined;
  }

  // Handle oneOf wrapping (NSwag nullable enums)
  const schemaObj = schema as OpenAPIV3.SchemaObject;
  if (schemaObj.oneOf) {
    for (const item of schemaObj.oneOf) {
      const resolved = resolveSchema(
        item as OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject,
        components
      );
      if (resolved?.type || resolved?.enum) return resolved;
    }
  }

  return schemaObj;
}

/**
 * Find the enum type name from a schema that may be wrapped in oneOf/$ref.
 */
function findEnumName(
  schema: OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject | undefined,
  components?: OpenAPIV3.ComponentsObject
): string | undefined {
  if (!schema) return undefined;

  if ('$ref' in schema) {
    const refName = schema.$ref.split('/').pop();
    if (refName && components?.schemas?.[refName]) {
      const resolved = components.schemas[refName] as OpenAPIV3.SchemaObject;
      if (resolved.enum) return refName;
    }
    return undefined;
  }

  const schemaObj = schema as OpenAPIV3.SchemaObject;
  if (schemaObj.oneOf) {
    for (const item of schemaObj.oneOf) {
      const name = findEnumName(
        item as OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject,
        components
      );
      if (name) return name;
    }
  }

  return undefined;
}

function parseRequestBody(body: OpenAPIV3.RequestBodyObject | undefined): { schema: string; isArray: boolean; required: boolean } | undefined {
  if (!body?.content?.['application/json']?.schema) return undefined;

  const required = body.required ?? false;
  const schema = body.content['application/json'].schema;
  if ('$ref' in schema) {
    const refName = schema.$ref.split('/').pop();
    return refName ? { schema: `${refName}Schema`, isArray: false, required } : undefined;
  }

  // Handle array request bodies (e.g., Treatment[])
  const schemaObj = schema as OpenAPIV3.SchemaObject;
  if (schemaObj.type === 'array' && schemaObj.items) {
    const items = schemaObj.items as OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject;
    if ('$ref' in items) {
      const itemName = items.$ref.split('/').pop();
      return itemName ? { schema: `${itemName}Schema`, isArray: true, required } : undefined;
    }
  }

  return undefined;
}

function parseResponse(response: OpenAPIV3.ResponseObject | undefined): string | undefined {
  if (!response?.content?.['application/json']?.schema) return undefined;

  const schema = response.content['application/json'].schema;
  if ('$ref' in schema) {
    return schema.$ref.split('/').pop();
  }

  // Handle array responses (e.g., TrackerDefinitionDto[])
  const schemaObj = schema as OpenAPIV3.SchemaObject;
  if (schemaObj.type === 'array' && schemaObj.items) {
    const items = schemaObj.items as OpenAPIV3.SchemaObject | OpenAPIV3.ReferenceObject;
    if ('$ref' in items) {
      const itemName = items.$ref.split('/').pop();
      return itemName ? `${itemName}[]` : undefined;
    }
  }

  return undefined;
}

function getSchemaType(schema: OpenAPIV3.SchemaObject | undefined): string {
  if (!schema) return 'unknown';

  if (schema.type === 'string') {
    if (schema.format === 'uuid') return 'string';
    if (schema.format === 'date-time') return 'Date';
    return 'string';
  }
  if (schema.type === 'integer' || schema.type === 'number') return 'number';
  if (schema.type === 'boolean') return 'boolean';
  if (schema.type === 'array') return 'array';

  return 'unknown';
}
