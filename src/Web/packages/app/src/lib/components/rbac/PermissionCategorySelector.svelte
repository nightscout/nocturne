<script lang="ts">
  import { distinct } from "#lib/utils/collections.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import {
    permissionGroups,
    type PermissionCategory,
  } from "./permission-categories.svelte";

  let {
    selected = $bindable<string[]>([]),
    readonly = false,
    grantedByRoles = [],
  }: {
    selected: string[];
    readonly?: boolean;
    grantedByRoles?: string[];
  } = $props();

  const groups = $derived(permissionGroups());

  /** All unique atoms across every level of a category. */
  function allAtoms(cat: PermissionCategory): string[] {
    return distinct(cat.levels.flatMap((level) => level.atoms));
  }

  /** Determine the current access level for a category (most permissive first). */
  function getLevel(cat: PermissionCategory): string {
    for (let i = cat.levels.length - 1; i >= 0; i--) {
      const level = cat.levels[i];
      if (level.atoms.length > 0 && level.atoms.every((a) => selected.includes(a))) {
        return level.value;
      }
    }
    return "none";
  }

  /** Whether the current level's atoms are all covered by grantedByRoles. */
  function isGrantedByRole(cat: PermissionCategory): boolean {
    const levelValue = getLevel(cat);
    const level = cat.levels.find((l) => l.value === levelValue);
    if (!level || level.atoms.length === 0) return false;
    return level.atoms.every((a) => grantedByRoles.includes(a));
  }

  /** Get the display label for the current level. */
  function getLevelLabel(cat: PermissionCategory): string {
    const levelValue = getLevel(cat);
    return cat.levels.find((l) => l.value === levelValue)?.label ?? "No access";
  }

  /** Set a new level: remove all category atoms then add the new level's atoms. */
  function setLevel(cat: PermissionCategory, newLevel: string) {
    const remove = new Set(allAtoms(cat));
    const level = cat.levels.find((l) => l.value === newLevel);
    const add = level?.atoms ?? [];
    selected = [...selected.filter((s) => !remove.has(s)), ...add];
  }
</script>

<div class="space-y-1">
  {#each groups as group (group.header)}
    <p class="text-sm font-medium pt-3 pb-1">{group.header}</p>
    {#each group.categories as cat (cat.name)}
      {@const level = getLevel(cat)}
      {@const roleGranted = isGrantedByRole(cat)}
      <div
        class="flex items-center justify-between gap-4 py-1.5 {cat.isSubItem ? 'pl-3' : ''}"
        class:opacity-60={roleGranted}
      >
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <span class="text-sm">{cat.name}</span>
            {#if roleGranted}
              <span class="text-xs text-muted-foreground">Granted by role</span>
            {/if}
          </div>
          {#if !roleGranted}
            <p class="text-xs text-muted-foreground leading-tight">{cat.description}</p>
          {/if}
        </div>
        <Select.Root
          type="single"
          value={level}
          onValueChange={(v) => {
            if (v) setLevel(cat, v);
          }}
          disabled={readonly || roleGranted}
        >
          <Select.Trigger size="sm" class="w-40">
            {getLevelLabel(cat)}
          </Select.Trigger>
          <Select.Content>
            {#each cat.levels as opt (opt.value)}
              <Select.Item value={opt.value} label={opt.label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    {/each}
  {/each}
</div>
