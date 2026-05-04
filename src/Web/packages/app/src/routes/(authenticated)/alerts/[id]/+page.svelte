<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { onMount } from "svelte";
  import {
    getRule,
    getRules,
    createRule,
    updateRule,
    deleteRule,
    testFire,
  } from "$api/generated/alertRules.generated.remote";
  import { getAlertHistory } from "$api/generated/alerts.generated.remote";
  import { AlertRuleSeverity, AlertConditionType } from "$api-clients";
  import type {
    AlertRuleResponse,
    HistoryExcursionResponse,
  } from "$api-clients";

  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Skeleton } from "$lib/components/ui/skeleton";
  import {
    ArrowLeft,
    Save,
    Trash2,
    Zap,
    Loader2,
    History as HistoryIcon,
    PlayCircle,
  } from "lucide-svelte";

  import RuleBuilder from "$lib/components/alerts/RuleBuilder.svelte";
  import AutoResolveSection from "$lib/components/alerts/AutoResolveSection.svelte";
  import ChannelsSection from "$lib/components/alerts/ChannelsSection.svelte";
  import ReplayPanel from "$lib/components/alerts/ReplayPanel.svelte";
  import { severityLabel } from "$lib/components/alerts/severity";
  import {
    parseRule,
    flattenSingleChildRoot,
    nodeToApi,
    stripEditorFields,
    ensureCompositeRoot,
    defaultPayload,
    type RuleEditorState,
  } from "$lib/components/alerts/types";

  // ---- Page state ------------------------------------------------------
  // The dynamic [id] segment is "new" when creating, otherwise a UUID.
  let ruleId = $derived(page.params.id);
  let isNew = $derived(ruleId === "new");

  let loading = $state(true);
  let saving = $state(false);
  let deleting = $state(false);
  let testingSaved = $state(false);
  let error = $state<string | null>(null);

  let state = $state<RuleEditorState>(parseRule(null));
  let availableRules = $state<{ id: string; name: string }[]>([]);

  // Per-rule excursion history surfaced in the right rail. Loaded after the
  // rule itself so the rail doesn't block the editor render.
  let history = $state<HistoryExcursionResponse[]>([]);
  let historyLoading = $state(false);

  // Replay dialog state — opened either by the "Test alert" button (no preset)
  // or by clicking a historic firing (preset to that day).
  let replayOpen = $state(false);
  let replayInitialDate = $state<string | undefined>(undefined);

  // Smart-snooze controls — driven by the snooze sub-tree on clientConfig.
  let smartSnoozeOn = $derived(state.clientConfig.snooze.smartSnooze);
  let smartSnoozeMinutes = $derived(
    state.clientConfig.snooze.smartSnoozeExtendMinutes
  );

  onMount(async () => {
    try {
      // Fetch all rules in parallel: one for the rule under edit, one for the
      // sibling list used by the alert_state condition picker.
      const [siblings, rule] = await Promise.all([
        getRules(),
        isNew
          ? Promise.resolve<AlertRuleResponse | null>(null)
          : getRule(ruleId),
      ]);
      availableRules = (siblings ?? [])
        .filter((r) => r.id !== ruleId)
        .map((r) => ({ id: r.id ?? "", name: r.name ?? "(unnamed)" }));
      state = parseRule(rule);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }

    if (!isNew) {
      historyLoading = true;
      try {
        const r = await getAlertHistory({
          page: 1,
          pageSize: 25,
          alertRuleId: ruleId,
        });
        history = r?.items ?? [];
      } catch {
        history = [];
      } finally {
        historyLoading = false;
      }
    }
  });

  // ---- Save ------------------------------------------------------------

  async function save(): Promise<void> {
    saving = true;
    error = null;
    try {
      // Reverse parseRule's wrap so single-leaf rules round-trip flat.
      const flat = flattenSingleChildRoot(state.condition!);
      const api = nodeToApi(flat);
      // Strip the editor-only `_uid` before sending — it's a stable-key
      // helper for {#each}, not part of the API contract.
      const channelsBody = state.channels.map((c) => ({
        channelType: c.channelType,
        destination: c.destination || undefined,
        destinationLabel: c.destinationLabel || undefined,
      }));
      const body = {
        name: state.name,
        description: state.description || undefined,
        conditionType: api?.conditionType as AlertConditionType,
        conditionParams: api?.conditionParams,
        isEnabled: state.isEnabled,
        sortOrder: state.sortOrder,
        severity: state.severity,
        allowThroughDnd: state.allowThroughDnd,
        autoResolveEnabled: state.autoResolveEnabled,
        // Auto-resolve persists as a full ConditionNode envelope. Flatten the
        // editor's single-child AND wrapper before serialising so the wire
        // shape doesn't grow a redundant composite for plain single-leaf
        // resolves.
        autoResolveParams: state.autoResolveCondition
          ? stripEditorFields(
              flattenSingleChildRoot(state.autoResolveCondition)
            )
          : undefined,
        // Snooze conditions are wrapped in single-child AND groups during edit
        // (the inline rule builder requires a composite root). Flatten + strip
        // editor uids before serialising.
        clientConfiguration: {
          ...state.clientConfig,
          snooze: {
            ...state.clientConfig.snooze,
            conditions: state.clientConfig.snooze.conditions.map((c) =>
              stripEditorFields(flattenSingleChildRoot(c))
            ),
          },
        },
        channels: channelsBody,
      };
      if (isNew) {
        const created = await createRule(body);
        await goto(`/alerts/${created?.id ?? ""}`);
      } else {
        await updateRule({ id: ruleId, request: body });
      }
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      saving = false;
    }
  }

  async function destroy(): Promise<void> {
    if (isNew) return;
    if (!confirm(`Delete "${state.name}"? This cannot be undone.`)) return;
    deleting = true;
    error = null;
    try {
      await deleteRule(ruleId);
      await goto("/alerts");
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      deleting = false;
    }
  }

  // ---- Test fire -------------------------------------------------------

  async function fireSaved(): Promise<void> {
    testingSaved = true;
    error = null;
    try {
      await testFire(ruleId);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      testingSaved = false;
    }
  }

  function openReplay(initialDate?: string | Date | undefined): void {
    if (initialDate instanceof Date) {
      replayInitialDate = ymd(initialDate);
    } else if (typeof initialDate === "string") {
      replayInitialDate = initialDate.slice(0, 10);
    } else {
      replayInitialDate = undefined;
    }
    replayOpen = true;
  }

  function ymd(d: Date): string {
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  }

  function formatHistoryRow(at: Date | string | undefined): string {
    if (!at) return "—";
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return "—";
    return d.toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "numeric",
      minute: "2-digit",
    });
  }

  // ---- Severity ---------------------------------------------------------

  const severityOptions = [
    { value: AlertRuleSeverity.Info, label: "Info" },
    { value: AlertRuleSeverity.Warning, label: "Warning" },
    { value: AlertRuleSeverity.Critical, label: "Critical" },
  ];

  // ---- Smart snooze -----------------------------------------------------

  /**
   * Snapshot the editor state into the dry-run rule shape. Re-evaluated each
   * time Run is pressed so unsaved edits between presses are picked up.
   */
  function buildReplayRule() {
    const flat = flattenSingleChildRoot(state.condition!);
    const api = nodeToApi(flat);
    const params = api?.conditionParams;
    const autoResolve = state.autoResolveCondition
      ? stripEditorFields(flattenSingleChildRoot(state.autoResolveCondition))
      : undefined;
    return {
      id: isNew ? undefined : ruleId,
      name: state.name,
      conditionType: api?.conditionType as AlertConditionType,
      conditionParams: params == null ? undefined : JSON.stringify(params),
      severity: state.severity,
      allowThroughDnd: state.allowThroughDnd,
      autoResolveEnabled: state.autoResolveEnabled,
      autoResolveParams: autoResolve ? JSON.stringify(autoResolve) : undefined,
    };
  }

  function toggleSmartSnooze(checked: boolean): void {
    state.clientConfig.snooze.smartSnooze = checked;
    if (checked && state.clientConfig.snooze.conditions.length === 0) {
      state.clientConfig.snooze.conditions = [
        ensureCompositeRoot(defaultPayload("trend")),
      ];
    }
  }
