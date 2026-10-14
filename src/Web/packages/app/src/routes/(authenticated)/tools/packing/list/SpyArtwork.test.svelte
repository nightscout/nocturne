<script module lang="ts">
  import { vi } from "vitest";
  import type { ArtworkPlayer, PlayerState, PlayerStateCallback } from "@nocturne/watercolour";

  export const spyPlayer = {
    seekTo: vi.fn<(progress: number) => void>(),
    play: vi.fn<() => void>(),
    finishImmediately: vi.fn<() => void>(),
  };

  let report: PlayerStateCallback | undefined;

  /** Reports a state change as the mounted player would. */
  export function settle(mode: PlayerState["mode"]) {
    report?.({ mode } as PlayerState);
  }
</script>

<script lang="ts">
  import type { PlayerReadyCallback } from "@nocturne/watercolour";

  let {
    onready,
    onstatechange,
  }: { onready?: PlayerReadyCallback; onstatechange?: PlayerStateCallback } = $props();

  $effect(() => {
    report = onstatechange;
    const cleanup = onready?.(spyPlayer as unknown as ArtworkPlayer);
    return () => {
      report = undefined;
      cleanup?.();
    };
  });
</script>

<div data-testid="suitcase"></div>
