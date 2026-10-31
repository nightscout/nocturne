<!--
  A food category for a meal group: free text, with the categories of the food catalogue offered as
  it is typed.
-->
<script lang="ts">
  import { getFoods } from "#lib/api/generated/foods.generated.remote.js";
  import { Input } from "#lib/components/ui/input/index.js";

  interface Props {
    id: string;
    value: string | undefined;
    onchange: (value: string | undefined) => void;
    disabled?: boolean;
    invalid?: boolean;
  }

  let { id, value, onchange, disabled = false, invalid = false }: Props = $props();

  const catalogue = getFoods({});
  const categories = $derived(
    [...new Set((catalogue.current ?? []).map((f) => f.category).filter((c): c is string => !!c))].sort(),
  );
</script>

<Input
  {id}
  class="w-full max-w-sm"
  list="{id}-categories"
  value={value ?? ""}
  oninput={(event) => onchange(event.currentTarget.value.trim() === "" ? undefined : event.currentTarget.value)}
  {disabled}
  autocomplete="off"
  aria-invalid={invalid}
/>
<datalist id="{id}-categories">
  {#each categories as category (category)}
    <option value={category}></option>
  {/each}
</datalist>
{#if catalogue.error}
  <p class="text-xs text-muted-foreground" role="status">Your access does not include food data, so no categories are offered.</p>
{/if}
