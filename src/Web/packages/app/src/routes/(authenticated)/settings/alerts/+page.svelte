<script lang="ts">
  import { onMount } from "svelte";
  import {
    getRules,
    deleteRule,
    toggleRule,
  } from "$api/generated/alertRules.generated.remote";
  import {
    getActiveAlerts,
    getAlertHistory,
    acknowledge,
  } from "$api/generated/alerts.generated.remote";
  import type {
    AlertRuleResponse,
    ActiveExcursionResponse,
    AlertHistoryResponse,
  } from "$api-clients";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import SettingsPageSkeleton from "$lib/components/settings/SettingsPageSkeleton.svelte";
  import {
    Bell,
    Plus,
    AlertTriangle,
    Check,
    Loader2,
    Zap,
  } from "lucide-svelte";
  import { goto } from "$app/navigation";
  import { coachmark } from "@nocturne/coach";
  import RuleEditorSheet from "$lib/components/alerts/RuleEditorSheet.svelte";
  import AlertRuleRow from "$lib/components/alerts/AlertRuleRow.svelte";
  import AlertHistoryCard from "$lib/components/alerts/AlertHistoryCard.svelte";
  import { AlertConditionType } from "$api-clients";

  let rules = $state<AlertRuleResponse[]>([]);
  let activeAlerts = $state<ActiveExcursionResponse[]>([]);
  let history = $state<AlertHistoryResponse | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let expandedRuleId = $state<string | null>(null);
  let deletingRuleId = $state<string | null>(null);
  let togglingRuleId = $state<string | null>(null);
  let acknowledging = $state(false);
  let historyPage = $state(1);
  let historyLoading = $state(false);
  let editorOpen = $state(false);
  let editingRule = $state<AlertRuleResponse | null>(null);

  function getConditionLabel(
    conditionType: AlertConditionType | undefined
  ): string {
    switch (conditionType) {
      case AlertConditionType.Threshold:
        return "Threshold";
      case AlertConditionType.RateOfChange:
        return "Rate of Change";
      case AlertConditionType.SignalLoss:
        return "Signal Loss";
      case AlertConditionType.Composite:
        return "Composite";
      default:
        return conditionType ?? "Unknown";
    }
  }

  function formatDate(date: Date | string | undefined): string {
    if (!date) return "-";
    const d = typeof date === "string" ? new Date(date) : date;
    return d.toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  async function loadData() {
    loading = true;
    error = null;
    try {
      const [rulesResult, alertsResult, historyResult] = await Promise.all([
        getRules(),
        getActiveAlerts(),
        getAlertHistory({ page: 1, pageSize: 10 }),
      ]);
      rules = Array.isArray(rulesResult) ? rulesResult : [];
      activeAlerts = Array.isArray(alertsResult) ? alertsResult : [];
      history = historyResult ?? null;
    } catch (err) {
      error =
        err instanceof Error ? err.message : "Failed to load alert settings";
    } finally {
      loading = false;
    }
  }

  async function loadHistory(page: number) {
    historyLoading = true;
    try {
      const result = await getAlertHistory({ page, pageSize: 10 });
      history = result ?? null;
      historyPage = page;
    } catch {
      // Keep existing history on error
    } finally {
      historyLoading = false;
    }
  }

  async function handleToggleRule(ruleId: string) {
    togglingRuleId = ruleId;
    try {
      await toggleRule(ruleId);
      const result = await getRules();
      rules = Array.isArray(result) ? result : [];
    } catch {
      // Error handled by remote function
    } finally {
      togglingRuleId = null;
    }
  }

  async function handleDeleteRule(ruleId: string) {
    deletingRuleId = ruleId;
    try {
      await deleteRule(ruleId);
      const result = await getRules();
      rules = Array.isArray(result) ? result : [];
    } catch {
      // Error handled by remote function
    } finally {
      deletingRuleId = null;
    }
  }

  async function handleAcknowledge() {
    acknowledging = true;
    try {
      await acknowledge({ acknowledgedBy: "web_user" });
      const result = await getActiveAlerts();
      activeAlerts = Array.isArray(result) ? result : [];
    } catch {
      // Error handled by remote function
    } finally {
      acknowledging = false;
    }
  }

  function toggleExpand(ruleId: string) {
    expandedRuleId = expandedRuleId === ruleId ? null : ruleId;
  }

  function openCreateEditor() {
    editingRule = null;
    editorOpen = true;
  }

  function openEditEditor(rule: AlertRuleResponse) {
    editingRule = rule;
    editorOpen = true;
  }

  async function handleEditorSave() {
    const result = await getRules();
    rules = Array.isArray(result) ? result : [];
  }

  const alertsConfigured = $derived(rules.length > 0);

  onMount(() => {
    loadData();
  });
</script>

<svelte:head>
  <title>Alerts - Settings - Nocturne</title>
</svelte:head>

<div
  class="container mx-auto max-w-4xl p-6 space-y-6"
  {@attach coachmark({
    key: "onboarding.alerts",
    title: "Don't miss highs or lows",
    description:
      "Set up at least one alert rule so Nocturne can notify you when glucose goes out of range. Use the Setup Wizard for the fastest start.",
    completedWhen: () => alertsConfigured,
  })}
>
  <!-- Header -->
  <div class="flex items-start justify-between">
    <div class="flex items-center gap-3">
      <div
        class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10"
      >
        <Bell class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Alerts</h1>
        <p class="text-muted-foreground">
          Configure alert rules, schedules, and escalation chains
        </p>
      </div>
    </div>
    <div
      class="flex items-center gap-2"
      {@attach coachmark({
        key: "power-user.alert-rules",
        title: "Custom alert rules",
        description:
          "Build custom alert rules with threshold, rate-of-change, or signal-loss conditions.",
      })}
    >
      <Button
        variant="outline"
        onclick={() => goto("/settings/alerts/setup")}
        {@attach coachmark({
          key: "setup-alerts.wizard",
          title: "Quickest way to start",
          description:
            "The wizard walks you through creating your first high and low alerts.",
        })}
      >
        <Zap class="h-4 w-4 mr-2" />
        Setup Wizard
      </Button>
      <Button onclick={openCreateEditor}>
        <Plus class="h-4 w-4 mr-2" />
        Add Rule
      </Button>
    </div>
  </div>

  {#if loading}
    <SettingsPageSkeleton cardCount={4} />
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="flex items-center gap-3 pt-6">
        <AlertTriangle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load alert settings</p>
          <p class="text-sm text-muted-foreground">{error}</p>
        </div>
      </CardContent>
    </Card>
  {:else}
    <!-- Active Alerts Banner -->
    {#if activeAlerts.length > 0}
      <Card class="border-destructive/50 bg-destructive/5">
        <CardHeader class="pb-3">
          <div class="flex items-center justify-between">
            <CardTitle class="flex items-center gap-2 text-destructive">
              <AlertTriangle class="h-5 w-5" />
              Active Alerts ({activeAlerts.length})
            </CardTitle>
            <Button
              variant="outline"
              size="sm"
              onclick={handleAcknowledge}
              disabled={acknowledging}
            >
              {#if acknowledging}
                <Loader2 class="h-4 w-4 mr-2 animate-spin" />
              {:else}
                <Check class="h-4 w-4 mr-2" />
              {/if}
              Acknowledge All
            </Button>
          </div>
        </CardHeader>
        <CardContent class="space-y-2">
          {#each activeAlerts as alert (alert.id)}
            <div
              class="flex items-center gap-3 p-3 rounded-lg bg-background border"
            >
              <AlertTriangle class="h-4 w-4 text-destructive shrink-0" />
              <div class="flex-1 min-w-0">
                <p class="text-sm font-medium">
                  {alert.ruleName ?? "Alert"}
                </p>
                <p class="text-xs text-muted-foreground">
                  {getConditionLabel(alert.conditionType)} — Started {formatDate(
                    alert.startedAt
                  )}
                </p>
              </div>
              {#if alert.acknowledgedAt}
                <Badge variant="secondary">Acknowledged</Badge>
              {/if}
            </div>
          {/each}
        </CardContent>
      </Card>
    {/if}

    <!-- Alert Rules -->
    <Card>
      <CardHeader>
        <div class="flex items-center justify-between">
          <div>
            <CardTitle class="flex items-center gap-2">
              <Bell class="h-5 w-5" />
              Alert Rules
            </CardTitle>
          </div>
        </div>
      </CardHeader>
      <CardContent class="space-y-3">
        {#if rules.length === 0}
          <div class="text-center py-12 text-muted-foreground">
            <Bell class="h-12 w-12 mx-auto mb-4 opacity-50" />
            <p class="font-medium">No alert rules configured</p>
            <p class="text-sm mb-4">
              Use the setup wizard to create your first alert rules
            </p>
            <Button
              variant="outline"
              onclick={() => goto("/settings/alerts/setup")}
            >
              <Plus class="h-4 w-4 mr-2" />
              Setup Wizard
            </Button>
          </div>
        {:else}
          {#each rules as rule (rule.id)}
            <AlertRuleRow
              {rule}
              isExpanded={expandedRuleId === rule.id}
              isToggling={togglingRuleId === rule.id}
              isDeleting={deletingRuleId === rule.id}
              onToggleExpand={() => toggleExpand(rule.id ?? "")}
              onToggleEnabled={() => handleToggleRule(rule.id ?? "")}
              onEdit={() => openEditEditor(rule)}
              onDelete={() => handleDeleteRule(rule.id ?? "")}
            />
          {/each}
        {/if}
      </CardContent>
    </Card>

    <!-- Alert History -->
    <AlertHistoryCard
      {history}
      page={historyPage}
      loading={historyLoading}
      onLoadPage={loadHistory}
    />
  {/if}
</div>

<RuleEditorSheet
  bind:open={editorOpen}
  rule={editingRule}
  onSave={handleEditorSave}
/>
