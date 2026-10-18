<script lang="ts">
  import type { GoogleHealthCategory } from "$lib/api";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import {
    categoryLabel,
    dataTypeLabel,
    destinationLabel,
    foundLabel,
    typeStateLabel,
  } from "./google-health-copy.svelte";

  let {
    category,
    selected = $bindable(),
    disabled,
  }: {
    category: GoogleHealthCategory;
    selected: string[];
    disabled: boolean;
  } = $props();

  const types = $derived(category.types ?? []);
  const selectedCount = $derived(
    types.filter((type) => selected.includes(type.dataType ?? "")).length
  );
  let expanded = $state<boolean | undefined>();
</script>

<details
  class="overflow-hidden rounded-lg border"
  data-testid={`google-health-category-${category.kind}`}
  open={expanded ?? category.hasSelectable}
  ontoggle={(event) => {
    expanded = event.currentTarget.open;
  }}
>
  <summary
    class="flex cursor-pointer items-center justify-between gap-3 px-4 py-3 font-medium"
  >
    <span>{category.kind ? categoryLabel(category.kind) : ""}</span>
    <span class="tabular-nums text-sm text-muted-foreground">
      {selectedCount}/{types.length} selected
    </span>
  </summary>
  <div class="overflow-auto border-t">
    <table class="w-full text-left text-sm">
      <thead>
        <tr class="border-b">
          <th class="p-3">Import</th>
          <th class="p-3">Data type</th>
          <th class="p-3">Found</th>
          <th class="p-3">Nocturne destination</th>
          <th class="p-3">Status</th>
        </tr>
      </thead>
      <tbody>
        {#each types as type (type.dataType)}
          {@const dataType = type.dataType ?? ""}
          {@const checked = selected.includes(dataType)}
          <tr class="border-b last:border-b-0">
            <td class="p-3">
              <Checkbox
                aria-label={`Import ${dataTypeLabel(dataType)}`}
                {checked}
                onCheckedChange={(value) => {
                  selected = value
                    ? [...selected, dataType]
                    : selected.filter((entry) => entry !== dataType);
                }}
                disabled={disabled || (!checked && !type.selectable)}
              />
            </td>
            <td class="p-3 font-medium">{dataTypeLabel(dataType)}</td>
            <td class="p-3">{foundLabel(type)}</td>
            <td class="p-3">{destinationLabel(type.destination)}</td>
            <td class="p-3">{typeStateLabel(type)}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
</details>
