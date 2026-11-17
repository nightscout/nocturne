interface StandardIssue {
  readonly message: string;
  readonly path?: readonly (PropertyKey | { readonly key: PropertyKey })[];
}

type StandardResult<Output> =
  | { readonly value: Output; readonly issues?: undefined }
  | { readonly issues: readonly StandardIssue[] };

export interface StandardSchema<Input = unknown, Output = Input> {
  readonly "~standard": {
    readonly version: 1;
    readonly vendor: string;
    readonly validate: (value: unknown) => StandardResult<Output> | Promise<StandardResult<Output>>;
    readonly types?: { readonly input: Input; readonly output: Output };
  };
}

const isKitKey = (issue: StandardIssue) =>
  (issue.path?.length ?? 0) === 0 &&
  "code" in issue &&
  issue.code === "unrecognized_keys" &&
  "keys" in issue &&
  Array.isArray(issue.keys) &&
  issue.keys.length === 1 &&
  issue.keys[0] === "id";

/**
 * SvelteKit 3.1 adds `id: key` to the input of a form posted through `.for(key)` when the form
 * posts no `id`, and the generated request schemas are strict, so a schema without an `id`
 * refuses every keyed submission. This validates such input again without that `id`.
 */
export function tolerateKitFormKey<Input, Output>(
  schema: StandardSchema<Input, Output>
): StandardSchema<Input, Output> {
  const standard = schema["~standard"];

  const retry = (value: unknown, result: StandardResult<Output>) => {
    if (!result.issues?.length || !result.issues.every(isKitKey)) return result;
    if (typeof value !== "object" || value === null) return result;
    const entries: [string, unknown][] = Object.entries(value);
    return standard.validate(Object.fromEntries(entries.filter(([key]) => key !== "id")));
  };

  return {
    "~standard": {
      ...standard,
      validate: (value) => {
        const result = standard.validate(value);
        return result instanceof Promise
          ? result.then((settled) => retry(value, settled))
          : retry(value, result);
      },
    },
  };
}

export function isStandardSchema(value: unknown): value is StandardSchema {
  return typeof value === "object" && value !== null && "~standard" in value;
}
