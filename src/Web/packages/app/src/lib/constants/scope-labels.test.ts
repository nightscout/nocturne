import { describe, expect, it } from "vitest";
import { OAuthScope, PermissionAtom } from "#lib/api/generated/nocturne-api-client.js";
import { scopeCategories, scopeLabels } from "./scope-labels.svelte";

describe("scope labels", () => {
  it("labels every scope the generated client knows", () => {
    const labels = scopeLabels();
    const known = [...Object.values(OAuthScope), ...Object.values(PermissionAtom)];
    const unlabelled = known.filter((scope) => !labels[scope]?.trim());

    expect(unlabelled).toEqual([]);
  });

  it("labels only scopes the generated client knows", () => {
    const known = new Set<string>([
      ...Object.values(OAuthScope),
      ...Object.values(PermissionAtom),
    ]);

    expect(Object.keys(scopeLabels()).filter((scope) => !known.has(scope))).toEqual([]);
  });

  it("groups only scopes that have a label", () => {
    const labels = scopeLabels();
    const grouped = scopeCategories().flatMap((cat) =>
      [cat.read, cat.readwrite, cat.full, ...(cat.alsoGrants ?? [])].filter(Boolean),
    );

    expect(grouped.length).toBeGreaterThan(0);
    expect(grouped.filter((scope) => !labels[scope as keyof typeof labels])).toEqual([]);
  });
});
