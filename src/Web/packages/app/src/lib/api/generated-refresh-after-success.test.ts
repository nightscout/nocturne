import { describe, it, expect, vi } from "vitest";
import { transformWithEsbuild } from "vite";
import { error } from "@sveltejs/kit";
import {
  generateRemoteFunctions,
  resolveConfig,
  type OperationInfo,
  type ParsedSpec,
  type UserConfig,
} from "openapi-remote-codegen";
import config from "../../../../../remote-codegen.config";
import { parseErrorBody, parseIssues } from "./error-body";

/**
 * A mutation's on-invalidate refreshes must run after the write's catch, not
 * inside its `try`: there, a refresh that threw would be relabelled by the
 * status arms as the write failing (#1068). These drive the patched generator
 * with this workspace's config and pin where the refresh lands.
 */

const resolved = resolveConfig(config as UserConfig);

function operation(overrides: Partial<OperationInfo>): OperationInfo {
  return {
    operationId: "Foods_GetFavorites",
    tag: "V4 Foods",
    method: "get",
    path: "/api/v4/foods/favorites",
    remoteType: "query",
    invalidates: [],
    parameters: [],
    isVoidResponse: false,
    clientPropertyName: "foodsV4",
    ...overrides,
  };
}

function generate(mutation: Partial<OperationInfo>): string {
  const parsed: ParsedSpec = {
    operations: [
      operation({}),
      operation({
        operationId: "Foods_AddFavorite",
        method: "post",
        remoteType: "command",
        requestBodySchema: "AddFavoriteRequestSchema",
        invalidates: ["GetFavorites"],
        ...mutation,
      }),
    ],
    tags: ["V4 Foods"],
  };
  const content = generateRemoteFunctions(parsed, resolved).get("foods.generated.remote.ts");
  if (!content) throw new Error("foods.generated.remote.ts was not generated");
  return content;
}

/** The source of one generated remote, from its `export const` to its closing `});`. */
function remoteSource(content: string, name: string): string {
  const start = content.indexOf(`export const ${name} =`);
  expect(start, `${name} is emitted`).toBeGreaterThanOrEqual(0);
  const end = content.indexOf("\n});", start);
  return content.slice(start, end + "\n});".length);
}

function expectRefreshAfterCatch(source: string) {
  const catchAt = source.indexOf("} catch (err) {");
  const refreshAt = source.indexOf("await refreshInvalidated('addFavorite', [");
  expect(catchAt).toBeGreaterThan(0);
  expect(refreshAt).toBeGreaterThan(catchAt);
  expect(source.slice(0, catchAt)).not.toContain("refreshInvalidated");
}

describe("generated mutation refreshes", () => {
  it.each([
    ["command returning a body", { remoteType: "command" as const }],
    ["command with no content", { remoteType: "command" as const, isVoidResponse: true }],
    ["form", { remoteType: "form" as const }],
    ["file upload", { isFileUpload: true, fileFieldName: "file" }],
    [
      "url-encoded command",
      {
        isUrlEncoded: true,
        urlEncodedProperties: [{ name: "code", type: "string", required: true }],
      },
    ],
  ])("emit the refresh after the catch for a %s", (_, mutation) => {
    expectRefreshAfterCatch(remoteSource(generate(mutation), "addFavorite"));
  });

  it("returns the write's result after the refresh", () => {
    const source = remoteSource(generate({}), "addFavorite");
    expect(source).toContain("let result;");
    expect(source).toContain("result = await apiClient.foodsV4.addFavorite(");
    expect(source).toMatch(/\]\);\n {2}return result;\n\}\);$/);
  });

  it("keeps the return inside the try when nothing is refreshed", () => {
    const source = remoteSource(generate({ invalidates: [] }), "addFavorite");
    expect(source).not.toContain("refreshInvalidated");
    expect(source).not.toContain("let result;");
    expect(source).toContain("const result = await apiClient.foodsV4.addFavorite(");
  });

  it("never lets a throwing refresh reach the write's status arms", async () => {
    const compiled = await transformWithEsbuild(
      remoteSource(generate({}), "addFavorite").replace("export const addFavorite =", "return"),
      "addFavorite.ts",
      { loader: "ts" }
    );
    const addFavorite = new Function(
      "command",
      "getRequestEvent",
      "AddFavoriteRequestSchema",
      "refreshInvalidated",
      "getFavorites",
      "error",
      "parseErrorBody",
      "parseIssues",
      compiled.code
    );

    const write = vi.fn(async () => ({ id: "created" }));
    const refused = (() => {
      try {
        error(403, "refresh refused");
      } catch (thrown) {
        return thrown;
      }
    })();
    const console = vi.spyOn(globalThis.console, "error").mockImplementation(() => {});

    const remote = addFavorite(
      (_schema: unknown, fn: (request: unknown) => Promise<unknown>) => fn,
      () => ({ locals: { apiClient: { foodsV4: { addFavorite: write } } } }),
      { optional: () => ({}) },
      async () => {
        throw refused;
      },
      () => ({ refresh: async () => {} }),
      error,
      parseErrorBody,
      parseIssues
    ) as (request: unknown) => Promise<unknown>;

    const outcome = await remote({ foodId: "1" }).catch((e: unknown) => e);
    console.mockRestore();

    expect(write).toHaveBeenCalledTimes(1);
    // Inside the try, the 403 arm would have rethrown a fresh HttpError carrying
    // the refresh's reason as if the write had been refused.
    expect(outcome).toBe(refused);
    expect(console).not.toHaveBeenCalled();
  });
});
