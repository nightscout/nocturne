#!/usr/bin/env node --experimental-strip-types

import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const OPENAPI_PATH = path.join(
  __dirname,
  "../../packages/app/src/lib/api/generated/openapi.json",
);
const OUTPUT_PATH = path.join(
  __dirname,
  "../../packages/app/src/lib/api/generated/schemas.ts",
);
const CLIENT_PATH = path.join(
  __dirname,
  "../../packages/app/src/lib/api/generated/nocturne-api-client.ts",
);

/**
 * The subset of an OpenAPI 3.0 schema object NSwag emits for this API. A keyword
 * outside it fails generation rather than being dropped, so a spec change that
 * needs new handling is noticed.
 */
interface Schema {
  type?: string;
  format?: string;
  nullable?: boolean;
  description?: string;
  properties?: Record<string, Schema>;
  required?: string[];
  additionalProperties?: boolean | Schema;
  items?: Schema;
  enum?: (string | number)[];
  $ref?: string;
  oneOf?: Schema[];
  allOf?: Schema[];
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  minimum?: number;
  maximum?: number;
}

interface OpenApiDocument {
  components?: { schemas?: Record<string, Schema> };
}

type Additional = Schema["additionalProperties"];

const HANDLED_KEYWORDS = new Set([
  "type", "format", "nullable", "description", "properties", "required",
  "additionalProperties", "items", "enum", "$ref", "oneOf", "allOf",
  "minLength", "maxLength", "pattern", "minimum", "maximum",
]);

// NSwag emits JsonDocument/JsonElement as concrete objects (isDisposable,
// rootElement), but on the wire they carry arbitrary JSON.
const OPAQUE_JSON_TYPES = new Set(["JsonDocument", "JsonElement"]);

/**
 * Helpers emitted at the top of the output.
 *
 * A `date-time` field is typed `Date` on the NSwag interfaces, so the schema
 * yields a real `Date`. It accepts one as well as the ISO string a form or a
 * JSON round trip produces, and `JSON.stringify` sends it back as that string.
 * A `date` (DateOnly) field stays a string: a serialised `Date` carries a time
 * component the server's DateOnly converter rejects.
 *
 * A nullable field accepts `null` and yields `undefined`, which is how the NSwag
 * interfaces (`nullValue: Undefined`) type it. The field is then left out of the
 * request body, which the server binds as null.
 */
const HEADER = `const dateTime = z.union([
  z.date(),
  z.iso.datetime().transform((value) => new Date(value)),
]);

const nullish = <T extends z.ZodType>(schema: T) =>
  schema.nullish().transform((value) => value ?? undefined);
`;

