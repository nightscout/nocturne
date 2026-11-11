<!--
  A row of range choices. A choice that cannot be made now is disabled, and its reason is written
  under the row, once for all the choices it applies to.
-->
<script lang="ts">
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";

  interface Option {
    id: string;
    label: string;
    reason: string | null;
  }

  interface Props {
    id: string;
    label: string;
    options: Option[];
    value: string;
    onchange: (id: string) => void;
  }

  let { id, label, options, value, onchange }: Props = $props();

  /** Each reason once, with the choices it disables. */
  const reasons = $derived.by(() => {
    const out: { reason: string; labels: string[] }[] = [];
    for (const o of options) {
      if (!o.reason) continue;
      const found = out.find((r) => r.reason === o.reason);
      if (found) found.labels.push(o.label);
      else out.push({ reason: o.reason, labels: [o.label] });
    }
    return out;
  });
</script>

<div class="space-y-1">
  <ToggleGroup.Root
    {id}
    type="single"
    variant="outline"
    size="sm"
    spacing={1}
    class="flex-wrap"
    aria-label={label}
    aria-describedby={reasons.length > 0 ? `${id}-reasons` : undefined}
    bind:value={() => value, (next: string) => next && onchange(next)}
  >
    {#each options as option (option.id)}
      <ToggleGroup.Item
        value={option.id}
        disabled={option.reason !== null && option.id !== value}
        data-lab-selected
      >
        {option.label}
      </ToggleGroup.Item>
    {/each}
  </ToggleGroup.Root>
  {#if reasons.length > 0}
    <div id="{id}-reasons" class="space-y-0.5">
      {#each reasons as r (r.reason)}
        <p class="text-xs text-muted-foreground">
          {r.labels.join(", ")}: {r.reason}
        </p>
      {/each}
    </div>
  {/if}
</div>
