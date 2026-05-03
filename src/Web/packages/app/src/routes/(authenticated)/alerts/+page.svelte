<script lang="ts">
  import { onMount } from "svelte";
  import { goto } from "$app/navigation";
  import {
    getRules,
    deleteRule,
    toggleRule,
    testFire,
  } from "$api/generated/alertRules.generated.remote";
  import {
    getActiveAlerts,
    getAlertHistory,
    acknowledge,
  } from "$api/generated/alerts.generated.remote";
  import {
    get as getTenantAlertSettings,
    update as updateTenantAlertSettings,
  } from "$api/generated/tenantAlertSettings.generated.remote";
  import type {
    AlertRuleResponse,
    ActiveExcursionResponse,
    TenantAlertSettingsResponse,
  } from "$api-clients";

  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import { Bell, Plus, AlertTriangle, Check, Loader2 } from "lucide-svelte";

  import AlertRuleRow from "$lib/components/alerts/AlertRuleRow.svelte";
  import ArmedStatusStrip from "$lib/components/alerts/ArmedStatusStrip.svelte";

  // ---- State ----
  let rules = $state<AlertRuleResponse[]>([]);
  let activeAlerts = $state<ActiveExcursionResponse[]>([]);
  let firedThisWeek = $state<number>(0);
  let dnd = $state<TenantAlertSettingsResponse | null>(null);

  let loading = $state(true);
  let error = $state<string | null>(null);
  let togglingRuleId = $state<string | null>(null);
  let deletingRuleId = $state<string | null>(null);
  let testingRuleId = $state<string | null>(null);
  let acknowledging = $state(false);
  let disablingDnd = $state(false);

  // ---- Derived ----
  let enabledCount = $derived(rules.filter((r) => r.isEnabled).length);
  let totalCount = $derived(rules.length);
  let armedState = $derived(deriveArmedState(dnd, activeAlerts));
  let ruleNamesById = $derived(
    new Map(rules.map((r) => [r.id ?? "", r.name ?? "(unnamed)"])),
  );

  function deriveArmedState(
    s: TenantAlertSettingsResponse | null,
    active: ActiveExcursionResponse[],
  ): "ok" | "warn" | "bad" | "dnd" {
    // Lightweight heuristic — we don't (yet) have a per-channel health
    // probe surfaced through the API, so this is currently driven entirely
    // by DND state and active-alert count. Wire to channel health when the
    // backend exposes it.
    if (s?.dndManualActive || s?.dndScheduleEnabled) return "dnd";
    if (active.length === 0) return "ok";
    if (active.some((a) => a.severity === "critical")) return "bad";
    return "warn";
  }

  // ---- Load ----
  async function load(): Promise<void> {
    loading = true;
    error = null;
    try {
      const [rulesResult, alertsResult, historyResult, dndResult] = await Promise.all([
        getRules(),
        getActiveAlerts(),
        getAlertHistory({ page: 1, pageSize: 50 }),
        getTenantAlertSettings().catch(() => null),
      ]);
      rules = Array.isArray(rulesResult) ? rulesResult : [];
      activeAlerts = Array.isArray(alertsResult) ? alertsResult : [];
      dnd = dndResult ?? null;

      // "Fired this week" — count history rows in the last 7 days. Falls back
      // to the unfiltered count if the timestamp shape isn't what we expect.
      const cutoff = Date.now() - 7 * 24 * 60 * 60 * 1000;
      const items = historyResult?.items ?? [];
      firedThisWeek = items.filter((h) => {
        const t = h.startedAt ? new Date(h.startedAt).getTime() : NaN;
        return Number.isFinite(t) && t >= cutoff;
      }).length;
    } catch (e) {
      error = e instanceof Error ? e.message : "Failed to load alerts";
    } finally {
      loading = false;
    }
  }

  // ---- Mutations ----
  async function handleToggleRule(ruleId: string): Promise<void> {
    togglingRuleId = ruleId;
    try {
      await toggleRule(ruleId);
      const result = await getRules();
      rules = Array.isArray(result) ? result : [];
    } finally {
      togglingRuleId = null;
    }
  }

  async function handleDeleteRule(ruleId: string): Promise<void> {
    deletingRuleId = ruleId;
    try {
      await deleteRule(ruleId);
      rules = rules.filter((r) => r.id !== ruleId);
    } finally {
      deletingRuleId = null;
    }
  }

  async function handleTestFire(ruleId: string): Promise<void> {
    testingRuleId = ruleId;
    try {
      await testFire(ruleId);
    } finally {
      testingRuleId = null;
    }
  }

  async function handleDisableDnd(): Promise<void> {
    if (!dnd) return;
    disablingDnd = true;
    try {
      const next = await updateTenantAlertSettings({
        dndManualActive: false,
        dndManualUntil: undefined,
        dndScheduleEnabled: false,
        dndScheduleStart: dnd.dndScheduleStart,
        dndScheduleEnd: dnd.dndScheduleEnd,
        timezone: dnd.timezone ?? "UTC",
      });
      dnd = next;
    } finally {
      disablingDnd = false;
    }
  }

  async function handleAcknowledge(): Promise<void> {
    acknowledging = true;
    try {
      await acknowledge({ acknowledgedBy: "web_user" });
      const result = await getActiveAlerts();
      activeAlerts = Array.isArray(result) ? result : [];
    } finally {
      acknowledging = false;
    }
  }

  function newRule(): void {
    goto("/alerts/new");
  }

  function editRule(rule: AlertRuleResponse): void {
    goto(`/alerts/${rule.id}`);
  }

  onMount(() => {
    load();
  });
