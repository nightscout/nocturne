<script lang="ts">
  import type { Component } from "svelte";
  import { setFoodState } from "#routes/(authenticated)/food/food-context.js";
  import type { FoodState } from "#routes/(authenticated)/food/food-state.svelte.js";

  interface Props {
    Form: Component<never>;
    formProps: Record<string, unknown>;
    width?: number;
  }

  const { Form, formProps, width }: Props = $props();
  const Rendered = $derived(Form as Component<Record<string, unknown>>);

  setFoodState({
    foods: [],
    categories: ["Dairy"],
    categoryMap: { Dairy: {} },
  } as unknown as FoodState);
</script>

<div
  class="w-(--stub-width)"
  style:--stub-width={width === undefined ? "auto" : `${width}px`}
>
  <Rendered {...formProps} />
</div>
