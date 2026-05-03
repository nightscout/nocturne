<script lang="ts">
  import { Bell, Smartphone, Globe } from "lucide-svelte";
  import { AlertRuleSeverity } from "$api-clients";
  import type { ConditionNode } from "./types";
  import { summarizeCondition } from "./summarizeCondition";

  interface Props {
    name: string;
    severity: AlertRuleSeverity;
    condition: ConditionNode | null;
  }

  let { name, severity, condition }: Props = $props();

  // Derived previews — re-summarise on every dependency change. Title falls
  // back to a placeholder when the user hasn't named the rule yet.
  let titleText = $derived(name?.trim() || "(Untitled rule)");
  let bodyText = $derived(
    condition ? summarizeCondition(condition) || "Always fire" : "Always fire",
  );
  let severityDot = $derived(severityClass(severity));

  function severityClass(s: AlertRuleSeverity): string {
    switch (s) {
      case AlertRuleSeverity.Critical:
        return "bg-red-500";
      case AlertRuleSeverity.Warning:
        return "bg-amber-500";
      case AlertRuleSeverity.Info:
        return "bg-sky-500";
      default:
        return "bg-muted-foreground";
    }
  }

  // Format current local time for the lock-screen mockup. Updated lazily —
  // the user only needs an approximate clock, not a ticking one.
  let nowLabel = $derived(
    new Date().toLocaleTimeString(undefined, {
      hour: "numeric",
      minute: "2-digit",
    }),
  );
</script>

<!--
  Right-rail live preview surface for the editor. Three stacked mock devices
  driven by the unsaved rule. Visual fidelity over functional fidelity — the
  goal is "what will this look like when it fires", not a pixel-perfect
  platform replica.
-->
<div class="space-y-4">
  <!-- Browser push -->
  <div class="space-y-1.5">
    <div class="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground">
      <Globe class="h-3 w-3" /> Browser push
    </div>
    <div class="rounded-md border bg-card p-3 shadow-sm">
      <div class="flex items-start gap-2">
        <span class="mt-0.5 grid h-7 w-7 shrink-0 place-items-center rounded bg-muted">
          <Bell class="h-3.5 w-3.5" />
        </span>
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-1.5">
            <span class="h-2 w-2 rounded-full {severityDot}" aria-hidden="true"></span>
            <span class="truncate text-sm font-semibold">{titleText}</span>
          </div>
          <div class="text-xs text-muted-foreground line-clamp-2">{bodyText}</div>
          <div class="mt-1 text-[10px] uppercase tracking-wider text-muted-foreground">
            Nocturne · just now
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- Mobile lock screen -->
  <div class="space-y-1.5">
    <div class="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground">
      <Smartphone class="h-3 w-3" /> Mobile lock screen
    </div>
    <div
      class="rounded-2xl border bg-gradient-to-b from-zinc-100 to-zinc-200 p-4 dark:from-zinc-800 dark:to-zinc-900"
    >
      <div class="text-center text-xs text-muted-foreground">{nowLabel}</div>
      <div class="mt-3 rounded-xl bg-card/95 p-3 shadow backdrop-blur">
        <div class="flex items-center gap-1.5">
          <span class="h-2 w-2 rounded-full {severityDot}" aria-hidden="true"></span>
          <span class="text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
            Nocturne
          </span>
          <span class="ml-auto text-[10px] text-muted-foreground">now</span>
        </div>
        <div class="mt-1 text-sm font-semibold leading-tight">{titleText}</div>
        <div class="text-xs text-muted-foreground line-clamp-2 leading-tight">{bodyText}</div>
      </div>
    </div>
  </div>

  <!-- In-app toast -->
  <div class="space-y-1.5">
    <div class="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground">
      <Bell class="h-3 w-3" /> In-app toast
    </div>
    <div class="rounded-md border bg-background p-3 shadow-md">
      <div class="flex items-start gap-2">
        <span class="mt-0.5 h-2 w-2 shrink-0 rounded-full {severityDot}" aria-hidden="true"></span>
        <div class="min-w-0 flex-1">
          <div class="text-sm font-semibold">{titleText}</div>
          <div class="text-xs text-muted-foreground line-clamp-2">{bodyText}</div>
          <div class="mt-2 flex gap-1">
            <span class="rounded border px-1.5 py-0.5 text-[10px] text-muted-foreground">Snooze</span>
            <span class="rounded border px-1.5 py-0.5 text-[10px] text-muted-foreground">Dismiss</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</div>
