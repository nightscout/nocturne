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

  /**
   * Lifts the glaze on a dark fill. DropSurface dims dark-ground paint so a
   * wide mark does not dry to a pale slab; on a label-sized fill that dimming
   * left the glaze barely readable. Judged by eye on the primary button.
   */
  const DARK_FILL_PEAK = 1.6;

  /** Seeds the glaze. Not the button's `name`, which a DropSurface cannot forward. */
  const GLAZE_SEED = "saved";

  /**
   * What DropSurface owns on its host: `name` is its seed, and it sets these
   * handlers and `class` after spreading the rest, so a caller's would be lost.
   */
  type Owned =
    | "class"
    | "children"
    | "name"
    | "onpointerenter"
    | "onpointerleave"
    | "onfocusin"
    | "onfocusout";

  interface Props extends Omit<HTMLButtonAttributes, Owned> {
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
  name={GLAZE_SEED}
  kind="glaze"
  palette="moss"
  spatter={false}
  shown={playing}
  {generation}
  {surface}
  peak={surface === "dark" ? DARK_FILL_PEAK : 1}
  revealMs={600}
  exitMs={600}
  class={cn(
    buttonVariants({ variant, size }),
    "p-0",
    // A saved form is clean, so its submit disables at the moment the glaze
    // lands; it dims to rest as the paint leaves instead.
    playing && "disabled:opacity-100",
    className,
  )}
  contentClass={cn("flex size-full items-center justify-center", CONTENT_PADDING[size])}
>
  {@render children()}
</DropSurface>
