<script lang="ts">
  import type { Snippet } from "svelte";
  import { getReveal } from "./reveal.js";

  interface Props {
    children: Snippet;
    /** The size of the space the content takes, held while it is not mounted. */
    class?: string;
  }

  const { children, class: className = "" }: Props = $props();

  const reveal = getReveal();
  let near = $state(!reveal);
</script>

<div class={className} {@attach (element) => reveal?.whenNear(element, (isNear) => (near = isNear))}>
  {#if near}
    {@render children()}
  {/if}
</div>
