<script lang="ts">
  import { scopeCategories } from "#lib/constants/scope-labels.svelte.js";

  interface SummaryEntry {
    name: string;
    level: string;
    scope: string;
  }

  let { permissions = [] }: { permissions: string[] } = $props();

  const entries = $derived.by(() => {
    const set = new Set(permissions);

    const categories = scopeCategories();
    const fullAccess = categories.find((cat) => cat.full === "*");
    if (fullAccess && set.has("*")) {
      return [{ name: fullAccess.label, level: "Full access", scope: "*" }];
    }

    const held: SummaryEntry[] = [];
    const covered: string[] = [];
    for (const cat of categories.filter((c) => c.group !== "oauth")) {
      if (cat.full && set.has(cat.full)) {
        held.push({ name: cat.label, level: "Full access", scope: cat.full });
        covered.push(...(cat.alsoGrants ?? []));
      } else if (cat.readwrite && set.has(cat.readwrite)) {
        held.push({ name: cat.label, level: "Read & write", scope: cat.readwrite });
      } else if (cat.read && set.has(cat.read)) {
        held.push({ name: cat.label, level: "Read", scope: cat.read });
      }
    }

    return held.filter((entry) => !covered.includes(entry.scope));
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
