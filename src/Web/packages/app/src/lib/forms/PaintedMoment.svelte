<script lang="ts">
  import type { Snippet } from "svelte";
  import { untrack } from "svelte";
  import { Artwork, type ArtworkId, type IconArtworkSource } from "@nocturne/watercolour";
  import { cn } from "$lib/utils";

  /**
   * A small painted mark that confirms something just took effect, then
   * leaves. It plays when `count` rises, never on mount, so a list row or a
   * form that re-renders does not replay it. Under reduced motion the artwork
   * lands as its finished still.
   */
  let {
    count,
    artwork,
    icon,
    holdMs = 4000,
    class: className,
    artClass = "size-8",
    idle,
    children,
  }: {
    count: number;
    artwork?: ArtworkId;
    icon?: IconArtworkSource;
    holdMs?: number;
    class?: string;
    artClass?: string;
    /** Shown in the moment's place while it is not playing. */
    idle?: Snippet;
    /** Copy shown beside the paint while it plays. */
    children?: Snippet;
  } = $props();

  let shown = $state(false);
  let generation = $state(0);
  let played = untrack(() => count);
  let timer: ReturnType<typeof setTimeout> | undefined;

  $effect(() => {
    const next = count;
    const rose = next > played;
    played = next;
    if (!rose) return;
    clearTimeout(timer);
    untrack(() => generation++);
    shown = true;
    timer = setTimeout(() => (shown = false), untrack(() => holdMs));
  });

  $effect(() => () => clearTimeout(timer));
</script>

{#if shown}
  <span data-testid="painted-moment" role={children ? "status" : undefined} class={cn("inline-flex items-center gap-2", className)}>
    {#key generation}
      <Artwork {artwork} {icon} motion="auto" autoplay="once" class={cn("shrink-0", artClass)} />
    {/key}
    {#if children}{@render children()}{/if}
  </span>
{:else if idle}
  {@render idle()}
{/if}