function refName(ref: string): string {
  const match = ref.match(/^#\/components\/schemas\/(\w+)$/);
  if (!match) throw new Error(`Unsupported $ref: ${ref}`);
  return match[1];
}

/** The named schema a property points at, when it is only a reference. */
function soleRef(schema: Schema): string | undefined {
  if (schema.$ref) return refName(schema.$ref);
  const wrapped = schema.oneOf ?? schema.allOf;
  if (wrapped?.length === 1 && wrapped[0].$ref && !schema.type && !schema.properties) {
    return refName(wrapped[0].$ref);
  }
  return undefined;
}

function children(schema: Schema): Schema[] {
  return [
    ...Object.values(schema.properties ?? {}),
    ...(schema.oneOf ?? []),
    ...(schema.allOf ?? []),
    ...(schema.items ? [schema.items] : []),
    ...(typeof schema.additionalProperties === "object" ? [schema.additionalProperties] : []),
  ];
}

function assertHandled(schema: Schema, where: string): void {
  for (const key of Object.keys(schema)) {
    if (key.startsWith("x-")) continue;
    if (!HANDLED_KEYWORDS.has(key)) {
      throw new Error(`${where}: unsupported keyword '${key}'`);
    }
  }
}

class Generator {
  private readonly schemas: Record<string, Schema>;
  private readonly clientEnums: Set<string>;
  /** Schemas whose wire shape differs from their NSwag interface: they hold a DateOnly field. */
  private readonly divergent = new Set<string>();
  /** Members of reference cycles, declared through `z.lazy`. */
  private readonly lazy = new Set<string>();

  constructor(schemas: Record<string, Schema>, clientEnums: Set<string>) {
    this.schemas = schemas;
    this.clientEnums = clientEnums;
  }

  private dependencies(schema: Schema, out = new Set<string>()): Set<string> {
    if (schema.$ref) out.add(refName(schema.$ref));
    for (const child of children(schema)) this.dependencies(child, out);
    return out;
  }

  private hasDateOnly(schema: Schema): boolean {
    if (schema.type === "string" && schema.format === "date") return true;
    return children(schema).some((child) => this.hasDateOnly(child));
  }

  /**
   * Orders the schemas so each is declared after the ones it references, and
   * marks cycle members, which have to be declared lazily.
   */
  order(): string[] {
    const names = Object.keys(this.schemas);
    const deps = new Map(names.map((n) => [n, [...this.dependencies(this.schemas[n])]]));

    // Tarjan's algorithm yields components dependencies-first.
    let counter = 0;
    const index = new Map<string, number>();
    const low = new Map<string, number>();
    const stack: string[] = [];
    const onStack = new Set<string>();
    const ordered: string[] = [];

    const visit = (name: string): void => {
      index.set(name, counter);
      low.set(name, counter);
      counter++;
      stack.push(name);
      onStack.add(name);
      for (const dep of deps.get(name) ?? []) {
        if (!this.schemas[dep]) throw new Error(`${name}: reference to missing schema ${dep}`);
        if (!index.has(dep)) {
          visit(dep);
          low.set(name, Math.min(low.get(name) ?? 0, low.get(dep) ?? 0));
        } else if (onStack.has(dep)) {
          low.set(name, Math.min(low.get(name) ?? 0, index.get(dep) ?? 0));
        }
      }
      if (low.get(name) !== index.get(name)) return;
      const component: string[] = [];
      let member: string | undefined;
      do {
        member = stack.pop();
        if (member === undefined) break;
        onStack.delete(member);
        component.push(member);
      } while (member !== name);
      const cyclic = component.length > 1 || (deps.get(name) ?? []).includes(name);
      for (const m of component.reverse()) {
        if (cyclic) this.lazy.add(m);
        ordered.push(m);
      }
    };
    for (const name of names) if (!index.has(name)) visit(name);

    for (const name of ordered) {
      if (
        this.hasDateOnly(this.schemas[name]) ||
        (deps.get(name) ?? []).some((d) => this.divergent.has(d))
      ) {
        this.divergent.add(name);
      }
    }
    for (const name of this.lazy) {
      if (this.divergent.has(name)) {
        throw new Error(`${name}: a cyclic schema with a DateOnly field has no type to be declared with`);
      }
    }
    return ordered;
  }

  private stringExpr(schema: Schema): string {
    if (schema.format === "date-time") return "dateTime";
    const formats: Record<string, string> = {
      date: "z.iso.date()",
      time: "z.iso.time()",
      duration: "z.iso.duration()",
      guid: "z.uuid()",
      uuid: "z.uuid()",
    };
    let expr = (schema.format && formats[schema.format]) || "z.string()";
    if (typeof schema.minLength === "number") expr += `.min(${schema.minLength})`;
    if (typeof schema.maxLength === "number") expr += `.max(${schema.maxLength})`;
    if (schema.pattern) expr += `.regex(new RegExp(${JSON.stringify(schema.pattern)}))`;
    return expr;
  }

  /** The Zod expression for a schema, before its nullability is applied. */
  private baseExpr(schema: Schema, where: string): string {
    assertHandled(schema, where);
    const ref = soleRef(schema);
    if (ref) return `${ref}Schema`;
    if (schema.oneOf || schema.allOf) {
      throw new Error(`${where}: only a single-reference oneOf/allOf is supported below the top level`);
    }
    if (schema.enum) {
      if (schema.enum.every((v) => typeof v === "string")) {
        return `z.enum(${JSON.stringify(schema.enum)})`;
      }
      return `z.union([${schema.enum.map((v) => `z.literal(${JSON.stringify(v)})`).join(", ")}])`;
    }
    switch (schema.type) {
      case undefined:
        return "z.unknown()";
      case "string":
        return this.stringExpr(schema);
      case "boolean":
        return "z.boolean()";
      case "integer":
      case "number": {
        let expr = schema.type === "integer" ? "z.number().int()" : "z.number()";
        if (typeof schema.minimum === "number") expr += `.min(${schema.minimum})`;
        if (typeof schema.maximum === "number") expr += `.max(${schema.maximum})`;
        return expr;
      }
      case "array":
        return `z.array(${schema.items ? this.expr(schema.items, `${where}[]`) : "z.unknown()"})`;
      case "object":
        return this.objectExpr(schema.properties ?? {}, schema.required ?? [], schema.additionalProperties, where);
      default:
        throw new Error(`${where}: unsupported type '${schema.type}'`);
    }
  }

  private expr(schema: Schema, where: string): string {
    const base = this.baseExpr(schema, where);
    return schema.nullable ? `nullish(${base})` : base;
  }

  private objectExpr(
    properties: Record<string, Schema>,
    required: string[],
    additional: Additional,
    where: string,
  ): string {
    const requiredSet = new Set(required);
    const shape = Object.entries(properties).map(([key, prop]) => {
      const value = this.expr(prop, `${where}.${key}`);
      return `${JSON.stringify(key)}: ${requiredSet.has(key) ? value : `${value}.optional()`}`;
    });
    const body = `{ ${shape.join(", ")} }`;
    if (additional === false) return `z.strictObject(${body})`;
    if (additional === undefined || additional === true) return `z.looseObject(${body})`;
    const extra = Object.keys(additional).length === 0 ? "z.unknown()" : this.expr(additional, `${where}{}`);
    return `z.object(${body}).catchall(${extra})`;
  }

  /**
   * Flattens an allOf of a base reference and an extension into one object, the
   * way NSwag emits it as `interface X extends Base`. An intersection of two
   * strict objects would reject each side's keys.
   */
  private flatten(schema: Schema, where: string): { properties: Record<string, Schema>; required: string[]; additional: Additional } {
    if (!schema.allOf) {
      return {
        properties: schema.properties ?? {},
        required: schema.required ?? [],
        additional: schema.additionalProperties,
      };
    }
    const properties: Record<string, Schema> = {};
    const required: string[] = [];
    let additional: Additional;
    for (const part of schema.allOf) {
      const resolved = part.$ref ? this.schemas[refName(part.$ref)] : part;
      assertHandled(resolved, where);
      const flat = this.flatten(resolved, where);
      Object.assign(properties, flat.properties);
      required.push(...flat.required);
      additional = flat.additional;
    }
    return { properties, required, additional };
  }

  declaration(name: string): string {
    const schema = this.schemas[name];

    if (OPAQUE_JSON_TYPES.has(name)) {
      return [
        `export const ${name}Schema = z.record(z.string(), z.unknown());`,
        `export type ${name} = z.output<typeof ${name}Schema>;`,
      ].join("\n");
    }

    assertHandled(schema, name);
    let expr: string;
    if (schema.enum) {
      if (!this.clientEnums.has(name)) throw new Error(`${name}: no matching enum in the NSwag client`);
      expr = `z.enum(Api.${name})`;
    } else if (schema.allOf) {
      const flat = this.flatten(schema, name);
      expr = this.objectExpr(flat.properties, flat.required, flat.additional, name);
    } else {
      expr = this.baseExpr(schema, name);
    }

    if (this.lazy.has(name)) {
      return [
        `export const ${name}Schema: z.ZodType<Api.${name}> = z.lazy(() => ${expr});`,
        `export type ${name} = Api.${name};`,
      ].join("\n");
    }
    if (this.divergent.has(name)) {
      return [
        `// A DateOnly field is a string on the wire but \`Date\` on the NSwag interface.`,
        `export const ${name}Schema = ${expr};`,
        `export type ${name} = z.output<typeof ${name}Schema>;`,
        `export type ${name}Input = z.input<typeof ${name}Schema>;`,
      ].join("\n");
    }
    return [
      `export const ${name}Schema = ${expr} satisfies z.ZodType<Api.${name}>;`,
      `export type ${name} = Api.${name};`,
      `export type ${name}Input = z.input<typeof ${name}Schema>;`,
    ].join("\n");
  }
}

function generateZodSchemas(): void {
  console.log("Generating Zod schemas from OpenAPI spec...");

  for (const input of [OPENAPI_PATH, CLIENT_PATH]) {
    if (!fs.existsSync(input)) {
      console.error(`Not found: ${input}`);
      console.error("Run 'pnpm run generate-api-client' first to generate the OpenAPI spec and client.");
      process.exit(1);
    }
  }

  const openApi: OpenApiDocument = JSON.parse(fs.readFileSync(OPENAPI_PATH, "utf8"));
  const schemas = openApi.components?.schemas;
  if (!schemas) {
    console.error("No schemas found in OpenAPI spec");
    process.exit(1);
  }

  const clientSource = fs.readFileSync(CLIENT_PATH, "utf8");
  const clientEnums = new Set([...clientSource.matchAll(/^export enum (\w+)/gm)].map((m) => m[1]));

  const generator = new Generator(schemas, clientEnums);
  const ordered = generator.order();

  const lines: string[] = [
    "// Auto-generated from OpenAPI spec - DO NOT EDIT",
    "// Generated by: pnpm run generate-zod-schemas",
    "",
    "import { z } from 'zod';",
    "import * as Api from './nocturne-api-client';",
    "",
    HEADER,
  ];
  for (const name of ordered) {
    lines.push(generator.declaration(name), "");
  }

  fs.writeFileSync(OUTPUT_PATH, lines.join("\n"), "utf8");
  console.log(`Generated ${ordered.length} Zod schemas at: ${OUTPUT_PATH}`);
}

generateZodSchemas();

export default generateZodSchemas;
