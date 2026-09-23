<script lang="ts">
  import { formatDayTime } from "$lib/utils/formatting";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { untrack } from "svelte";
  import {
    getRule,
    getRules,
    createRule,
    updateRule,
    deleteRule,
    testFire,
  } from "$api/generated/alertRules.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { getAlertHistory } from "$api/generated/alerts.generated.remote";
  import { z } from "zod";
  import { AlertRuleSeverity, AlertConditionType } from "$api-clients";
  import type { HistoryExcursionResponse } from "$api-clients";

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
    CalendarDays,
  } from "lucide-svelte";

  import { EditorActionBar } from "$lib/components/layout";
  import RuleBuilder from "$lib/components/alerts/RuleBuilder.svelte";
  import AutoResolveSection from "$lib/components/alerts/AutoResolveSection.svelte";
  import ChannelsSection from "$lib/components/alerts/ChannelsSection.svelte";
  import ReplayPanel from "$lib/components/alerts/ReplayPanel.svelte";
  import { severity, severityLabel } from "$lib/components/alerts/severity";
  import {
    parseRule,
    flattenSingleChildRoot,
    nodeToApi,
    stripEditorFields,
    ensureCompositeRoot,
    defaultPayload,
    buildBody,
    validateChannels,
    type RuleEditorState,
  } from "$lib/components/alerts/types";

  // ---- Page state ------------------------------------------------------
  // The dynamic [id] segment is "new" when creating, otherwise a UUID.
  let ruleId = $derived(page.params.id ?? "");
  let isNew = $derived(ruleId === "new");

  let saving = $state(false);
  let deleting = $state(false);
  let testingSaved = $state(false);
  let error = $state<string | null>(null);

  let editor = $state<RuleEditorState>(parseRule(null));
  let seededId = $state<string | null>(null);
  let savedBody = $state<ReturnType<typeof buildBody> | null>(null);
  const isDirty = $derived(
    isNew || savedBody === null || JSON.stringify(buildBody(editor)) !== JSON.stringify(savedBody)
  );

  // Queries — fire on the server during SSR, results land in cache for hydration.
  const rulesQuery = getRules();
  const ruleQuery = $derived(isNew ? null : getRule(ruleId));
  const historyQuery = $derived(
    isNew ? null : getAlertHistory({ page: 1, pageSize: 25, alertRuleId: ruleId }),
  );

  const availableRules = $derived<{ id: string; name: string }[]>(
    (rulesQuery.current ?? [])
      .filter((r) => r.id !== ruleId)
      .map((r) => ({ id: r.id ?? "", name: r.name ?? "(unnamed)" })),
  );
  const history = $derived<HistoryExcursionResponse[]>(
    historyQuery?.current?.items ?? [],
  );
  const historyLoading = $derived(
    historyQuery !== null && historyQuery.current === undefined,
  );
  const loading = $derived(
    rulesQuery.current === undefined ||
      (ruleQuery !== null && ruleQuery.current === undefined),
  );

  // Replay dialog state — opened either by the "Test alert" button (no preset)
  // or by clicking a historic firing (preset to that day).
  let replayOpen = $state(false);
  let replayInitialDate = $state<string | undefined>(undefined);

  // Smart-snooze controls — driven by the snooze sub-tree on clientConfig.
  let smartSnoozeOn = $derived(editor.clientConfig.snooze.smartSnooze);
  let smartSnoozeMinutes = $derived(
    editor.clientConfig.snooze.smartSnoozeExtendMinutes
  );

  // Seed the editor state from the loaded rule once per ruleId. Rebuilds when
  // the route param changes (e.g. navigating from /alerts/foo to /alerts/bar).
  $effect(() => {
    if (seededId === ruleId) return;
    if (isNew) {
      untrack(() => {
        editor = parseRule(null);
        savedBody = null;
        seededId = ruleId;
      });
      return;
    }
    const rule = ruleQuery?.current;
    if (rule === undefined) return;
    untrack(() => {
      editor = parseRule(rule ?? null);
      savedBody = buildBody(editor);
      seededId = ruleId;
    });
  });

  // ---- Save ------------------------------------------------------------

  async function save(): Promise<void> {
    const channelError = validateChannels(editor.channels);
    if (channelError) {
      error = channelError;
      return;
    }
    saving = true;
    error = null;
    try {
      const body = buildBody(editor);
      if (isNew) {
        const created = await createRule(body);
        await goto(
          created?.id
            ? resolve("/(authenticated)/alerts/[id]", { id: created.id })
            : resolve("/alerts")
        );
      } else {
        await updateRule({ id: ruleId, request: body });
        savedBody = buildBody(editor);
      }
    } catch (e) {
      error = describeSubmitError(e, "Failed to save the alert rule. Please try again.");
    } finally {
      saving = false;
    }
  }

  async function destroy(): Promise<void> {
    if (isNew) return;
    if (!confirm(`Delete "${editor.name}"? This cannot be undone.`)) return;
    deleting = true;
    error = null;
    try {
      await deleteRule(ruleId);
      await goto(resolve("/alerts"));
    } catch (e) {
      error = describeSubmitError(e, "Failed to delete the alert rule. Please try again.");
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
      error = describeSubmitError(e, "Failed to send a test alert. Please try again.");
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

  // Link to the Day in Review report for the calendar day a firing occurred.
  function dayInReviewHref(at: Date | string | undefined): string | undefined {
    if (!at) return undefined;
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return undefined;
    return `/reports/day-in-review?date=${ymd(d)}`;
  }

  function formatHistoryRow(at: Date | string | undefined): string {
    if (!at) return "—";
    const d = at instanceof Date ? at : new Date(at);
    if (Number.isNaN(d.getTime())) return "—";
    return formatDayTime(d);
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
  const conditionTypeSchema = z.enum(AlertConditionType);
  const severitySchema = z.enum(AlertRuleSeverity);

  function buildReplayRule() {
    const flat = flattenSingleChildRoot(editor.condition!);
    const api = nodeToApi(flat);
    const params = api?.conditionParams;
    const autoResolve = editor.autoResolveCondition
      ? stripEditorFields(flattenSingleChildRoot(editor.autoResolveCondition))
      : undefined;
    return {
      id: isNew ? undefined : ruleId,
      name: editor.name,
      conditionType: conditionTypeSchema.safeParse(api?.conditionType).data,
      conditionParams: params == null ? undefined : JSON.stringify(params),
      severity: editor.severity,
      allowThroughDnd: editor.allowThroughDnd,
      autoResolveEnabled: editor.autoResolveEnabled,
      autoResolveParams: autoResolve ? JSON.stringify(autoResolve) : undefined,
    };
  }

  function toggleSmartSnooze(checked: boolean): void {
    editor.clientConfig.snooze.smartSnooze = checked;
    if (checked && editor.clientConfig.snooze.conditions.length === 0) {
      editor.clientConfig.snooze.conditions = [
        ensureCompositeRoot(defaultPayload("trend")),
      ];
    }
  }
</script>

<svelte:head>
  <title>{isNew ? "New alert" : editor.name || "Alert"} · Nocturne</title>
</svelte:head>

<div class="@container container mx-auto p-3 @md:p-6 max-w-7xl max-md:pb-24">
  <!-- Header -->
  <EditorActionBar>
    {#snippet leading()}
      <Button
        type="button"
        variant="ghost"
        size="icon"
        onclick={() => goto(resolve("/alerts"))}
        aria-label="Back to alerts"
      >
        <ArrowLeft class="h-4 w-4" />
      </Button>
      <div class="min-w-0">
        <h1 class="text-2xl font-bold truncate">
          {isNew ? "New alert" : editor.name || "Alert"}
        </h1>
        <p class="text-sm text-muted-foreground">
          {isNew ? "Define a new alert rule" : "Edit alert rule"}
        </p>
      </div>
    {/snippet}
    {#snippet actions()}
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
      <Button type="button" onclick={save} disabled={saving || loading || !isDirty}>
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {:else}
          <Save class="h-4 w-4 mr-2" />
        {/if}
        {isNew ? "Create" : "Save"}
      </Button>
    {/snippet}
  </EditorActionBar>

  {#if error}
    <div
      class="mb-4 rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
    >
      {error}
    </div>
  {/if}

  <div class="grid grid-cols-1 gap-6 @3xl:grid-cols-[minmax(0,1fr)_320px] @3xl:items-start">
    <!-- Main editor column -->
    <div class="min-w-0 space-y-6">
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
              <Label class="cursor-pointer" for="rule-enabled">
                Enabled
              </Label>
              <Switch
                id="rule-enabled"
                checked={editor.isEnabled}
                onCheckedChange={(c: boolean) => {
                  editor.isEnabled = c;
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
                value={editor.name}
                oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                  editor.name = e.currentTarget.value;
                }}
              />
            </div>
            <div class="space-y-2">
              <Label for="rule-desc">Description (optional)</Label>
              <Textarea
                id="rule-desc"
                rows={2}
                placeholder="Why this alert exists, what it should trigger"
                value={editor.description}
                oninput={(e: Event & { currentTarget: HTMLTextAreaElement }) => {
                  editor.description = e.currentTarget.value;
                }}
              />
            </div>
            <div class="space-y-2">
              <Label>Severity</Label>
              <Select.Root
                type="single"
                value={editor.severity}
                onValueChange={(v) => {
                  const parsed = severitySchema.safeParse(v);
                  if (parsed.success) editor.severity = parsed.data;
                }}
              >
                <Select.Trigger>{severityLabel(editor.severity)}</Select.Trigger>
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
                checked={editor.allowThroughDnd}
                onCheckedChange={(c: boolean) => {
                  editor.allowThroughDnd = c === true;
                }}
              />
              <div class="space-y-0.5">
                <Label class="cursor-pointer" for="rule-allow-dnd">
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
            {#if editor.condition}
              <RuleBuilder bind:node={editor.condition} {availableRules} />
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
            <ChannelsSection
              bind:channels={editor.channels}
              severity={editor.severity}
            />
          </CardContent>
        </Card>

        <!-- Auto-resolve -->
        <Card>
          <CardHeader>
            <CardTitle>Auto-resolve</CardTitle>
          </CardHeader>
          <CardContent>
            <AutoResolveSection
              bind:enabled={editor.autoResolveEnabled}
              bind:condition={editor.autoResolveCondition}
              firingCondition={editor.condition}
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
                  oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
                    const n = Number(e.currentTarget.value);
                    if (Number.isFinite(n))
                      editor.clientConfig.snooze.smartSnoozeExtendMinutes = n;
                  }}
                />
              </div>
              <div class="space-y-2">
                <Label>Extend while</Label>
                {#each editor.clientConfig.snooze.conditions as _c, i (i)}
                  <RuleBuilder
                    bind:node={editor.clientConfig.snooze.conditions[i]}
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
    <aside class="min-w-0 lg:sticky lg:top-6 self-start space-y-4">
      <Card>
        <CardHeader>
          <CardTitle class="text-base">Test alert</CardTitle>
          <CardDescription size="sm">
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
            <CardDescription size="sm">
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
                  <div
                    class="flex items-center gap-1 rounded-md border bg-background pr-1 hover:bg-muted"
                  >
                    <Button
                      variant="ghost"
                      size="xs"
                      class="flex min-w-0 flex-1 items-center text-left"
                      onclick={() => openReplay(h.startedAt)}
                      title="Replay this day in the simulator"
                    >
                      <span
                        class="h-1.5 w-1.5 shrink-0 rounded-full {severity(
                          h.severity,
                          'dot'
                        )}"
                        aria-hidden="true"
                      ></span>
                      <span class="min-w-0 flex-1 truncate tabular-nums">
                        {formatHistoryRow(h.startedAt)}
                      </span>
                      {#if h.acknowledgedAt}
                        <span class="text-2xs text-muted-foreground shrink-0">
                          ack
                        </span>
                      {/if}
                    </Button>
                    {#if dayInReviewHref(h.startedAt)}
                      <Button
                        variant="ghost-muted"
                        size="icon-xs"
                        class="shrink-0"
                        href={dayInReviewHref(h.startedAt)}
                        title="Open day in review"
                        aria-label="Open day in review"
                      >
                        <CalendarDays class="h-3.5 w-3.5" />
                      </Button>
                    {/if}
                  </div>
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
  <Dialog.Content
    class="flex h-[90vh] max-h-[90vh] w-[calc(100vw-1rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0 sm:w-[95vw] sm:max-w-7xl"
  >
    <Dialog.Header class="border-b px-4 py-3">
      <Dialog.Title class="flex items-center gap-2">
        <PlayCircle class="h-4 w-4" /> Replay
      </Dialog.Title>
      <Dialog.Description>
        Replay this alert (and any siblings) against historical glucose. Nothing
        is delivered.
      </Dialog.Description>
    </Dialog.Header>

    <div class="@container min-h-0 flex-1 overflow-hidden p-3 @md:p-4">
      <ReplayPanel
        initialCustomDate={replayInitialDate}
        rule={buildReplayRule}
        editingRuleId={isNew ? undefined : ruleId}
        editingTree={editor.condition ?? undefined}
        availableRules={rulesQuery.current ?? []}
      />
    </div>
  </Dialog.Content>
</Dialog.Root>
