<script lang="ts" generics="Id extends string">
  import type { Snippet } from "svelte";
  import { DropGroup, DropSurface } from "water-ui";
  import { itemVariants } from "#lib/components/ui/item/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { cn } from "#lib/utils/index.js";
  import { stillArtwork } from "#lib/watercolour-deferral.svelte.js";
  import Check from "@lucide/svelte/icons/check";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";

  interface Props {
    /** What the list holds, as a screen reader names it and the paint groups it: "Alert types". */
    label: string;
    /** Each row's test id is this and the option's id. */
    testIdPrefix: string;
    options: readonly { id: Id; label: string; description: string }[];
    selected: Id | null;
    onPick: (id: Id) => void;
    icon: Snippet<[Id]>;
    /** Whether the options are still on their way; the list holds their place until they arrive. */
    loading?: boolean;
    class?: string;
  }

  let {
    label,
    testIdPrefix,
    options,
    selected,
    onPick,
    icon,
    loading = false,
    class: className,
  }: Props = $props();

  // The padding is on the content, which sits above the paint.
  const ROW = cn(itemVariants({ variant: "row", interactive: true }), "p-0");

  // Absolutely positioned over the row, so the list lays out the same before it arrives.
  const selectionEdge = stillArtwork(() => import("#lib/watercolour/selection-edge.js"));

  const PLACEHOLDERS = [0, 1, 2, 3];
</script>

{#if loading && options.length === 0}
  <ul class={cn("divide-y border-y", className)} aria-label={label} aria-busy="true">
    {#each PLACEHOLDERS as placeholder (placeholder)}
      <li class="flex items-center gap-3 p-3">
        <Skeleton class="size-5 shrink-0 rounded-full" />
        <span class="flex-1 space-y-2">
          <Skeleton class="h-4 w-32" />
          <Skeleton class="h-4 w-64 max-w-full" />
        </span>
      </li>
    {/each}
  </ul>
{:else}
  <DropGroup name={label}>
    <ul class={cn("divide-y border-y", className)} aria-label={label}>
      {#each options as option (option.id)}
        {@const isSelected = selected === option.id}
        <li class="relative">
          <!-- The row is the surface, so the paint lands under its copy; the chosen row has the
               edge and stays plain. -->
          <DropSurface
            as="button"
            type="button"
            name={option.id}
            shown={isSelected ? false : undefined}
            data-testid="{testIdPrefix}{option.id}"
            aria-current={isSelected ? "true" : undefined}
            onclick={() => onPick(option.id)}
            class={ROW}
            contentClass="flex w-full items-center gap-3 p-3"
          >
            <span data-drop-obstacle class="shrink-0 text-muted-foreground" aria-hidden="true">
              {@render icon(option.id)}
            </span>
            <span class="min-w-0 flex-1">
              <span data-drop-obstacle class="block font-medium">{option.label}</span>
              <span data-drop-obstacle class="block text-sm text-muted-foreground">
                {option.description}
              </span>
            </span>
            {#if isSelected}
              <Check class="size-4 shrink-0 text-primary" aria-hidden="true" />
            {:else}
              <ChevronRight class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            {/if}
          </DropSurface>
          {#if selectionEdge.component}
            <selectionEdge.component active={isSelected} />
          {/if}
        </li>
      {/each}
    </ul>
  </DropGroup>
{/if}
