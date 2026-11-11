<!--
  One against another: which signal lies along the axis and which goes up, and the lag chart.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import ArrowLeftRight from "@lucide/svelte/icons/arrow-left-right";
  import { signal as signalOf } from "./vocabulary.js";
  import type { Workspace } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
  }

  let { ws }: Props = $props();

  const pair = $derived(ws.againstPair);
  const lagReason = $derived(ws.findLag ? null : ws.findLagReason);
</script>

<section
  aria-label="One against another"
  class="space-y-3"
  id="compare-strip"
  tabindex="-1"
>
  <div class="flex flex-wrap items-center gap-3">
    {#if pair.length === 2}
      <p class="text-sm">
        <span class="text-muted-foreground">Along the axis:</span>
        <span class="font-medium">{signalOf(pair[0]).label}</span>
      </p>
      <p class="text-sm">
        <span class="text-muted-foreground">Up:</span>
        <span class="font-medium">{signalOf(pair[1]).label}</span>
      </p>
      <Button variant="outline" size="xs" onclick={() => ws.swapAgainst()}>
        <ArrowLeftRight aria-hidden="true" />
        Swap
      </Button>
    {:else}
      <p class="text-sm text-muted-foreground">
        Select two signals: the first lies along the axis, the second goes up.
      </p>
    {/if}
  </div>
  {#if pair.length === 2}
  <div class="flex items-start gap-3">
    <Switch
      id="find-lag"
      checked={ws.findLag}
      disabled={lagReason !== null}
      onCheckedChange={(on: boolean) => ws.setFindLag(on)}
      aria-describedby="find-lag-note"
    />
    <div class="space-y-0.5">
      <Label for="find-lag" variant="option">Find the lag</Label>
      <p id="find-lag-note" class="text-xs text-muted-foreground">
        Adds a second chart: how the two correlate when one is shifted earlier
        or later.
      </p>
      {#if lagReason}
        <p class="text-xs text-muted-foreground">{lagReason}</p>
      {/if}
    </div>
  </div>
  {/if}
</section>
