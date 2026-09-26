<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const emptyStateVariants = tv({
    slots: {
      root: "flex flex-col items-center justify-center text-center",
      art: "shrink-0",
      title: "font-medium text-foreground",
      body: "max-w-md text-muted-foreground",
      actions: "flex flex-wrap items-center justify-center gap-2",
    },
    variants: {
      size: {
        // A page, tab or settings section with nothing in it yet.
        default: {
          root: "gap-3 px-6 py-10",
          art: "size-40",
          title: "text-base",
          body: "text-sm",
          actions: "mt-1",
        },
        // A card, dialog or widget, where a full-size painting would crowd its neighbours.
        compact: {
          root: "gap-2 px-4 py-6",
          art: "size-16",
          title: "text-sm",
          body: "text-xs",
        },
      },
      // Inside a Card, whose own py-6 stands in for part of the padding.
      framed: { true: {}, false: {} },
    },
    compoundVariants: [
      { size: "default", framed: true, class: { root: "py-4" } },
      { size: "compact", framed: true, class: { root: "py-0" } },
    ],
    defaultVariants: { size: "default", framed: false },
  });

  export type EmptyStateSize = VariantProps<typeof emptyStateVariants>["size"];
</script>

<script lang="ts">
  import type { Snippet } from "svelte";
  import {
    Artwork,
    defaultPaletteFor,
    type ArtworkId,
    type IconArtworkSource,
    type PaletteId,
  } from "@nocturne/watercolour";
  import * as Card from "$lib/components/ui/card";
  import { cn } from "$lib/utils";

  interface Props {
    /**
     * A catalogue artwork id, or a Lucide icon source from `$lib/watercolour-icons`.
     * Without a GPU a catalogue artwork plays its baked reveal (its final frame under
     * reduced motion); an icon source draws its baked final, else the plain Lucide icon.
     */
    art: ArtworkId | IconArtworkSource;
    /** Defaults to the palette the artwork is baked in, so the fallback matches the live paint. */
    palette?: PaletteId;
    title: string;
    body?: string;
    /** Rich body content, rendered after `body`. */
    children?: Snippet;
    action?: Snippet;
    size?: EmptyStateSize;
    /**
     * `plain` sits inside the caller's own card or panel; `card` is a standalone card;
     * `dashed` is a dashed card, the design system's mark for content yet to be added.
     */
    variant?: "plain" | "card" | "dashed";
    class?: string;
    "data-testid"?: string;
  }

  let {
    art,
    palette,
    title,
    body,
    children,
    action,
    size = "default",
    variant = "plain",
    class: className,
    "data-testid": testId,
  }: Props = $props();

  const icon = $derived(typeof art === "string" ? undefined : art);
  const artwork = $derived(typeof art === "string" ? art : undefined);
  const resolvedPalette = $derived(
    palette ?? defaultPaletteFor(typeof art === "string" ? art : `lucide-${art.name}`)
  );
  const styles = $derived(emptyStateVariants({ size, framed: variant !== "plain" }));
</script>

{#snippet content()}
  <div class={cn(styles.root(), variant === "plain" && className)} data-testid={variant === "plain" ? testId : undefined}>
    <Artwork
      {artwork}
      {icon}
      palette={resolvedPalette}
      motion="auto"
      autoplay="once"
      class={styles.art()}
    />
    <p class={styles.title()}>{title}</p>
    {#if body || children}
      <div class={styles.body()}>
        {#if body}<p>{body}</p>{/if}
        {@render children?.()}
      </div>
    {/if}
    {#if action}
      <div class={styles.actions()}>{@render action()}</div>
    {/if}
  </div>
{/snippet}

{#if variant === "plain"}
  {@render content()}
{:else}
  <Card.Root variant={variant === "dashed" ? "dashed" : "default"} class={className} data-testid={testId}>
    {@render content()}
  </Card.Root>
{/if}
