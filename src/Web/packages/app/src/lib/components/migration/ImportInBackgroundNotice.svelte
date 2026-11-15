<script lang="ts">
  import { resolve } from "$app/paths";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Progress } from "#lib/components/ui/progress/index.js";
  import TimeRemaining from "./TimeRemaining.svelte";
  import type { ImportWatcher } from "#lib/migration/background-import.svelte.js";

  let { watcher }: { watcher: ImportWatcher } = $props();

  const status = $derived(watcher.status);
  const open = $derived(status !== undefined && !watcher.acknowledged);
  const percent = $derived(Math.round(status?.progressPercentage ?? 0));
</script>

<Dialog.Root
  open={open}
  onOpenChange={(next) => {
    if (!next) watcher.acknowledge();
  }}
>
  <Dialog.Content
    fullscreen
    showClose={false}
    data-testid="import-in-background-notice"
  >
    <div class="flex max-w-140 flex-col items-center gap-4">
      <Dialog.Title
        class="font-brand font-hairline text-4xl leading-tight tracking-tight sm:text-5xl"
      >
        {#if status?.recentDataReady}
          Your recent data is ready.
        {:else}
          Your data is importing.
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        {#if status?.recentDataReady}
          Nocturne is still importing the older history from Nightscout. You
          can use Nocturne while the import continues. Older dates fill in as
          the history arrives.
        {:else}
          Nocturne is importing data from Nightscout, the newest data first.
          You can use Nocturne while the import continues.
        {/if}
      </Dialog.Description>
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
      <Button size="lg" onclick={() => watcher.acknowledge()}>
        Continue to Nocturne
        <ArrowRight class="h-4 w-4" />
      </Button>
      <Button variant="ghost" href={resolve("settings/migration")} onclick={() => watcher.acknowledge()}>
        See the import
      </Button>
    </div>
  </Dialog.Content>
</Dialog.Root>
