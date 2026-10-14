<script lang="ts">
  import type { Food } from "$api";
  import Composer from "./Composer.svelte";
  import { setFoodState } from "./food-context";
  import type { FoodState } from "./food-state.svelte";

  let {
    onadd,
    onclose = () => {},
  }: { onadd: (food: Food) => Promise<Food | null>; onclose?: () => void } = $props();

  setFoodState({ foods: [], categories: [] } as unknown as FoodState);
</script>

<Composer {onadd} {onclose} />
