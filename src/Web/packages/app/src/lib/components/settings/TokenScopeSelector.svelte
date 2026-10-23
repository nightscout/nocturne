<script lang="ts">
  import { distinct } from "$lib/utils/collections";
  import * as Select from "$lib/components/ui/select";
  import { Separator } from "$lib/components/ui/separator";
  import { scopeCategories, type Scope, type ScopeCategory } from "$lib/constants/scope-labels.svelte";
  import { levelsOf, type AccessLevel } from "$lib/components/rbac/permission-categories.svelte";

  interface PermissionCategory {
    name: string;
    description?: string;
    isSubItem?: boolean;
    /** Domain of a parent super-permission (e.g. "health" for heartrate/stepcount). */
    coveredBy?: string;
    levels: AccessLevel[];
  }

  interface PermissionGroup {
    header: string;
    categories: PermissionCategory[];
  }

  let { selected = $bindable<string[]>([]) }: { selected: string[] } = $props();

  const healthReadScopes: Scope[] = [
    "glucose.read",
    "treatments.read",
    "devices.read",
    "therapy.read",
    "food.read",
    "heartrate.read",
    "stepcount.read",
    "sleep.read",
  ];

  function row(cat: ScopeCategory, extra: Partial<PermissionCategory> = {}): PermissionCategory {
    return { name: cat.label, description: cat.description, levels: levelsOf(cat), ...extra };
  }

  const groups = $derived.by((): PermissionGroup[] => {
    const categories = scopeCategories();
    const rowsFor = (scopes: Scope[], extra: Partial<PermissionCategory> = {}) =>
      scopes.flatMap((scope) =>
        categories.filter((cat) => cat.read === scope || cat.full === scope).map((cat) => row(cat, extra)),
      );
    return [
      {
        header: "Health Data",
        categories: [
          ...rowsFor(["health.read"]),
          ...rowsFor(healthReadScopes, { isSubItem: true, coveredBy: "health" }),
        ],
      },
      {
        header: "Platform",
        categories: rowsFor(["alerts.read", "alerts.acknowledge", "reports.read"]),
      },
      {
        header: "Account",
        categories: rowsFor(["identity.read", "sharing.readwrite"]),
      },
    ];
  });

  const fullAccessCategories = $derived(
    scopeCategories()
      .filter((cat) => cat.full === "*")
      .map((cat) => row(cat)),
  );

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

  /** Get the display label for the current level. */
  function getLevelLabel(cat: PermissionCategory): string {
    const levelValue = getLevel(cat);
    return cat.levels.find((l) => l.value === levelValue)?.label ?? "No access";
  }

  /**
   * Whether a category is fully covered by its parent super-permission.
   * Only disables children when the parent is at Full access.
   */
  function isCoveredByParent(cat: PermissionCategory): boolean {
    if (!cat.coveredBy) return false;
    return selected.includes(`${cat.coveredBy}.readwrite`);
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
      {@const covered = isCoveredByParent(cat)}
      <div
        class="flex items-center justify-between gap-4 py-1.5 {cat.isSubItem ? 'pl-3' : ''}"
        class:opacity-60={covered}
      >
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <span class="text-sm {!cat.isSubItem ? 'font-medium' : ''}">{cat.name}</span>
            {#if covered}
              <span class="text-xs text-muted-foreground">Covered by Health</span>
            {/if}
          </div>
          {#if cat.description && !covered}
            <p class="text-xs text-muted-foreground leading-tight">{cat.description}</p>
          {/if}
        </div>
        <Select.Root
          type="single"
          value={level}
          onValueChange={(v) => {
            if (v) setLevel(cat, v);
          }}
          disabled={covered}
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

  <Separator class="my-3" />

  {#each fullAccessCategories as cat (cat.name)}
    {@const level = getLevel(cat)}
    <div class="flex items-center justify-between gap-4 py-1.5">
      <div class="min-w-0 flex-1">
        <span class="text-sm font-medium">{cat.name}</span>
        <p class="text-xs text-muted-foreground leading-tight">{cat.description}</p>
      </div>
      <Select.Root
        type="single"
        value={level}
        onValueChange={(v) => {
          if (v) setLevel(cat, v);
        }}
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
</div>
