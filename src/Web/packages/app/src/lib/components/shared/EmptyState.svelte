<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const emptyStateVariants = tv({
    slots: {
      root: "flex flex-col items-center justify-center text-center",
      art: "shrink-0",
      title: "font-medium text-foreground",
      body: "max-w-md text-muted-foreground",
      actions: "flex flex-wrap items-center justify-center gap-2",
      footnote: "text-xs text-muted-foreground",
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
          footnote: "mt-1",
        },
        // A card, dialog or widget, where a full-size painting would crowd its neighbours.
        compact: {
          root: "gap-2 px-4 py-3",
          art: "size-16",
          title: "text-sm",
          body: "text-xs",
        },
      },
      frame: {
        // Sits inside the caller's own card or panel.
        plain: {},
        // Inside a Card.Root this component draws, whose own py-6 stands in for part of the padding.
        card: {},
        // Inside a card that is already there: the dashed empty-state mark as an outline, with no
        // fill or shadow, since a filled tile may not nest in a card.
        outline: { root: "rounded-lg border border-dashed" },
      },
      // A page or section heading, which reads at heading weight.
      heading: { true: {}, false: {} },
    },
    compoundVariants: [
      { size: "default", frame: "card", class: { root: "py-4" } },
      { size: "compact", frame: "card", class: { root: "py-0" } },
      { size: "default", heading: true, class: { title: "text-lg font-semibold" } },
    ],
    defaultVariants: { size: "default", frame: "plain", heading: false },
  });

  export type EmptyStateSize = VariantProps<typeof emptyStateVariants>["size"];
</script>

<script lang="ts">
  import { onMount, type Snippet } from "svelte";
  import type { ArtworkId, IconArtworkSource, PaletteId } from "water-ui";
  import { stillArtwork } from "#lib/watercolour-deferral.svelte.js";
  import { watercolourWanted } from "#lib/stores/illustrations.svelte.js";
  import EmptyStatePlainIcon, { hasPlainGlyph } from "./EmptyStatePlainIcon.svelte";
  import * as Card from "#lib/components/ui/card/index.js";
  import { cn } from "#lib/utils/index.js";

  interface Props {
    /**
     * A catalogue artwork id, or a Lucide icon source from `#lib/watercolour-icons.js`.
     * Without a GPU a catalogue artwork plays its baked reveal (its final frame under
     * reduced motion); an icon source draws its baked final, else the plain Lucide icon.
     * With illustrations off or the browser saving data, either shows the Lucide glyph it
     * stands for (see `EmptyStatePlainIcon`), and art with none shows nothing.
     */
    art: ArtworkId | IconArtworkSource;
    /** Defaults to the palette the artwork is baked in, so the fallback matches the live paint. */
    palette?: PaletteId;
    title: string;
    /** Renders the title as a heading of this level; without it the title is a paragraph. */
    headingLevel?: 2 | 3 | 4;
    body?: string;
    /** Rich body content, rendered after `body`. */
    children?: Snippet;
    action?: Snippet;
    /** A low-emphasis line after the actions, e.g. a pointer to somewhere else to look. */
    footnote?: Snippet;
    size?: EmptyStateSize;
    /**
     * `plain` sits inside the caller's own card or panel; `card` is a standalone card;
     * `dashed` is a standalone dashed card, the design system's mark for content yet to be
     * added; `outline` is that dashed mark inside a card that is already there.
     */
    variant?: "plain" | "card" | "dashed" | "outline";
    class?: string;
    "data-testid"?: string;
  }

  let {
    art,
    palette,
    title,
    headingLevel,
    body,
    children,
    action,
    footnote,
    size = "default",
    variant = "plain",
    class: className,
    "data-testid": testId,
  }: Props = $props();

  // Most cards that can be empty are not, so their pages never load the watercolour. The art slot
  // keeps its size until the painting arrives.
  const painting = stillArtwork(() => import("./EmptyStateArtwork.svelte"));
  // After hydration: the server renders the held slot whatever the device's preference.
  let hydrated = $state(false);
  onMount(() => {
    hydrated = true;
  });
  const glyphOnly = $derived(hydrated && !watercolourWanted());
  const inCard = $derived(variant === "card" || variant === "dashed");
  const styles = $derived(
    emptyStateVariants({
      size,
      frame: inCard ? "card" : variant === "outline" ? "outline" : "plain",
      heading: headingLevel !== undefined,
    })
  );
</script>

{#snippet content()}
  <div class={cn(styles.root(), !inCard && className)} data-testid={inCard ? undefined : testId}>
    {#if glyphOnly}
      {#if hasPlainGlyph(art)}
        <EmptyStatePlainIcon {art} class={styles.art()} />
      {/if}
    {:else if painting.component}
      <painting.component {art} {palette} class={styles.art()} />
    {:else}
      <div class={styles.art()} aria-hidden="true"></div>
    {/if}
    <svelte:element this={headingLevel ? `h${headingLevel}` : "p"} class={styles.title()}>
      {title}
    </svelte:element>
    {#if body || children}
      <div class={styles.body()}>
        {#if body}<p>{body}</p>{/if}
        {@render children?.()}
      </div>
    {/if}
    {#if action}
      <div class={styles.actions()}>{@render action()}</div>
    {/if}
    {#if footnote}
      <div class={styles.footnote()}>{@render footnote()}</div>
    {/if}
  </div>
{/snippet}

{#if inCard}
  <Card.Root variant={variant === "dashed" ? "dashed" : "default"} class={className} data-testid={testId}>
    {@render content()}
  </Card.Root>
{:else}
  {@render content()}
{/if}
