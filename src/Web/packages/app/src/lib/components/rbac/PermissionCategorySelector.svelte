<script lang="ts">
  import * as Select from "$lib/components/ui/select";

  interface AccessLevel {
    value: string;
    label: string;
    atoms: string[];
  }

  interface PermissionCategory {
    name: string;
    description?: string;
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
      header: "Patient Record",
      categories: [
        { name: "Blood Glucose", description: "CGM readings and manual blood glucose entries", levels: rw("glucose") },
        { name: "Treatments", description: "Insulin doses, carb entries, and therapy events", levels: rw("treatments") },
        { name: "Device Status", description: "Pump, CGM, and phone status reports", levels: rw("devices") },
        { name: "Heart Rate", description: "Heart rate data from wearables", levels: rw("heartrate") },
        { name: "Step Count", description: "Daily step counts from activity trackers", levels: rw("stepcount") },
        { name: "Food & Meals", description: "Food database entries and nutritional information", levels: rw("food") },
        { name: "Statistics", description: "Time-in-range, A1c estimates, and averages", levels: readOnly("statistics") },
        { name: "Reports", description: "Generated reports and data exports", levels: readOnly("reports") },
      ],
    },
    {
      header: "Therapy Settings",
      categories: [
        { name: "Treatment Profile", description: "Basal rates, sensitivity factors, carb ratios, and targets", levels: rw("therapy") },
        { name: "Alerts", description: "Alert rules, notification delivery, and history", levels: rw("alerts") },
      ],
    },
    {
      header: "Account",
      categories: [
        { name: "Identity", description: "Display name, email, and account details", levels: readOnly("identity") },
      ],
    },
    {
      header: "Administration",
      categories: [
        { name: "Manage Roles", description: "Create, edit, and delete roles", isSubItem: true, levels: toggle("roles.manage") },
        { name: "Invite Members", description: "Create and send invite links", isSubItem: true, levels: toggle("members.invite") },
        { name: "Manage Members", description: "Edit member roles and remove members", isSubItem: true, levels: toggle("members.manage") },
        { name: "Tenant Settings", description: "Site name, units, and preferences", isSubItem: true, levels: toggle("tenant.settings") },
        { name: "Manage Sharing", description: "Public sharing and follower configuration", isSubItem: true, levels: toggle("sharing.manage") },
        { name: "Guest Links", description: "Create and revoke temporary guest access links", isSubItem: true, levels: toggle("sharing.guest") },
      ],
    },
    {
      header: "Audit",
      categories: [
        { name: "View Audit Logs", description: "Read the history of data changes", isSubItem: true, levels: toggle("audit.read") },
        {
          name: "Manage Audit Settings",
          description: "Configure audit retention and export",
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
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <span class="text-sm">{cat.name}</span>
            {#if roleGranted}
              <span class="text-xs text-muted-foreground">Granted by role</span>
            {/if}
          </div>
          {#if cat.description && !roleGranted}
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
          <Select.Trigger class="w-40 h-8 text-sm">
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
