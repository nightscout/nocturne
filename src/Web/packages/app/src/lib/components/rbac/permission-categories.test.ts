import { describe, expect, it } from "vitest";
import { PermissionAtom } from "#lib/api/generated/nocturne-api-client.js";
import { permissionGroups } from "./permission-categories.svelte";

const knownAtoms = new Set<string>(Object.values(PermissionAtom));

describe("permission categories", () => {
  it("offers only atoms the backend knows", () => {
    const emitted = permissionGroups().flatMap((group) =>
      group.categories.flatMap((cat) => cat.levels.flatMap((level) => level.atoms)),
    );

    expect(emitted.length).toBeGreaterThan(0);
    expect(emitted.filter((atom) => !knownAtoms.has(atom))).toEqual([]);
  });
});
