<script lang="ts">
  import { BellOff } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { severity } from "./severity";

  /**
   * Notice shown while Do Not Disturb is suppressing non-critical rules.
   *
   * This replaced a four-state "armed" strip whose copy reported channel health
   * ("All channels healthy", "Critical channels unreachable") from the
   * active-alert count. The API exposes no per-channel delivery probe, so those
   * states asserted reachability nothing had measured. Render this only when DND
   * is on right now.
   */
  interface Props {
    /** Provided when DND can be turned off inline. */
    onDisableDnd?: () => void | Promise<void>;
    disablingDnd?: boolean;
  }

  let { onDisableDnd, disablingDnd = false }: Props = $props();

  // DND isn't an alert severity (it's a "notifications paused" state) but wants
  // the same calm-blue treatment as info, so it routes through the same token.
  const stripClass = severity("info", "strip");
</script>

<div
  role="status"
  class="flex items-center gap-3 rounded-lg border px-4 py-3 {stripClass}"
>
  <span
    class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-background"
  >
    <BellOff class="h-4 w-4" />
  </span>
  <div class="flex-1 text-sm font-medium">
    Do Not Disturb is on. Only critical rules will fire.
  </div>
  {#if onDisableDnd}
    <Button
      type="button"
      variant="outline"
      size="sm"
      onclick={onDisableDnd}
      disabled={disablingDnd}
    >
      Turn off
    </Button>
  {/if}
</div>
