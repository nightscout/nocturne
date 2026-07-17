#!/usr/bin/env node
/**
 * Filters the full Nocturne OpenAPI spec down to V4 endpoints only.
 * Usage: node filter-v4-spec.js <input-openapi.json> <output-openapi-v4.json>
 */
import { readFileSync, writeFileSync } from 'fs';

const [,, inputPath, outputPath] = process.argv;
if (!inputPath || !outputPath) {
  console.error('Usage: node filter-v4-spec.js <input> <output>');
  process.exit(1);
}

const spec = JSON.parse(readFileSync(inputPath, 'utf8'));

// Filter paths to only /api/v4/**
const filteredPaths = {};
for (const [path, operations] of Object.entries(spec.paths)) {
  if (path.startsWith('/api/v4/')) {
    filteredPaths[path] = operations;
  }
}

// Collect all $ref references used by V4 paths
function collectRefs(obj, refs = new Set()) {
  if (obj === null || typeof obj !== 'object') return refs;
  if (obj['$ref'] && typeof obj['$ref'] === 'string') {
    const schemaName = obj['$ref'].replace('#/components/schemas/', '');
    if (obj['$ref'].startsWith('#/components/schemas/')) {
      refs.add(schemaName);
    }
  }
  for (const value of Object.values(obj)) {
    collectRefs(value, refs);
  }
  return refs;
}

// Iteratively resolve transitive schema references
const allSchemas = spec.components?.schemas || {};
const usedSchemaNames = collectRefs(filteredPaths);
let prevSize = 0;
while (usedSchemaNames.size > prevSize) {
  prevSize = usedSchemaNames.size;
  for (const name of [...usedSchemaNames]) {
    if (allSchemas[name]) {
      collectRefs(allSchemas[name], usedSchemaNames);
    }
  }
}

// Build filtered schemas
const filteredSchemas = {};
for (const name of usedSchemaNames) {
  if (allSchemas[name]) {
    filteredSchemas[name] = allSchemas[name];
  }
}

// Normalize property name the way OpenAPI Generator does:
// strip leading underscore, convert snake_case to camelCase
function normalize(name) {
  return name.replace(/^_/, '').replace(/_([a-z])/g, (_, c) => c.toUpperCase());
}

// Deduplicate allOf compositions: remove properties from composed object
// that resolve to the same generated field name as a parent property
// (e.g., parent has "id" and child has "_id" — both become "id" in codegen)
for (const schema of Object.values(filteredSchemas)) {
  if (!schema.allOf || schema.allOf.length < 2) continue;

  // Collect normalized property names from all $ref'd parent schemas
  const parentNormalized = new Set();
  for (const part of schema.allOf) {
    if (part['$ref']) {
      const parentName = part['$ref'].replace('#/components/schemas/', '');
      const parent = filteredSchemas[parentName];
      if (parent?.properties) {
        for (const prop of Object.keys(parent.properties)) {
          parentNormalized.add(normalize(prop));
        }
      }
    }
  }

  // Remove properties from inline composed objects that collide after normalization
  if (parentNormalized.size > 0) {
    for (const part of schema.allOf) {
      if (!part['$ref'] && part.properties) {
        for (const prop of Object.keys(part.properties)) {
          if (parentNormalized.has(normalize(prop))) {
            delete part.properties[prop];
          }
        }
      }
    }
  }
}

// Collapse NSwag's nullable-enum-ref query parameter idiom down to a plain
// $ref. NSwag emits optional enum query params as a doubly-nested oneOf
// ({ oneOf: [{ nullable: true, oneOf: [{ $ref }] }] }) instead of a plain
// $ref. openapi-generator's swift6 target treats that shape as a oneOf
// composition and generates a wrapper enum with no `asParameter`
// conformance, so every such parameter fails to compile. The wrapper is
// unnecessary here: the param is already optional (no `required: true`), so
// nullability adds nothing and the $ref can stand alone.
function unwrapNullableEnumParam(schema) {
  if (schema?.oneOf?.length === 1) {
    const inner = schema.oneOf[0];
    if (inner?.nullable === true && inner.oneOf?.length === 1 && inner.oneOf[0]['$ref']) {
      return { '$ref': inner.oneOf[0]['$ref'] };
    }
  }
  return schema;
}

for (const operations of Object.values(filteredPaths)) {
  for (const operation of Object.values(operations)) {
    for (const param of operation.parameters ?? []) {
      if (param.schema) {
        param.schema = unwrapNullableEnumParam(param.schema);
      }
    }
  }
}

const output = {
  openapi: spec.openapi,
  info: {
    ...spec.info,
    title: 'Nocturne SDK',
    description: 'Nocturne V4 API SDK',
  },
  paths: filteredPaths,
  components: {
    ...spec.components,
    schemas: filteredSchemas,
  },
};

// Remove security schemes not relevant to SDK consumers (keep Bearer)
if (output.components.securitySchemes) {
  const kept = {};
  for (const [name, scheme] of Object.entries(output.components.securitySchemes)) {
    if (scheme.type === 'http' && scheme.scheme === 'bearer') {
      kept[name] = scheme;
    }
  }
  output.components.securitySchemes = kept;
}

writeFileSync(outputPath, JSON.stringify(output, null, 2));
console.log(`Filtered spec: ${Object.keys(filteredPaths).length} paths, ${Object.keys(filteredSchemas).length} schemas`);
