<script lang="ts">
  import { goto } from "$app/navigation";
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import { Bell, Check, ChevronRight } from "lucide-svelte";
  import type { AlertRuleResponse, AlertRuleSeverity } from "$api-clients";
  import { testFire } from "$api/generated/alertRules.generated.remote";

  /**
   * First-run trust ladder shown above the overview when the tenant has no
   * critical rule yet. Four steps, in trust-building order:
   *
   *   1. Grant browser notification permission (the only step that proves
   *      sound-and-fury will actually reach the user).
   *   2. Add a critical rule (a "wake me if BG &lt; 55" baseline).
   *   3. Test fire — the proof gesture.
   *   4. Add a backup channel — the redundancy gesture.
   *
   * Each step is independently completable; the card disappears once all
   * four are done. Replaces the legacy `/setup` wizard.
   */
  interface Props {
    rules: AlertRuleResponse[];
  }

  let { rules }: Props = $props();

  // ---- Step state ----
  let permission = $state<NotificationPermission | "unknown">(
    typeof Notification !== "undefined" ? Notification.permission : "unknown",
  );
  let testing = $state<string | null>(null);

  let hasCritical = $derived(rules.some((r) => r.severity === ("critical" as AlertRuleSeverity)));
  let hasMultiChannel = $derived(rules.some((r) => (r.channels?.length ?? 0) >= 2));
  // The "test fire" step is satisfied when *any* rule has at least one
  // delivery — we can't reach into history from here without an extra fetch
  // and the proof gesture is "I just felt it", not "the audit log says so".
  let testedSomething = $state(false);

  let allDone = $derived(
    permission === "granted" && hasCritical && hasMultiChannel && testedSomething,
  );

  async function requestPermission(): Promise<void> {
    if (typeof Notification === "undefined") return;
    permission = await Notification.requestPermission();
  }

  async function fireFirstCriticalRule(): Promise<void> {
    const target = rules.find((r) => r.severity === ("critical" as AlertRuleSeverity)) ?? rules[0];
    if (!target?.id) return;
    testing = target.id;
    try {
      await testFire(target.id);
      testedSomething = true;
    } finally {
      testing = null;
    }
  }
</script>

{#if !allDone}
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Bell class="h-5 w-5 text-primary" /> Get alerts working
      </CardTitle>
      <CardDescription>
        Four short steps. The point isn't the checklist — it's the moment your phone buzzes
        and you trust it.
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-2">
      {@render step({
        n: 1,
        done: permission === "granted",
        label: "Grant browser notification permission",
        cta:
          permission === "granted"
            ? "Granted"
            : permission === "denied"
              ? "Denied — reset in browser"
              : "Grant permission",
        ctaDisabled:
          permission === "granted" ||
          permission === "denied" ||
          typeof Notification === "undefined",
        onCta: requestPermission,
      })}
      {@render step({
        n: 2,
        done: hasCritical,
        label: "Add a critical rule (e.g. BG below 55)",
        cta: hasCritical ? "Done" : "Add rule",
        ctaDisabled: hasCritical,
        onCta: () => goto("/settings/alerts/new"),
      })}
      {@render step({
        n: 3,
        done: testedSomething,
        label: "Test fire — feel it",
        cta: testedSomething ? "Fired" : testing ? "Firing…" : "Fire test",
        ctaDisabled: testing !== null || rules.length === 0,
        onCta: fireFirstCriticalRule,
      })}
      {@render step({
        n: 4,
        done: hasMultiChannel,
        label: "Add a backup channel on at least one rule",
        cta: hasMultiChannel ? "Done" : "Pick a rule",
        ctaDisabled: hasMultiChannel || rules.length === 0,
        onCta: () => {
          const target =
            rules.find((r) => r.severity === ("critical" as AlertRuleSeverity)) ?? rules[0];
          if (target?.id) goto(`/settings/alerts/${target.id}`);
        },
      })}
    </CardContent>
  </Card>
{/if}

{#snippet step(props: { n: number; done: boolean; label: string; cta: string; ctaDisabled: boolean; onCta: () => void })}
  <div class="flex items-center gap-3 rounded-md border bg-background px-3 py-2">
    <span
      class="grid h-7 w-7 shrink-0 place-items-center rounded-full text-xs font-semibold {props.done
        ? 'bg-emerald-500/15 text-emerald-700 dark:text-emerald-400'
        : 'bg-muted text-muted-foreground'}"
    >
      {#if props.done}
        <Check class="h-3.5 w-3.5" />
      {:else}
        {props.n}
      {/if}
    </span>
    <span class="flex-1 text-sm {props.done ? 'text-muted-foreground line-through' : ''}">
      {props.label}
    </span>
    <Button
      type="button"
      variant={props.done ? "ghost" : "outline"}
      size="sm"
      onclick={props.onCta}
      disabled={props.ctaDisabled}
    >
      {props.cta}
      {#if !props.done}
        <ChevronRight class="h-3.5 w-3.5 ml-1" />
      {/if}
    </Button>
  </div>
{/snippet}
