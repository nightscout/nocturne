<script lang="ts">
  import { onMount, type Snippet } from "svelte";
  import { PrintMode } from "$lib/components/charts/print/print-mode.svelte";

  let {
    children,
    label,
    placeholderClass = "h-full",
  }: {
    children: Snippet;
    label: string;
    placeholderClass?: string;
  } = $props();

  let element: HTMLDivElement;
  let visible = $state(false);
  const print = new PrintMode();

  onMount(() => {
    if (typeof IntersectionObserver === "undefined") {
      visible = true;
      return;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          visible = true;
          observer.disconnect();
        }
      },
      { rootMargin: "100px" }
    );
    observer.observe(element);
    return () => observer.disconnect();
  });
</script>

<!-- svelte-ignore a11y_no_noninteractive_tabindex (Focus reveals a chart before viewport observation does.) -->
<div
  class="h-full"
  bind:this={element}
  role="region"
  aria-label={label}
  tabindex={visible || print.active ? -1 : 0}
  onfocus={() => (visible = true)}
>
  {#if visible || print.active}
    {@render children()}
  {:else}
    <div class={placeholderClass} aria-hidden="true"></div>
  {/if}
</div>
