<script lang="ts">
  interface SummaryEntry {
    name: string;
    level: string;
  }

  let { permissions = [] }: { permissions: string[] } = $props();

  const entries = $derived.by(() => {
    const set = new Set(permissions);
    const result: SummaryEntry[] = [];

    if (set.has("*")) {
      return [{ name: "All permissions", level: "Full access" }];
    }

    // Data categories (readwrite takes precedence over read)
    const dataCategories: { name: string; read: string; readwrite: string }[] = [
      { name: "Blood Glucose", read: "entries.read", readwrite: "entries.readwrite" },
      { name: "Treatments", read: "treatments.read", readwrite: "treatments.readwrite" },
      { name: "Devices", read: "devicestatus.read", readwrite: "devicestatus.readwrite" },
      { name: "Profile", read: "profile.read", readwrite: "profile.readwrite" },
      { name: "Notifications", read: "notifications.read", readwrite: "notifications.readwrite" },
    ];

    for (const cat of dataCategories) {
      if (set.has(cat.readwrite)) {
        result.push({ name: cat.name, level: "Read & Write" });
      } else if (set.has(cat.read)) {
        result.push({ name: cat.name, level: "Read" });
      }
    }

    // Read-only categories
    const readOnlyCategories: { name: string; atom: string }[] = [
      { name: "Reports", atom: "reports.read" },
      { name: "Health", atom: "health.read" },
      { name: "Identity", atom: "identity.read" },
    ];

    for (const cat of readOnlyCategories) {
      if (set.has(cat.atom)) {
        result.push({ name: cat.name, level: "Read" });
      }
    }

    // Admin permissions
    const adminPermissions: { name: string; atom: string }[] = [
      { name: "Manage Roles", atom: "roles.manage" },
      { name: "Invite Members", atom: "members.invite" },
      { name: "Manage Members", atom: "members.manage" },
      { name: "Tenant Settings", atom: "tenant.settings" },
      { name: "Manage Sharing", atom: "sharing.manage" },
    ];

    for (const perm of adminPermissions) {
      if (set.has(perm.atom)) {
        result.push({ name: perm.name, level: "Full access" });
      }
    }

    // Audit (manage takes precedence over read)
    if (set.has("audit.manage")) {
      result.push({ name: "Manage Audit Settings", level: "Full access" });
    } else if (set.has("audit.read")) {
      result.push({ name: "View Audit Logs", level: "Full access" });
    }

    return result;
  });
</script>

{#if entries.length === 0}
  <p class="text-sm text-muted-foreground">No permissions granted by roles</p>
{:else}
  <div class="space-y-0.5">
    {#each entries as entry (entry.name)}
      <div class="flex items-center justify-between text-sm py-0.5">
        <span>{entry.name}</span>
        <span class="text-muted-foreground">{entry.level}</span>
      </div>
    {/each}
  </div>
{/if}