</script>

<svelte:head>
  <title>Alerts · Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-5xl p-4 lg:p-6 space-y-6">
  <!-- Header -->
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
        <Bell class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Alerts</h1>
        <p class="text-sm text-muted-foreground">Rules that decide when, how, and where you're notified.</p>
      </div>
    </div>
    <div class="flex items-center gap-2">
      <Button onclick={newRule}>
        <Plus class="h-4 w-4 mr-2" /> New rule
      </Button>
    </div>
  </div>

  {#if loading}
    <SettingsPageSkeleton cardCount={3} />
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3">
        <AlertTriangle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load alerts</p>
          <p class="text-sm text-muted-foreground">{error}</p>
        </div>
      </CardContent>
    </Card>
  {:else}
    <!-- Armed status strip -->
    <ArmedStatusStrip
      state={armedState}
      onDisableDnd={armedState === "dnd" ? handleDisableDnd : undefined}
      {disablingDnd}
    />

    <!-- Stat row -->
    <div class="grid gap-3 sm:grid-cols-3">
      <Card>
        <CardContent>
          <p class="text-xs uppercase tracking-wider text-muted-foreground">Rules enabled</p>
          <p class="mt-1 text-2xl font-bold tabular-nums">
            {enabledCount}<span class="text-muted-foreground text-base font-normal"> / {totalCount}</span>
          </p>
        </CardContent>
      </Card>
      <Card>
        <CardContent>
          <p class="text-xs uppercase tracking-wider text-muted-foreground">Active now</p>
          <p class="mt-1 text-2xl font-bold tabular-nums">{activeAlerts.length}</p>
        </CardContent>
      </Card>
      <a
        href="/alerts/history"
        class="block rounded-xl outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <Card class="transition-colors hover:bg-muted/40">
          <CardContent>
            <p class="text-xs uppercase tracking-wider text-muted-foreground">Fired this week</p>
            <p class="mt-1 text-2xl font-bold tabular-nums">{firedThisWeek}</p>
          </CardContent>
        </Card>
      </a>
    </div>

    <!-- Active alerts banner (kept as a persistent surface separate from the
         FiringToast which handles fresh-fire moments). -->
    {#if activeAlerts.length > 0}
      <Card class="border-destructive/40 bg-destructive/5">
        <CardHeader>
          <div class="flex items-center justify-between">
            <CardTitle class="flex items-center gap-2 text-destructive">
              <AlertTriangle class="h-5 w-5" />
              Active alerts ({activeAlerts.length})
            </CardTitle>
            <Button variant="outline" size="sm" onclick={handleAcknowledge} disabled={acknowledging}>
              {#if acknowledging}
                <Loader2 class="h-4 w-4 mr-2 animate-spin" />
              {:else}
                <Check class="h-4 w-4 mr-2" />
              {/if}
              Acknowledge all
            </Button>
          </div>
        </CardHeader>
        <CardContent class="space-y-2">
          {#each activeAlerts as a (a.id)}
            <div class="flex items-center gap-3 rounded-md border bg-background p-3">
              <span class="h-2 w-2 rounded-full bg-status-critical" aria-hidden="true"></span>
              <div class="flex-1 min-w-0">
                <p class="text-sm font-medium truncate">{a.ruleName ?? "Alert"}</p>
                <p class="text-xs text-muted-foreground">
                  Since {a.startedAt ? new Date(a.startedAt).toLocaleTimeString() : "—"}
                </p>
              </div>
              {#if a.acknowledgedAt}
                <Badge variant="secondary">Acknowledged</Badge>
              {/if}
            </div>
          {/each}
        </CardContent>
      </Card>
    {/if}

    <!-- Rules table -->
    <Card>
      <CardHeader>
        <CardTitle>Alert rules</CardTitle>
      </CardHeader>
      <CardContent>
        {#if rules.length === 0}
          <div class="rounded-md border border-dashed py-10 text-center text-muted-foreground">
            <Bell class="mx-auto h-8 w-8 opacity-50" />
            <p class="mt-2 text-sm font-medium">No alert rules yet</p>
            <p class="mt-1 text-xs">Add a rule so Nocturne can notify you when glucose goes out of range.</p>
            <Button class="mt-3" size="sm" onclick={newRule}>
              <Plus class="h-4 w-4 mr-2" /> New rule
            </Button>
          </div>
        {:else}
          <div class="space-y-2">
            {#each rules as rule (rule.id)}
              <AlertRuleRow
                {rule}
                isToggling={togglingRuleId === rule.id}
                isDeleting={deletingRuleId === rule.id}
                isTesting={testingRuleId === rule.id}
                onToggleEnabled={() => handleToggleRule(rule.id ?? "")}
                onEdit={() => editRule(rule)}
                onDelete={() => handleDeleteRule(rule.id ?? "")}
                onTestFire={() => handleTestFire(rule.id ?? "")}
                resolveAlertName={(id) => ruleNamesById.get(id)}
              />
            {/each}
          </div>
        {/if}
      </CardContent>
    </Card>
  {/if}
</div>