</script>

<svelte:head>
  <title>{isNew ? "New alert" : state.name || "Alert"} · Nocturne</title>
</svelte:head>

<div class="container mx-auto p-4 lg:p-6 max-w-7xl">
  <!-- Header -->
  <div class="mb-6 flex items-center justify-between gap-4">
    <div class="flex items-center gap-2 min-w-0">
      <Button
        type="button"
        variant="ghost"
        size="icon"
        onclick={() => goto("/alerts")}
        aria-label="Back to alerts"
      >
        <ArrowLeft class="h-4 w-4" />
      </Button>
      <div class="min-w-0">
        <h1 class="text-2xl font-bold truncate">
          {isNew ? "New alert" : state.name || "Alert"}
        </h1>
        <p class="text-sm text-muted-foreground">
          {isNew ? "Define a new alert rule" : "Edit alert rule"}
        </p>
      </div>
    </div>
    <div class="flex items-center gap-2 shrink-0">
      {#if !isNew}
        <Button
          type="button"
          variant="outline"
          size="sm"
          onclick={destroy}
          disabled={deleting}
        >
          {#if deleting}
            <Loader2 class="h-4 w-4 mr-2 animate-spin" />
          {:else}
            <Trash2 class="h-4 w-4 mr-2" />
          {/if}
          Delete
        </Button>
      {/if}
      <Button type="button" onclick={save} disabled={saving || loading}>
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {:else}
          <Save class="h-4 w-4 mr-2" />
        {/if}
        {isNew ? "Create" : "Save"}
      </Button>
    </div>
  </div>

  {#if error}
    <div
      class="mb-4 rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
    >
      {error}
    </div>
  {/if}

  <div class="grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px] lg:items-start">
    <!-- Main editor column -->
    <div class="space-y-6">
      {#if loading}
        <Card>
          <CardHeader>
            <Skeleton class="h-5 w-40" />
          </CardHeader>
          <CardContent class="space-y-3">
            <Skeleton class="h-9 w-full" />
            <Skeleton class="h-20 w-full" />
          </CardContent>
        </Card>
      {:else}
        <!-- Identity -->
        <Card>
          <CardHeader class="flex flex-row items-start justify-between gap-4">
            <div class="space-y-1.5">
              <CardTitle>Identity</CardTitle>
              <CardDescription>
                What should this alert be called?
              </CardDescription>
            </div>
            <div class="flex items-center gap-2 shrink-0">
              <Label class="cursor-pointer text-sm" for="rule-enabled">
                Enabled
              </Label>
              <Switch
                id="rule-enabled"
                checked={state.isEnabled}
                onCheckedChange={(c) => {
                  state.isEnabled = c;
                }}
              />
            </div>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="space-y-2">
              <Label for="rule-name">Name</Label>
              <Input
                id="rule-name"
                type="text"
                placeholder="Approaching low"
                value={state.name}
                oninput={(e) => {
                  state.name = e.currentTarget.value;
                }}
              />
            </div>
            <div class="space-y-2">
              <Label for="rule-desc">Description (optional)</Label>
              <Textarea
                id="rule-desc"
                rows={2}
                placeholder="Why this alert exists, what it should trigger"
                value={state.description}
                oninput={(e) => {
                  state.description = e.currentTarget.value;
                }}
              />
            </div>
            <div class="space-y-2">
              <Label>Severity</Label>
              <Select.Root
                type="single"
                value={state.severity}
                onValueChange={(v) => {
                  state.severity = v as AlertRuleSeverity;
                }}
              >
                <Select.Trigger>{severityLabel(state.severity)}</Select.Trigger>
                <Select.Content>
                  {#each severityOptions as o (o.value)}
                    <Select.Item value={o.value} label={o.label} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
            <div class="flex items-start gap-2 rounded border bg-muted/30 p-3">
              <Checkbox
                id="rule-allow-dnd"
                checked={state.allowThroughDnd}
                onCheckedChange={(c) => {
                  state.allowThroughDnd = c === true;
                }}
              />
              <div class="space-y-0.5">
                <Label class="cursor-pointer text-sm" for="rule-allow-dnd">
                  Allow through Do Not Disturb
                </Label>
                <p class="text-xs text-muted-foreground">
                  Critical-severity rules implicitly bypass DND regardless of
                  this flag.
                </p>
              </div>
            </div>
          </CardContent>
        </Card>

        <!-- Condition tree -->
        <Card>
          <CardHeader>
            <CardTitle>Condition</CardTitle>
            <CardDescription>
              Define when this alert fires. Mix facts with AND/OR; nest with
              brackets.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if state.condition}
              <RuleBuilder bind:node={state.condition} {availableRules} />
            {/if}
          </CardContent>
        </Card>

        <!-- Channels -->
        <Card>
          <CardHeader>
            <CardTitle>Channels</CardTitle>
            <CardDescription>
              Where to deliver the alert. All channels fire in parallel.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <ChannelsSection bind:channels={state.channels} />
          </CardContent>
        </Card>

        <!-- Auto-resolve -->
        <Card>
          <CardHeader>
            <CardTitle>Auto-resolve</CardTitle>
          </CardHeader>
          <CardContent>
            <AutoResolveSection
              bind:enabled={state.autoResolveEnabled}
              bind:condition={state.autoResolveCondition}
              firingCondition={state.condition}
              {availableRules}
            />
          </CardContent>
        </Card>

        <!-- Smart snooze -->
        <Card>
          <CardHeader>
            <CardTitle>Smart snooze</CardTitle>
            <CardDescription>
              When the user snoozes, extend the snooze automatically while these
              conditions hold.
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-4">
            <div class="flex items-center justify-between gap-2">
              <Label class="cursor-pointer" for="smart-snooze">
                Enable smart snooze
              </Label>
              <Switch
                id="smart-snooze"
                checked={smartSnoozeOn}
                onCheckedChange={toggleSmartSnooze}
              />
            </div>
            {#if smartSnoozeOn}
              <div class="space-y-2">
                <Label for="smart-snooze-min">Extend by (minutes)</Label>
                <Input
                  id="smart-snooze-min"
                  type="number"
                  min="1"
                  class="max-w-32"
                  value={smartSnoozeMinutes}
                  oninput={(e) => {
                    const n = Number(e.currentTarget.value);
                    if (Number.isFinite(n))
                      state.clientConfig.snooze.smartSnoozeExtendMinutes = n;
                  }}
                />
              </div>
              <div class="space-y-2">
                <Label>Extend while</Label>
                {#each state.clientConfig.snooze.conditions as _c, i (i)}
                  <RuleBuilder
                    bind:node={state.clientConfig.snooze.conditions[i]}
                    {availableRules}
                  />
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      {/if}
    </div>

    <!-- Right rail: test alert + historic firings -->
    <aside class="lg:sticky lg:top-6 self-start space-y-4">
      <Card>
        <CardHeader>
          <CardTitle class="text-base">Test alert</CardTitle>
          <CardDescription class="text-xs">
            Fire a real notification, or replay the rule against historical
            glucose.
          </CardDescription>
        </CardHeader>
        <CardContent class="space-y-2">
          {#if !isNew}
            <Button
              type="button"
              variant="outline"
              class="w-full justify-start"
              onclick={fireSaved}
              disabled={testingSaved || loading}
            >
              {#if testingSaved}
                <Loader2 class="h-4 w-4 mr-2 animate-spin" />
              {:else}
                <Zap class="h-4 w-4 mr-2" />
              {/if}
              Fire saved rule
            </Button>
          {/if}
          <Button
            type="button"
            variant="outline"
            class="w-full justify-start"
            onclick={() => openReplay()}
            disabled={loading}
          >
            <PlayCircle class="h-4 w-4 mr-2" />
            Replay against history
          </Button>
        </CardContent>
      </Card>

      {#if !isNew}
        <Card>
          <CardHeader>
            <CardTitle class="text-base flex items-center gap-2">
              <HistoryIcon class="h-4 w-4" /> Historic firings
            </CardTitle>
            <CardDescription class="text-xs">
              Real fires for this rule. Click any to replay the day in the
              simulator.
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-1.5">
            {#if historyLoading}
              <div
                class="flex items-center justify-center py-4 text-muted-foreground"
              >
                <Loader2 class="h-4 w-4 animate-spin" />
              </div>
            {:else if history.length === 0}
              <div
                class="rounded-md border border-dashed py-4 text-center text-xs text-muted-foreground"
              >
                No firings yet.
              </div>
            {:else}
              <div class="max-h-72 overflow-y-auto space-y-1">
                {#each history as h (h.id)}
                  <button
                    type="button"
                    class="flex w-full items-center gap-2 rounded-md border bg-background px-2 py-1.5 text-left text-xs hover:bg-muted"
                    onclick={() => openReplay(h.startedAt)}
                  >
                    <span
                      class="h-1.5 w-1.5 shrink-0 rounded-full bg-amber-500"
                      aria-hidden="true"
                    ></span>
                    <span class="flex-1 min-w-0 truncate tabular-nums">
                      {formatHistoryRow(h.startedAt)}
                    </span>
                    {#if h.acknowledgedAt}
                      <span class="text-[10px] text-muted-foreground shrink-0">
                        ack
                      </span>
                    {/if}
                  </button>
                {/each}
              </div>
            {/if}
          </CardContent>
        </Card>
      {/if}
    </aside>
  </div>
</div>

<Dialog.Root bind:open={replayOpen}>
  <Dialog.Content class="flex max-w-6xl w-6xl flex-col sm:max-w-[95vw]">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <PlayCircle class="h-4 w-4" /> Replay
      </Dialog.Title>
      <Dialog.Description>
        Replay this alert (and any siblings) against historical glucose. Nothing
        is delivered.
      </Dialog.Description>
    </Dialog.Header>

    <div class="flex-1 min-h-0 overflow-hidden py-2">
      <ReplayPanel
        initialCustomDate={replayInitialDate}
        rule={buildReplayRule}
        editingRuleId={isNew ? undefined : ruleId}
        editingTree={state.condition ?? undefined}
      />
    </div>
  </Dialog.Content>
</Dialog.Root>
