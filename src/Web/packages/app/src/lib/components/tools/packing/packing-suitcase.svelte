<script lang="ts">
  import { untrack } from "svelte";
  import { Tween, prefersReducedMotion } from "svelte/motion";
  import { cubicOut } from "svelte/easing";
  import {
    Artwork,
    DEFAULT_TAIL,
    PACKING_LIVE_CLOCK,
    mountPlayer,
    packingSuitcaseScene,
    type ArtworkPlayer,
    type PackingSuitcase,
    type PlayerState,
    type TimedOp,
  } from "@nocturne/watercolour";

  /**
   * The suitcase painted one band per packed item. The page reports each pack
   * and unpack as it happens through `pack` and `unpack`, and every removal
   * through `countChanged`, after an `unpack` when the item was packed.
   * `packed` is the list as the suitcase mounts and is not read again;
   * `total` is the list as it stands.
   */
  interface Props {
    /** Categories of the packed items when the suitcase mounts, in list order. Read once. */
    packed: readonly string[];
    total: number;
    /** State of whichever player is drawing, live or the catalogue fallback. */
    onstatechange?: (state: PlayerState) => void;
    class?: string;
  }

  let { packed, total, onstatechange, class: className = "" }: Props = $props();

  /** What is painted, in painting order: the player is rebuilt from this. */
  const stack = untrack(() => [...packed]);
  let packedCount = $state(stack.length);
  let painter: PackingSuitcase | undefined;
  let player: ArtworkPlayer | undefined;
  /** Painted before the player was ready to take it. */
  let pending: TimedOp[][] = [];
  let live = $state(true);

  export function pack(category: string, stillUnpacked: number) {
    stack.push(category);
    packedCount = stack.length;
    if (painter) paint(painter.pack(category, stillUnpacked));
  }

  export function unpack() {
    stack.pop();
    packedCount = stack.length;
    if (painter) paint(painter.unpack());
  }

  export function countChanged(stillUnpacked: number) {
    if (painter) paint(painter.countChanged(stillUnpacked));
  }

  function paint(ops: TimedOp[]) {
    if (ops.length === 0) return;
    if (!player) {
      pending.push(ops);
      return;
    }
    player.paint(ops, PACKING_LIVE_CLOCK);
    if (player.state.motion === "reduced") player.finishImmediately();
  }

  let canvas: HTMLCanvasElement | undefined = $state();
  let frame: HTMLDivElement | undefined = $state();
  $effect(() => {
    const el = canvas;
    const container = frame;
    if (!el || !container || !live) return;
    const unmount = mountPlayer(container, el, {
      scene: (module, width, height, dpr) => {
        const scene = packingSuitcaseScene(module, width, height, {
          packed: stack,
          unpacked: untrack(() => total) - stack.length,
          dpr,
        });
        painter = scene.painter;
        pending = [];
        return scene.sceneJson;
      },
      mode: "live",
      durationMs: 1500,
      onReady: (ready) => {
        player = ready;
        for (const ops of pending.splice(0)) paint(ops);
        return () => (player = undefined);
      },
      onStateChange: (state) =>
        untrack(() => {
          if (state.mode === "live" || state.mode === "pending") onstatechange?.(state);
          else live = false;
        }),
    });
    return () => {
      unmount();
      painter = undefined;
      pending = [];
    };
  });

  // Without a live engine the catalogue suitcase stands in, seeking its reveal
  // to the share packed: it draws every stroke in its first `1 - DEFAULT_TAIL`
  // and settles in the rest, so the settle plays once everything is in.
  const PAINT_END = 1 - DEFAULT_TAIL;
  const complete = $derived(total > 0 && packedCount >= total);
  const reveal = new Tween(0, { easing: cubicOut });
  let fallback = $state<ArtworkPlayer>();

  $effect(() => {
    if (live) return;
    const target = total ? (Math.min(packedCount, total) / total) * PAINT_END : 0;
    // Each backward frame is a checkpoint replay, so an unpack jumps.
    const instant = prefersReducedMotion.current || target < untrack(() => reveal.target);
    void reveal.set(target, { duration: instant ? 0 : 700 });
  });

  $effect(() => {
    const catalogue = fallback;
    if (!catalogue) return;
    const at = reveal.current;
    if (!complete || at < PAINT_END) catalogue.seekTo(at);
    else if (prefersReducedMotion.current) catalogue.finishImmediately();
    else catalogue.play();
  });
</script>

{#if live}
  <div bind:this={frame} aria-hidden="true" class="relative overflow-hidden {className}" data-testid="suitcase">
    <canvas bind:this={canvas} class="absolute inset-0 block"></canvas>
  </div>
{:else}
  <!-- Reduced motion keeps the baked strip: each seek draws one still frame, so progress shows without animating. -->
  <Artwork
    artwork="suitcase"
    palette="dusk"
    autoplay="never"
    mode={prefersReducedMotion.current ? "baked" : "auto"}
    onready={(catalogue) => {
      fallback = catalogue;
      return () => (fallback = undefined);
    }}
    onstatechange={(state) => onstatechange?.(state)}
    class={className}
  />
{/if}
