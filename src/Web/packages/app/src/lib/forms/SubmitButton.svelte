<script lang="ts">
  import type { Snippet } from "svelte";
  import { untrack } from "svelte";
  import type { HTMLButtonAttributes } from "svelte/elements";
  import {
    DropSurface,
    hostSurface,
    prefersReducedMotion,
    watchSurface,
    type Surface,
  } from "@nocturne/watercolour";
  import { buttonVariants, type ButtonVariant } from "$lib/components/ui/button";
  import { cn } from "$lib/utils";

  /**
   * The sizes whose padding moves onto the content. A glaze is fitted to the
   * content box, so the padding has to live there for it to cover the control.
   */
  const CONTENT_PADDING = {
    default: "gap-2 px-4 py-2 has-[>svg]:px-3",
    sm: "gap-1.5 px-3 has-[>svg]:px-2.5",
    lg: "gap-2 px-6 has-[>svg]:px-4",
  } as const;

  /** How long the glaze stays up before the button returns to rest. */
  const HOLD_MS = 1600;

  interface Props extends Omit<HTMLButtonAttributes, "class" | "children"> {
    /**
     * A count that moves on each successful save, e.g. `Submission.saved`.
     * The button plays its glaze once per move, never on mount.
     */
    saved?: number;
    variant?: ButtonVariant;
    size?: keyof typeof CONTENT_PADDING;
    class?: string;
    children: Snippet;
  }

  let {
    saved = 0,
    variant = "default",
    size = "default",
    type = "submit",
    class: className,
    children,
    ...rest
  }: Props = $props();

  let playing = $state(false);
  let generation = $state(0);
  let played = untrack(() => saved);

  $effect(() => {
    const next = saved;
    if (next === played) return;
    played = next;
    // A glaze that cannot move would only flash on and off.
    if (prefersReducedMotion()) return;
    generation = next;
    playing = true;
    const timer = setTimeout(() => (playing = false), HOLD_MS);
    return () => clearTimeout(timer);
  });

  let page = $state<Surface>("light");
  $effect(() => {
    page = hostSurface();
    return watchSurface((next) => (page = next));
  });

  // A primary fill is the inverse of the page it sits on, and the paint
  // composites against the ground it lands on.
  const surface = $derived<Surface | undefined>(
    variant === "default" ? (page === "dark" ? "light" : "dark") : undefined,
  );
</script>

<DropSurface
  as="button"
  {type}
  {...rest}
  data-slot="button"
  name="saved"
  kind="glaze"
  palette="moss"
  spatter={false}
  shown={playing}
  {generation}
  {surface}
  revealMs={600}
  exitMs={600}
  class={cn(buttonVariants({ variant, size }), "p-0", className)}
  contentClass={cn("flex size-full items-center justify-center", CONTENT_PADDING[size])}
>
  {@render children()}
</DropSurface>
