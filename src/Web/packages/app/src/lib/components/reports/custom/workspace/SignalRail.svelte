<!--
  The signals on the chart, each with a remove button, and "Add a signal", which opens the catalogue of
  every signal. Removing a signal moves the focus to the next one, else to "Add a signal".
-->
<script lang="ts">
  import { tick } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import type { SignalId } from "./catalogue-vocabulary.js";
  import { chartSource } from "./catalogue-source.js";
  import type { ChartBlock, Report } from "./report.svelte.js";
  import SignalCatalogue from "./SignalCatalogue.svelte";
  import { SignalPicks } from "./signal-picks.svelte.js";
  import Swatch from "./Swatch.svelte";
  import { removeWithUndo } from "./undo.svelte.js";
  import { signalLabel } from "./vocabulary-words.svelte.js";

  interface Props {
    report: Report;
    block: ChartBlock;
  }

  let { report, block }: Props = $props();

  const ws = $derived(block.ws);
  const source = $derived(chartSource(report, block));
  const picks = new SignalPicks();
  let open = $state(false);
  let nav = $state<HTMLElement | null>(null);

  const onChart = $derived(ws.lens === "against" ? ws.againstPair : ws.shown);

  async function remove(id: SignalId) {
    const at = onChart.indexOf(id);
    const removed = removeWithUndo(
      ws,
      `Nocturne removed ${signalLabel(id)} from the chart.`,
      () => ws.toggleSignal(id),
      () => ws.placeSignal(id)
    );
    if (!removed) return;
    await tick();
    const next = nav?.querySelectorAll<HTMLElement>("[data-remove-signal]")[at] ?? null;
    (next ?? nav?.querySelector<HTMLElement>("[data-testid='add-signal']"))?.focus();
  }
</script>

<nav aria-labelledby="rail-heading-{block.key}" class="space-y-2" data-testid="signal-rail" bind:this={nav}>
  <h3 id="rail-heading-{block.key}" class="px-2 text-xs font-medium text-muted-foreground">On this chart</h3>
  {#if onChart.length === 0}
    <p class="px-2 text-sm text-muted-foreground">No signals yet.</p>
  {:else}
    <ul class="space-y-0.5">
      {#each onChart as id (id)}
        <li class="flex min-h-11 items-center gap-2 rounded-md pl-2 hover:bg-accent/50 @4xl:min-h-9" data-rail-signal={id}>
          <Swatch swatch={{ kind: "solid", color: ws.vocab.signalColor(id) }} />
          <span class="min-w-0 flex-1 py-1 text-sm leading-snug text-pretty wrap-break-word">{signalLabel(id)}</span>
          <Button
            variant="ghost"
            size="icon-touch"
            class="shrink-0"
            aria-label={`Remove ${signalLabel(id)}`}
            onclick={() => remove(id)}
            data-remove-signal={id}
          >
            <X aria-hidden="true" />
          </Button>
        </li>
      {/each}
    </ul>
  {/if}
  <SignalCatalogue {report} {source} {picks} bind:open>
    {#snippet trigger(props)}
      <Button {...props} variant="dashed" size="touch-sm" class="w-full justify-start" data-testid="add-signal">
        <Plus aria-hidden="true" />
        Add a signal
      </Button>
    {/snippet}
  </SignalCatalogue>
</nav>
