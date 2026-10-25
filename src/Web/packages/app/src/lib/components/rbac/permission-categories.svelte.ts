import { scopeCategories, type ScopeCategory, type ScopeCategoryGroup } from "$lib/constants/scope-labels.svelte";

export interface AccessLevel {
  value: string;
  label: string;
  atoms: string[];
}

export interface PermissionCategory {
  name: string;
  description: string;
  isSubItem: boolean;
  levels: AccessLevel[];
}

export interface PermissionGroup {
  header: string;
  categories: PermissionCategory[];
}

export function levelsOf(cat: ScopeCategory): AccessLevel[] {
  const none: AccessLevel = { value: "none", label: "No access", atoms: [] };
  if (cat.full) {
    return [
      none,
      { value: "full", label: "Full access", atoms: [cat.full, ...(cat.alsoGrants ?? [])] },
    ];
  }
  const read = cat.read ? [cat.read] : [];
  const levels = [none, { value: "read", label: "Read", atoms: read }];
  if (cat.readwrite) {
    levels.push({ value: "readwrite", label: "Read & write", atoms: [...read, cat.readwrite] });
  }
  return levels;
}

function groupHeaders(): [ScopeCategoryGroup, string][] {
  return [
    ["record", "Patient record"],
    ["therapy", "Therapy settings"],
    ["account", "Account"],
    ["administration", "Administration"],
    ["audit", "Audit"],
  ];
}

export function permissionGroups(): PermissionGroup[] {
  const categories = scopeCategories();
  return groupHeaders().map(([group, header]) => ({
    header,
    categories: categories
      .filter((cat) => cat.group === group)
      .map((cat) => ({
        name: cat.label,
        description: cat.description,
        isSubItem: group === "administration" || group === "audit",
        levels: levelsOf(cat),
      })),
  }));
}
