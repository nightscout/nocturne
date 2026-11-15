<!--
  The foods a meal group matches: the picked foods by name, and a search of the food catalogue
  through the foods search to add more. Without food data access the search says so.
-->
<script lang="ts">
  import { getFood, getFoods } from "#lib/api/generated/foods.generated.remote.js";
  import { isHttpError } from "@sveltejs/kit";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";

  interface Props {
    id: string;
    value: string[];
    onchange: (ids: string[]) => void;
    max?: number;
    disabled?: boolean;
  }

  let { id, value, onchange, max = Infinity, disabled = false }: Props = $props();

  let typed = $state("");
  let find = $state("");
  let timer: ReturnType<typeof setTimeout> | undefined;

  function search(text: string) {
    typed = text;
    clearTimeout(timer);
    timer = setTimeout(() => (find = text.trim()), 300);
  }

  const results = $derived(find.length >= 2 ? getFoods({ find, count: 20 }) : null);
  const searchError = $derived(results?.error);
  const found = $derived((results?.current ?? []).filter((f) => f._id && !value.includes(f._id)));
</script>

<div class="space-y-2">
  {#if value.length > 0}
    <ul class="flex flex-wrap gap-1.5" aria-label="Foods picked">
      {#each value as foodId (foodId)}
        {@const food = getFood(foodId)}
        <li class="flex items-center gap-1 rounded-md border border-border bg-muted/40 py-0.5 pr-0.5 pl-2 text-xs">
          <span>
            {#if food.current}
              {food.current.name}
            {:else if isHttpError(food.error) && food.error.status === 404}
              A food that no longer exists
            {:else if isHttpError(food.error) && food.error.status === 403}
              A food you cannot read
            {:else if food.error}
              A food that Nocturne could not load
            {:else}
              …
            {/if}
          </span>
          <Button
            variant="ghost"
            size="icon-xs"
            onclick={() => onchange(value.filter((v) => v !== foodId))}
            aria-label="Remove {food.current?.name ?? 'food'}"
            {disabled}
          >
            <X aria-hidden="true" />
          </Button>
        </li>
      {/each}
    </ul>
  {/if}
  <Input
    {id}
    class="w-full max-w-sm"
    value={typed}
    placeholder="Find a food"
    oninput={(event) => search(event.currentTarget.value)}
    disabled={disabled || value.length >= max}
    autocomplete="off"
  />
  {#if isHttpError(searchError) && searchError.status === 403}
    <p class="text-xs text-muted-foreground" role="status">Your access does not include food data, so you cannot pick foods.</p>
  {:else if searchError}
    <p class="text-xs text-destructive" role="alert">The food search did not work. Try again.</p>
  {:else if find.length >= 2 && results?.current && found.length === 0}
    <p class="text-xs text-muted-foreground" role="status">No food matches "{find}".</p>
  {:else if found.length > 0}
    <ul class="max-w-sm divide-y divide-border rounded-md border border-border" aria-label="Foods found">
      {#each found as food (food._id)}
        <li>
          <Button
            variant="menu"
            size="menu"
            onclick={() => food._id && onchange([...value, food._id])}
            disabled={disabled || value.length >= max}
          >
            <Plus aria-hidden="true" />
            {food.name}{food.category ? ` (${food.category})` : ""}
          </Button>
        </li>
      {/each}
    </ul>
  {/if}
</div>
