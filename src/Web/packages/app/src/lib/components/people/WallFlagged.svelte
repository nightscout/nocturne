<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import AttentionReasons from "./AttentionReasons.svelte";
  import PersonGlucose from "./PersonGlucose.svelte";
  import ReadingAge from "./ReadingAge.svelte";
  import { personName } from "./person.js";
  import type { WallStep } from "./wall-layout.js";

  interface Props {
    person: TenantOverviewItem;
    step: WallStep;
  }

  const { person, step }: Props = $props();

  const name = $derived(personName(person));
</script>

<li
  class="flex min-w-0 items-center justify-between gap-3 rounded-xl border bg-card p-2 text-card-foreground"
  data-testid="wall-flagged"
>
  <div class="min-w-0 space-y-0.5">
    <div class="flex items-baseline gap-3">
      <p class="min-w-0 truncate font-semibold {step.cardNameClass}" title={name}>{name}</p>
      <ReadingAge {person} size={step.cardSize} />
    </div>
    <AttentionReasons {person} size={step.cardSize} />
  </div>
  <PersonGlucose {person} size="lg" deltaUnit />
</li>
