import { describe, it, expect } from "vitest";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

/**
 * `BulkRestore` is the only shape in the spec whose body is an array of
 * primitives, and the generator used to drop such a body from both the wrapper
 * signature and the client call. Typechecking caught that only because the
 * client method then received too few arguments; a body bound as optional
 * satisfies the arity and posts nothing, so the emitted source is what has to
 * be asserted.
 *
 * The generated tree is gitignored and appears after an API build.
 */
const WRAPPER = fileURLToPath(
  new URL("./generated/notes.generated.remote.ts", import.meta.url)
);

const ABSENT =
  "src/lib/api/generated/notes.generated.remote.ts is not present — run `dotnet build src/API/Nocturne.API/Nocturne.API.csproj -p:GenerateNSwagClient=true` first.";

function bulkRestoreSource(): string {
  const source = readFileSync(WRAPPER, "utf8");
  const start = source.indexOf("export const bulkRestore =");
  return source.slice(start, source.indexOf("\n});", start));
}

describe("the body a generated bulk-restore command carries", () => {
  it("validates the ids as a required array", (ctx) => {
    if (!existsSync(WRAPPER)) ctx.skip(ABSENT);

    expect(bulkRestoreSource()).toContain(
      "command(z.array(z.string()), async (request) =>"
    );
  });

  it("hands the ids to the client instead of an empty object", (ctx) => {
    if (!existsSync(WRAPPER)) ctx.skip(ABSENT);

    expect(bulkRestoreSource()).toContain(
      ".bulkRestore(request as string[])"
    );
  });
});
