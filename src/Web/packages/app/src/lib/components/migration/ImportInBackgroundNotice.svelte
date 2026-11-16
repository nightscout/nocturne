<script lang="ts">
  import { resolve } from "$app/paths";
  import { Key } from "@nocturne/ui/keys";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Progress } from "#lib/components/ui/progress/index.js";
  import TimeRemaining from "./TimeRemaining.svelte";
  import type { ImportWatcher } from "#lib/migration/background-import.svelte.js";

  let { watcher }: { watcher: ImportWatcher } = $props();

  const status = $derived(watcher.status);
  const open = $derived(status !== undefined && !watcher.acknowledged);
  const percent = $derived(Math.round(status?.progressPercentage ?? 0));

  let continueButton = $state<HTMLElement | null>(null);
  $effect(() => {
    if (open) continueButton?.focus();
  });

  function closeOnEscape(event: KeyboardEvent) {
    if (open && event.key === Key.Escape) watcher.acknowledge();
  }
</script>

<svelte:window onkeydown={closeOnEscape} />

<!-- Not a modal dialog: a modal one takes pointer events from the rest of the page, and the
     first tap on a critical alarm that opens over this would close the notice instead. -->
{#if open}
  <div
    role="dialog"
    aria-labelledby="import-notice-title"
    aria-describedby="import-notice-text"
    data-testid="import-in-background-notice"
    class="fixed inset-0 z-50 flex flex-col items-center justify-center gap-8 overflow-y-auto bg-background p-6 text-center"
  >
    <div class="flex max-w-140 flex-col items-center gap-4">
      <h2
        id="import-notice-title"
        class="font-brand font-hairline text-4xl leading-tight tracking-tight sm:text-5xl"
      >
        {#if status?.recentDataReady}
          Your recent data is ready.
        {:else}
          Nocturne is importing your data.
        {/if}
      </h2>
      <p id="import-notice-text" class="text-base leading-relaxed text-muted-foreground">
        {#if status?.recentDataReady}
          Nocturne is still importing the older history from Nightscout. You
          can use Nocturne while the import continues. Older dates show data
          when their history arrives.
        {:else}
          Nocturne imports the newest data from Nightscout first. You can use
          Nocturne while the import continues.
        {/if}
      </p>
    </div>

    <div class="flex w-full max-w-100 flex-col gap-2">
      <Progress value={percent} aria-label="Import progress" />
      <p class="text-sm text-muted-foreground">
        {percent}% imported
        {#if status?.estimatedTimeRemaining}
          &middot; <TimeRemaining remaining={status.estimatedTimeRemaining} />
        {/if}
      </p>
    </div>

    <div class="flex flex-col items-center gap-3">
      <Button bind:ref={continueButton} size="lg" onclick={() => watcher.acknowledge()}>
        Continue to Nocturne
        <ArrowRight class="h-4 w-4" />
      </Button>
      <Button variant="ghost" href={resolve("settings/migration")} onclick={() => watcher.acknowledge()}>
        See the import
      </Button>
    </div>
  </div>
{/if}
