<!--
  The time since the last carbs before an event: plain choices first, exact minutes behind "More".
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import MealSizeEditor from "./MealSizeEditor.svelte";
  import { sinceMealText, upperFirst, type Bounds } from "./workspace.svelte.js";

  interface Props {
    id: string;
    value: Bounds;
    onchange: (bounds: Bounds) => void;
    highest: number;
  }

  let { id, value, onchange, highest }: Props = $props();

  const CHOICES: Bounds[] = [
    { min: 120 },
    { min: 180 },
    { min: 240 },
    { min: 360 },
    { max: 60 },
    { max: 120 },
  ];

  const key = (b: Bounds) => `${b.min ?? ""}:${b.max ?? ""}`;
  const chosen = $derived(CHOICES.find((c) => key(c) === key(value)));
</script>

<div class="w-80 space-y-3">
  <ToggleGroup.Root
    type="single"
    variant="outline"
    size="xs"
    spacing={1}
    class="flex-wrap"
    aria-label="Time since the last carbs"
    bind:value={
      () => (chosen ? key(chosen) : ""),
      (v: string) => {
        const found = CHOICES.find((c) => key(c) === v);
        if (found) onchange({ ...found });
      }
    }
  >
    {#each CHOICES as c (key(c))}
      <ToggleGroup.Item value={key(c)} data-lab-selected>
        {upperFirst(sinceMealText(c) ?? "")}
      </ToggleGroup.Item>
    {/each}
  </ToggleGroup.Root>
  <Collapsible.Root open={chosen === undefined}>
    <Collapsible.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button {...props} variant="subtle" size="xs" class="group">
          <ChevronRight
            class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
            aria-hidden="true"
          />
          More
        </Button>
      {/snippet}
    </Collapsible.Trigger>
    <Collapsible.Content class="pt-2">
      <p class="mb-2 text-xs text-muted-foreground">
        Minutes from the last carbs to the event
      </p>
      <MealSizeEditor
        {id}
        min={value.min}
        max={value.max}
        unit="min"
        lowest={1}
        {highest}
        {onchange}
      />
    </Collapsible.Content>
  </Collapsible.Root>
</div>
