<script lang="ts">
  import { ShieldCheck, ShieldAlert, ShieldX, BellOff } from "lucide-svelte";

  /**
   * Coarse health summary surfaced at the top of the alerts surface. Four states:
   * <ul>
   *   <li><c>ok</c> — every channel is healthy and the tenant is reachable.</li>
   *   <li><c>warn</c> — at least one channel is degraded but a fallback exists.</li>
   *   <li><c>bad</c> — at least one channel is unreachable with no working backup.</li>
   *   <li><c>dnd</c> — Do Not Disturb is on; non-critical rules are suppressed.</li>
   * </ul>
   * The strip is informational; it doesn't link anywhere by itself — the row
   * actions on the overview drive the user to the right setting.
   */
  type ArmedState = "ok" | "warn" | "bad" | "dnd";

  interface Props {
    state: ArmedState;
    detail?: string;
  }

  let { state, detail }: Props = $props();

  let copy = $derived(messageFor(state, detail));
  let Icon = $derived(iconFor(state));
  let bg = $derived(bgFor(state));
  let fg = $derived(fgFor(state));

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

  function bgFor(s: ArmedState): string {
    switch (s) {
      case "ok":
        return "bg-emerald-500/10 border-emerald-500/30";
      case "warn":
        return "bg-amber-500/10 border-amber-500/30";
      case "bad":
        return "bg-red-500/10 border-red-500/40";
      case "dnd":
        return "bg-indigo-500/10 border-indigo-500/30";
    }
  }

  function fgFor(s: ArmedState): string {
    switch (s) {
      case "ok":
        return "text-emerald-700 dark:text-emerald-400";
      case "warn":
        return "text-amber-700 dark:text-amber-400";
      case "bad":
        return "text-red-700 dark:text-red-400";
      case "dnd":
        return "text-indigo-700 dark:text-indigo-400";
    }
  }
</script>

<div
  role="status"
  class="flex items-center gap-3 rounded-lg border px-4 py-3 {bg}"
>
  <span class="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-background {fg}">
    <Icon class="h-4 w-4" />
  </span>
  <div class="text-sm font-medium {fg}">{copy}</div>
</div>
