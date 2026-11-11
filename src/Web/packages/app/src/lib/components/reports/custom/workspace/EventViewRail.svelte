<!--
  The rail of a chart of the events one by one, in place of the signals: for each event, the figure
  up, what it lies along and the line through the points; for a spread, the figure counted; for when,
  hour of day or weekday. Only the figures that apply to the groups' events are offered.
-->
<script lang="ts">
  import { alongLabel, outcomeLabel } from "./vocabulary-words.svelte.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import type { Along, Outcome, WhenAxis } from "./vocabulary.js";
  import type { Workspace } from "./workspace.svelte.js";

  interface Props {
    ws: Workspace;
  }

  let { ws }: Props = $props();

  const WHENS: { id: WhenAxis; label: string }[] = [
    { id: "hour", label: "By hour of day" },
    { id: "weekday", label: "By weekday" },
  ];

  function pickUp(v: string) {
    const found = ws.upChoices.find((o) => o.id === v);
    if (found) ws.setEvent({ up: found.id satisfies Outcome });
  }

  function pickAlong(v: string) {
    const found = ws.alongChoices.find((a) => a.id === v);
    if (found) ws.setEvent({ along: found.id satisfies Along });
  }

  function pickFigure(v: string) {
    const found = ws.figureChoices.find((o) => o.id === v);
    if (found) ws.setEvent({ figure: found.id });
  }

  function pickWhen(v: string) {
    const found = WHENS.find((w) => w.id === v);
    if (found) ws.setEvent({ when: found.id });
  }
</script>

{#snippet list(
  id: string,
  heading: string,
  choices: readonly { id: string; label: string }[],
  value: string,
  pick: (v: string) => void
)}
  <div class="space-y-1.5">
    <p class="px-2 text-xs text-muted-foreground" {id}>{heading}</p>
    <ToggleGroup.Root
      type="single"
      orientation="vertical"
      variant="choice"
      size="sm"
      spacing={1}
      class="w-full flex-col items-stretch"
      aria-labelledby={id}
      bind:value={() => value, (v: string) => v && pick(v)}
    >
      {#each choices as c (c.id)}
        <ToggleGroup.Item value={c.id} class="justify-start">
          {c.label}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  </div>
{/snippet}

<nav aria-label="What the chart shows" class="space-y-4" data-testid="event-view-rail">
  {#if ws.show === "each"}
    {@render list("rail-up", "Up", ws.upChoices.map((o) => ({ id: o.id, label: outcomeLabel(o.id) })), ws.up, pickUp)}
    {@render list("rail-along", "Along", ws.alongChoices.map((a) => ({ id: a.id, label: alongLabel(a.id) })), ws.along, pickAlong)}
    {#if ws.fitApplies}
      <div class="flex items-center gap-2 px-2">
        <Switch
          id="rail-fit"
          checked={ws.event.fit}
          onCheckedChange={(on: boolean) => ws.setEvent({ fit: on })}
        />
        <Label for="rail-fit" variant="option">Line through the points</Label>
      </div>
    {:else}
      <p class="px-2 text-xs text-muted-foreground" data-testid="no-fit">No line along the time of day</p>
    {/if}
  {:else if ws.show === "spread"}
    {@render list("rail-figure", "Figure", ws.figureChoices.map((o) => ({ id: o.id, label: outcomeLabel(o.id) })), ws.figure, pickFigure)}
  {:else if ws.show === "when"}
    {@render list("rail-when", "Count the events", WHENS, ws.event.when, pickWhen)}
  {/if}
</nav>
