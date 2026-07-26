<script lang="ts">
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
  import DndNoticeStrip from "$lib/components/alerts/DndNoticeStrip.svelte";
  import { isDndActiveNow } from "$lib/components/alerts/dnd";
  import { severity, severityLabel } from "$lib/components/alerts/severity";

  // ---- Queries ----
  const rulesQuery = getRules();
  const activeAlertsQuery = getActiveAlerts();
  const historyQuery = getAlertHistory({ page: 1, pageSize: 50 });
  const dndQuery = getTenantAlertSettings();

  // ---- Mutation state ----
  let togglingRuleId = $state<string | null>(null);
  let deletingRuleId = $state<string | null>(null);
  let testingRuleId = $state<string | null>(null);
  let acknowledging = $state(false);
  let disablingDnd = $state(false);

  // ---- Mutations ----
  async function handleToggleRule(ruleId: string): Promise<void> {
    togglingRuleId = ruleId;
    try {
      await toggleRule(ruleId);
      await rulesQuery.refresh();
    } finally {
      togglingRuleId = null;
    }
  }

  async function handleDeleteRule(ruleId: string): Promise<void> {
    deletingRuleId = ruleId;
    try {
      await deleteRule(ruleId);
      await rulesQuery.refresh();
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

  async function handleDisableDnd(
    current: TenantAlertSettingsResponse,
  ): Promise<void> {
    disablingDnd = true;
    try {
      // Clears the manual mute only; the configured quiet-hours window is left
      // in place.
      await updateTenantAlertSettings({
        dndManualActive: false,
        dndManualUntil: undefined,
        dndScheduleEnabled: current.dndScheduleEnabled,
        dndScheduleStart: current.dndScheduleStart,
        dndScheduleEnd: current.dndScheduleEnd,
      });
      await dndQuery.refresh();
    } finally {
      disablingDnd = false;
    }
  }

  async function handleAcknowledge(): Promise<void> {
    acknowledging = true;
    try {
      // Optimistically badge every unacknowledged excursion so the card updates
      // at once; the command's GetActiveAlerts invalidation reconciles it in the
      // same round-trip (same pattern as AlertBanner/FiringToast).
      await acknowledge({}).updates(
        activeAlertsQuery.withOverride((current) =>
          (current ?? []).map((a) =>
            a.acknowledgedAt ? a : { ...a, acknowledgedAt: new Date() },
          ),
        ),
      );
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
</script>

<svelte:head>
  <title>Alerts · Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-5xl p-3 @md:p-6 space-y-6">
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

  <svelte:boundary>
    {#snippet pending()}
      <SettingsPageSkeleton cardCount={3} />
    {/snippet}

    {#snippet failed(error)}
      <Card class="border-destructive">
        <CardContent class="flex items-center gap-3">
          <AlertTriangle class="h-5 w-5 text-destructive" />
          <div>
            <p class="font-medium">Failed to load alerts</p>
            <p class="text-sm text-muted-foreground">
              {error instanceof Error ? error.message : "Unknown error"}
            </p>
          </div>
        </CardContent>
      </Card>
    {/snippet}

    {@const rules = (await rulesQuery) ?? []}
    {@const activeAlerts = (await activeAlertsQuery) ?? []}
    {@const history = await historyQuery}
    {@const dnd = (await dndQuery) ?? null}
    {@const enabledCount = rules.filter((r) => r.isEnabled).length}
    {@const totalCount = rules.length}
    {@const ruleNamesById = new Map(
      rules.map((r) => [r.id ?? "", r.name ?? "(unnamed)"]),
    )}
    {@const cutoff = Date.now() - 7 * 24 * 60 * 60 * 1000}
    {@const fetchedHistory = history?.items ?? []}
    {@const firedThisWeek = fetchedHistory.filter((h) => {
      const t = h.startedAt ? new Date(h.startedAt).getTime() : NaN;
      return Number.isFinite(t) && t >= cutoff;
    }).length}
    <!-- The endpoint has no date filter, so the week is counted within one page
         of history. When every row on that page is inside the week and the
         server holds more, the real total is higher than we can see. -->
    {@const firedThisWeekIsFloor =
      firedThisWeek === fetchedHistory.length &&
      (history?.totalCount ?? 0) > fetchedHistory.length}

    <!-- Do Not Disturb notice, shown only while a manual mute is in effect. -->
    {#if dnd && isDndActiveNow(dnd)}
      <DndNoticeStrip
        onDisableDnd={() => handleDisableDnd(dnd)}
        {disablingDnd}
      />
    {/if}

    <!-- Stat row -->
    <div class="grid gap-3 @md:grid-cols-3">
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
            <p class="mt-1 text-2xl font-bold tabular-nums">
              {firedThisWeek}{firedThisWeekIsFloor ? "+" : ""}
            </p>
          </CardContent>
        </Card>
      </a>
    </div>

    <!-- Active alerts banner (kept as a persistent surface separate from the
         FiringToast which handles fresh-fire moments). -->
    {#if activeAlerts.length > 0}
      <Card class="border-destructive/40 bg-destructive/5">
        <CardHeader>
          <div class="flex flex-col gap-2 @sm:flex-row @sm:items-center @sm:justify-between">
            <CardTitle class="flex min-w-0 items-center gap-2 text-destructive">
              <AlertTriangle class="h-5 w-5 shrink-0" />
              <span class="truncate">Active alerts ({activeAlerts.length})</span>
            </CardTitle>
            <Button
              class="@sm:shrink-0"
              variant="outline"
              size="sm"
              onclick={handleAcknowledge}
              disabled={acknowledging || activeAlerts.every((a) => a.acknowledgedAt)}
            >
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
              <span
                class="h-2 w-2 shrink-0 rounded-full {severity(a.severity, 'dot')}"
                aria-hidden="true"
              ></span>
              <div class="flex-1 min-w-0">
                <p class="text-sm font-medium truncate">
                  <span class="text-muted-foreground text-xs uppercase tracking-wider">
                    {severityLabel(a.severity)}
                  </span>
                  {a.ruleName ?? "Alert"}
                </p>
                <p class="text-xs text-muted-foreground">
                  Since {a.startedAt ? new Date(a.startedAt).toLocaleTimeString() : "—"}
                </p>
              </div>
              {#if a.acknowledgedAt}
                <Badge variant="secondary" class="shrink-0">Acknowledged</Badge>
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
  </svelte:boundary>
</div>
