<script lang="ts">
  import * as Select from "$lib/components/ui/select";

  interface AccessLevel {
    value: string;
    label: string;
    atoms: string[];
  }

  interface PermissionCategory {
    name: string;
    isSubItem?: boolean;
    levels: AccessLevel[];
  }

  interface PermissionGroup {
    header?: string;
    categories: PermissionCategory[];
  }

  let {
    selected = $bindable<string[]>([]),
    readonly = false,
    grantedByRoles = [],
  }: {
    selected: string[];
    readonly?: boolean;
    grantedByRoles?: string[];
  } = $props();

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
      categories: [
        { name: "Blood Glucose", levels: rw("entries") },
        { name: "Treatments", levels: rw("treatments") },
        { name: "Devices", levels: rw("devicestatus") },
        { name: "Profile", levels: rw("profile") },
        { name: "Notifications", levels: rw("notifications") },
        { name: "Reports", levels: readOnly("reports") },
        { name: "Health", levels: readOnly("health") },
        { name: "Identity", levels: readOnly("identity") },
      ],
    },
    {
      header: "Administration",
      categories: [
        { name: "Manage Roles", isSubItem: true, levels: toggle("roles.manage") },
        { name: "Invite Members", isSubItem: true, levels: toggle("members.invite") },
        { name: "Manage Members", isSubItem: true, levels: toggle("members.manage") },
        { name: "Tenant Settings", isSubItem: true, levels: toggle("tenant.settings") },
        { name: "Manage Sharing", isSubItem: true, levels: toggle("sharing.manage") },
      ],
    },
    {
      header: "Audit",
      categories: [
        { name: "View Audit Logs", isSubItem: true, levels: toggle("audit.read") },
        {
          name: "Manage Audit Settings",
          isSubItem: true,
          levels: [
            { value: "none", label: "No access", atoms: [] },
            { value: "full", label: "Full access", atoms: ["audit.manage", "audit.read"] },
          ],
        },
      ],
    },
  ];

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
  {#each groups as group (group.header ?? '')}
    {#if group.header}
      <p class="text-sm font-medium pt-3 pb-1">{group.header}</p>
    {/if}
    {#each group.categories as cat (cat.name)}
      {@const level = getLevel(cat)}
      {@const roleGranted = isGrantedByRole(cat)}
      <div
        class="flex items-center justify-between gap-4 py-1.5 {cat.isSubItem ? 'pl-3' : ''}"
        class:opacity-60={roleGranted}
      >
        <div class="flex items-center gap-2 min-w-0 flex-1">
          <span class="text-sm">{cat.name}</span>
          {#if roleGranted}
            <span class="text-xs text-muted-foreground">Granted by role</span>
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
</div>
