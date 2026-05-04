<script lang="ts">
  import { ShieldCheck, ShieldAlert, ShieldX, BellOff } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { severity } from "./severity";

  /**
   * Coarse health summary surfaced at the top of the alerts surface. Four
   * states:<ul>  <li><c>ok</c> — every channel is healthy and the tenant is reachable.</li>
   *   <li><c>warn</c> — at least one channel is degraded but a fallback exists.</li>
   *   <li><c>bad</c> — at least one channel is unreachable with no working backup.</li>
   *   <li><c>dnd</c> — Do Not Disturb is on; non-critical rules are suppressed.</li>
   * </ul>
   */
  type ArmedState = "ok" | "warn" | "bad" | "dnd";

  interface Props {
    state: ArmedState;
    detail?: string;
    /** Provided when state === "dnd" and DND can be turned off inline. */
    onDisableDnd?: () => void | Promise<void>;
    disablingDnd?: boolean;
  }

  let { state, detail, onDisableDnd, disablingDnd = false }: Props = $props();

  let copy = $derived(messageFor(state, detail));
  let Icon = $derived(iconFor(state));
  // dnd isn't an alert severity (it's a "system paused" state) but visually
  // wants the same calm-blue treatment as info, so we route it through the
  // same token. ok maps to status-normal directly since severity() doesn't
  // model a "healthy" severity.
  let stripClass = $derived(
    state === "ok"
      ? "bg-status-normal/10 border-status-normal/30 text-status-normal"
      : state === "warn"
        ? severity("warning", "strip")
        : state === "bad"
          ? severity("critical", "strip")
          : severity("info", "strip")
  );

  function messageFor(s: ArmedState, d: string | undefined): string {
    if (d) return d;
    switch (s) {
      case "ok":
        return "All channels healthy. Alerts are armed.";
      case "warn":
        return "One or more channels degraded. Alerts will fall back to backup channels.";
      case "bad":
        return "Critical channels unreachable. Alerts may not deliver.";
      case "dnd":
        return "Do Not Disturb is on. Only critical rules will fire.";
    }
  }

  function iconFor(s: ArmedState) {
    switch (s) {
      case "ok":
        return ShieldCheck;
      case "warn":
        return ShieldAlert;
      case "bad":
        return ShieldX;
      case "dnd":
        return BellOff;
    }
  }
</script>

<div
  role="status"
  class="flex items-center gap-3 rounded-lg border px-4 py-3 {stripClass}"
>
  <span
    class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-background"
  >
    <Icon class="h-4 w-4" />
  </span>
  <div class="flex-1 text-sm font-medium">{copy}</div>
  {#if state === "dnd" && onDisableDnd}
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
