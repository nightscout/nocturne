<!--
  The foods a meal group matches, picked with the builder's food search. The picker gives ids; the
  group also keeps each food's name, which the caption and the chip show.
-->
<script lang="ts">
  import { getFood } from "#lib/api/generated/foods.generated.remote.js";
  import FoodPicker from "../builder/FoodPicker.svelte";
  import type { FoodRef } from "./workspace.svelte.js";

  interface Props {
    id: string;
    value: FoodRef[];
    onchange: (foods: FoodRef[]) => void;
  }

  let { id, value, onchange }: Props = $props();

  async function pick(ids: string[]) {
    const foods = await Promise.all(
      ids.map(
        async (foodId) =>
          value.find((f) => f.id === foodId) ?? {
            id: foodId,
            name: (await getFood(foodId)).name ?? "A food",
          }
      )
    );
    onchange(foods);
  }
</script>

<div class="w-80">
  <FoodPicker
    {id}
    value={value.map((f) => f.id)}
    onchange={(ids) => void pick(ids)}
    max={20}
  />
</div>
