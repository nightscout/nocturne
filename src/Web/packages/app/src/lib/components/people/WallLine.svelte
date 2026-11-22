<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import AttentionReasons from "./AttentionReasons.svelte";
  import PersonTrend from "./PersonTrend.svelte";
  import PersonValue from "./PersonValue.svelte";
  import ReadingAge from "./ReadingAge.svelte";
  import { isFlagged, personName } from "./person.js";
  import { getReadState, isShownAsCurrent } from "./read-state.js";
  import type { WallStep } from "./wall-layout.js";

  interface Props {
    person: TenantOverviewItem;
    step: WallStep;
  }

  const { person, step }: Props = $props();

  const read = getReadState();
  const name = $derived(personName(person));
  const current = $derived(isShownAsCurrent(person, read));
</script>

<li class="flex min-w-0 items-center gap-2 px-1 {step.rowClass}" data-testid="wall-line">
  <div class="flex min-w-0 flex-1 items-baseline gap-2">
    <p class="min-w-0 shrink truncate {step.nameClass}" title={name}>{name}</p>
    {#if isFlagged(person)}
      <div class="min-w-0 shrink-[2]">
        <AttentionReasons {person} compact size={step.cardSize} />
      </div>
    {/if}
  </div>
  <ReadingAge {person} />
  {#if current}
    <PersonTrend {person} size={step.arrowPx} />
  {/if}
  <PersonValue {person} size={step.valueSize} />
</li>
