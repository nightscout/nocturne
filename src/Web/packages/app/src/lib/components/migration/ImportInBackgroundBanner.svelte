<script lang="ts">
  import { resolve } from "$app/paths";
  import Import from "@lucide/svelte/icons/import";
  import { Banner } from "#lib/components/ui/banner/index.js";
  import TimeRemaining from "./TimeRemaining.svelte";
  import type { ImportWatcher } from "#lib/migration/background-import.svelte.js";

  let { watcher }: { watcher: ImportWatcher } = $props();

  const status = $derived(watcher.status);
  const percent = $derived(Math.round(status?.progressPercentage ?? 0));
</script>

{#if status && watcher.acknowledged}
  <Banner variant="info">
    <div class="flex items-center gap-2" data-testid="import-in-background-banner">
      <Import class="h-4 w-4 shrink-0" />
      <span>
        Importing history from Nightscout: {percent}%
        {#if status.estimatedTimeRemaining}
          &middot; <TimeRemaining remaining={status.estimatedTimeRemaining} />
        {/if}
      </span>
    </div>
    <a class="shrink-0 font-medium underline" href={resolve("settings/migration")}>
      See the import
    </a>
  </Banner>
{/if}
