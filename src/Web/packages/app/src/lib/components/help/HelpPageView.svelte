<script lang="ts">
  import { tick, type Snippet } from "svelte";
  import { getHelpContext } from "$lib/help/help-context.svelte";
  import HelpBrowser from "./HelpBrowser.svelte";
  const { children }: { children: Snippet } = $props();
  const help = getHelpContext();
  $effect(() => {
    if (!help?.open) return;
    const scrollY = window.scrollY;
    window.scrollTo({ top: 0, behavior: "instant" });
    return () => {
      void tick().then(() => {
        if (!help.open) window.scrollTo({ top: scrollY, behavior: "instant" });
      });
    };
  });
</script>

<svelte:window
  onkeydown={(event) => {
    if (help?.open && event.key === "Escape" && !event.defaultPrevented)
      help.open = false;
  }}
/>

{#if help?.open}
  <HelpBrowser />
{/if}
<!-- Keep page controls and loaded data mounted while documentation replaces their view. -->
<div
  hidden={help?.open}
  inert={help?.open}
  class={help?.open ? "hidden" : "contents"}
>
  {@render children()}
</div>
