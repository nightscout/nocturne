<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import { Separator } from "$lib/components/ui/separator";

  interface AccessLevel {
    value: string;
    label: string;
    atoms: string[];
  }

  interface PermissionCategory {
    name: string;
    isSubItem?: boolean;
    /** Domain of a parent super-permission (e.g. "health" for heartrate/stepcount). */
    coveredBy?: string;
    levels: AccessLevel[];
  }

  interface PermissionGroup {
    header?: string;
    categories: PermissionCategory[];
  }

  let { selected = $bindable<string[]>([]) }: { selected: string[] } = $props();

  function rw(domain: string): AccessLevel[] {
    return [
      { value: "none", label: "No access", atoms: [] },
      { value: "read", label: "Read", atoms: [`${domain}.read`] },
      {
        value: "readwrite",
        label: "Read & Write",
        atoms: [`${domain}.read`, `${domain}.readwrite`],
      },
    ];
  }

  function readOnly(domain: string): AccessLevel[] {
    return [
      { value: "none", label: "No access", atoms: [] },
      { value: "read", label: "Read", atoms: [`${domain}.read`] },
    ];
  }

  function toggle(atom: string): AccessLevel[] {
    return [
      { value: "none", label: "No access", atoms: [] },
      { value: "full", label: "Full access", atoms: [atom] },
    ];
  }

  const groups: PermissionGroup[] = [
    {
      header: "Health",
      categories: [
        { name: "Health", levels: rw("health") },
        { name: "Blood Glucose", isSubItem: true, coveredBy: "health", levels: rw("entries") },
        { name: "Treatments", isSubItem: true, coveredBy: "health", levels: rw("treatments") },
        { name: "Devices", isSubItem: true, coveredBy: "health", levels: rw("devicestatus") },
        { name: "Profile", isSubItem: true, coveredBy: "health", levels: rw("profile") },
        { name: "Food", isSubItem: true, coveredBy: "health", levels: rw("food") },
      ],
    },
    {
      header: "Biometrics",
      categories: [
        { name: "Heart Rate", isSubItem: true, coveredBy: "health", levels: rw("heartrate") },
        { name: "Step Count", isSubItem: true, coveredBy: "health", levels: rw("stepcount") },
      ],
    },
    {
      header: "Platform",
      categories: [
        { name: "Notifications", levels: rw("notifications") },
        { name: "Reports", levels: readOnly("reports") },
      ],
    },
    {
      header: "Account",
      categories: [
        { name: "Identity", levels: readOnly("identity") },
        { name: "Sharing", levels: toggle("sharing.readwrite") },
      ],
    },
  ];

  const fullAccessCategory: PermissionCategory = {
    name: "Full access",
    levels: [
      { value: "none", label: "No access", atoms: [] },
      { value: "full", label: "Full access", atoms: ["*"] },
    ],
  };

  /** All unique atoms across every level of a category. */
  function allAtoms(cat: PermissionCategory): string[] {
    const set = new Set<string>();
    for (const level of cat.levels) {
      for (const atom of level.atoms) {
        set.add(atom);
      }
    }
    return [...set];
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
   * Only disables children when the parent is at its maximum level (Read & Write).
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
  {#each groups as group (group.header ?? '')}
    {#if group.header}
      <p class="text-sm font-medium pt-3 pb-1">{group.header}</p>
    {/if}
    {#each group.categories as cat (cat.name)}
      {@const level = getLevel(cat)}
      {@const covered = isCoveredByParent(cat)}
      <div
        class="flex items-center justify-between gap-4 py-1.5 {cat.isSubItem ? 'pl-3' : ''}"
        class:opacity-60={covered}
      >
        <div class="flex items-center gap-2 min-w-0 flex-1">
          <span class="text-sm">{cat.name}</span>
          {#if covered}
            <span class="text-xs text-muted-foreground">Covered by {cat.coveredBy!.charAt(0).toUpperCase() + cat.coveredBy!.slice(1)}</span>
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
          <Select.Trigger class="w-[160px] h-8 text-sm">
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

  {#each [fullAccessCategory] as cat (cat.name)}
    {@const level = getLevel(cat)}
    <div class="flex items-center justify-between gap-4 py-1.5">
      <span class="text-sm font-medium">{cat.name}</span>
      <Select.Root
        type="single"
        value={level}
        onValueChange={(v) => {
          if (v) setLevel(cat, v);
        }}
      >
        <Select.Trigger class="w-[160px] h-8 text-sm">
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
