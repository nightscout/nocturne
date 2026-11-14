<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import Plus from "@lucide/svelte/icons/plus";
  import { canLogEvents } from "./can-log-events";

  interface Props {
    /**
     * `inline` sits in a row of controls and shows from viewport md; `floating` is pinned to the
     * bottom-right below md. Both switch on the viewport so exactly one shows at any width.
     */
    placement?: "inline" | "floating";
  }

  let { placement = "inline" }: Props = $props();

  const canLog = $derived(canLogEvents());

  const entryDialog = lazyComponent(() => import("./EntryEditDialog.svelte"));
</script>

{#if canLog}
  {#if placement === "floating"}
    <Button
      size="fab"
      class="fixed right-4 bottom-[calc(1rem+env(safe-area-inset-bottom))] z-40 md:hidden print:hidden"
      aria-label="Log event"
      title="Log event"
      data-testid="log-event-floating"
      onclick={() => (entryDialog.open = true)}
    >
      <Plus aria-hidden="true" />
    </Button>
  {:else}
    <Button
      size="sm"
      class="max-md:hidden print:hidden"
      data-testid="log-event"
      onclick={() => (entryDialog.open = true)}
    >
      <Plus aria-hidden="true" />
      Log event
    </Button>
  {/if}

  {#if entryDialog.component}
    <entryDialog.component
      bind:open={entryDialog.open}
      onClose={() => (entryDialog.open = false)}
    />
  {/if}
{/if}
