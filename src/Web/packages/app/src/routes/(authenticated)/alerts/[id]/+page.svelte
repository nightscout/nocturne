<script lang="ts">
  import { formatDayTime } from "#lib/utils/formatting.js";
  import { page } from "$app/state";
  import { beforeNavigate, goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { untrack } from "svelte";
  import {
    getRule,
    getRules,
    getSettingOptions,
    updateRule,
    deleteRule,
    testFire,
  } from "#lib/api/generated/alertRules.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { toast } from "svelte-sonner";
  import { conditionIssuesMessage } from "#lib/components/alerts/conditionIssues.svelte.js";
  import { holdFactLines, holdSinceText } from "#lib/components/alerts/holdText.svelte.js";
  import Hourglass from "@lucide/svelte/icons/hourglass";
  import { getAlertHistory } from "#lib/api/generated/alerts.generated.remote.js";
  import { z } from "zod";
  import { AlertRuleSeverity } from "@nocturne/api-client";
  import type { HistoryExcursionResponse } from "@nocturne/api-client";

  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
    CardDescription,
  } from "#lib/components/ui/card/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Save from "@lucide/svelte/icons/save";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Send from "@lucide/svelte/icons/send";
  import Check from "@lucide/svelte/icons/check";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import HistoryIcon from "@lucide/svelte/icons/history";
  import PlayCircle from "@lucide/svelte/icons/circle-play";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";

  import { EditorActionBar } from "#lib/components/layout/index.js";
  import { EmptyState } from "#lib/components/shared/index.js";
  import RuleBuilder from "#lib/components/alerts/RuleBuilder.svelte";
  import AutoResolveSection from "#lib/components/alerts/AutoResolveSection.svelte";
  import ChannelsSection from "#lib/components/alerts/ChannelsSection.svelte";
  import ChannelsNotSent from "#lib/components/alerts/ChannelsNotSent.svelte";
  import { channelNotSentMessage } from "#lib/components/alerts/channelState.svelte.js";
  import AdvancedSection from "#lib/components/alerts/AdvancedSection.svelte";
  import ReplayPanel from "#lib/components/alerts/ReplayPanel.svelte";
  import HistoryCheck from "#lib/components/alerts/HistoryCheck.svelte";
  import OpenOnlyWarning from "#lib/components/alerts/OpenOnlyWarning.svelte";
  import { severity } from "#lib/components/alerts/severity.js";
  import { endsLowCover, lastLowCoverMessage } from "#lib/components/alerts/lastLowCover.js";
  import {
    severityDescription,
    severityLabel,
  } from "#lib/components/alerts/severityLabel.svelte.js";
  import {
    parseRule,
    buildBody,
    freshnessIssue,
    markFreshnessSaved,
    toReplayRule,
    validateChannels,
    type RuleEditorState,
  } from "#lib/components/alerts/types.js";

  // ---- Page state ------------------------------------------------------
  let ruleId = $derived(page.params.id ?? "");

  let saving = $state(false);
  let deleting = $state(false);
  let testingSaved = $state(false);
  let testSent = $state(false);
  let testNotSent = $state<string[]>([]);
  let error = $state<string | null>(null);
  let confirmingDisable = $state(false);
  let confirmingDelete = $state(false);

  let editor = $state<RuleEditorState>(parseRule(null));
  let seededId = $state<string | null>(null);
  let savedBody = $state<ReturnType<typeof buildBody> | null>(null);
  let savedEnabled = $state(true);
  const isDirty = $derived(
    savedBody === null || JSON.stringify(buildBody(editor)) !== JSON.stringify(savedBody)
  );

  // Queries — fire on the server during SSR, results land in cache for hydration.
  const rulesQuery = getRules();
  const settingOptionsQuery = getSettingOptions();
  const ruleQuery = $derived(getRule(ruleId));

  const historyQuery = $derived(getAlertHistory({ page: 1, pageSize: 25, alertRuleId: ruleId }));

  const availableRules = $derived<{ id: string; name: string }[]>((rulesQuery.current ?? []).filter((r) => r.id !== ruleId).map((r) => ({ id: r.id ?? "", name: r.name ?? "(unnamed)" })));
  const history = $derived<HistoryExcursionResponse[]>(historyQuery.current?.items ?? []);
  const historyLoading = $derived(historyQuery.current === undefined);

  // Ready once the rule has seeded the editor, not when `ruleQuery.current` is set: the derived
  // can re-run and hand the template a fresh query that never starts, while the seeding effect
  // below has already read the loaded one (#1751).
  const loading = $derived(rulesQuery.current === undefined || seededId !== ruleId);

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
    const rule = ruleQuery.current;
    if (rule === undefined) return;
    untrack(() => {
      editor = parseRule(rule ?? null);
      savedBody = buildBody(editor);
      savedEnabled = editor.isEnabled;
      seededId = ruleId;
    });
  });

  // ---- Save ------------------------------------------------------------

  const lastLowCover = $derived(endsLowCover(ruleQuery.current));

  function requestSave(): void {
    if (lastLowCover && savedEnabled && !editor.isEnabled) {
      confirmingDisable = true;
    } else {
      void save();
    }
  }

  async function save(): Promise<void> {
    const issue =
      validateChannels(editor.channels) ?? freshnessIssue(editor, settingOptionsQuery.current);
    if (issue) {
      error = issue;
      return;
    }
    saving = true;
    error = null;
    try {
      await updateRule({ id: ruleId, request: buildBody(editor) });
      markFreshnessSaved(editor);
      savedBody = buildBody(editor);
      savedEnabled = editor.isEnabled;
      toast.success("Alert saved");
    } catch (e) {
      const described = describeSubmitError(e, "Failed to save the alert rule. Try again.");
      error = conditionIssuesMessage(e) ?? described;
    } finally {
      saving = false;
    }
  }

  async function destroy(): Promise<void> {
    deleting = true;
    error = null;
    try {
      await deleteRule(ruleId);
      await goto(resolve('alerts'));
    } catch(e) {
      error = describeSubmitError(e, "Failed to delete the alert rule. Try again.");
    } finally {
      deleting = false;
    }
  }

  // ---- Test fire -------------------------------------------------------

  async function fireSaved(): Promise<void> {
    testingSaved = true;
    testSent = false;
    testNotSent = [];
    error = null;
    try {
      const answer = await testFire(ruleId);
      const channels = ruleQuery.current?.channels ?? [];
      testNotSent = (answer.notSent ?? []).map((entry) => channelNotSentMessage(entry, channels));
      testSent = true;
    } catch (e) {
      error = describeSubmitError(e, "Failed to send a test alert. Try again.");
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
   * Snapshot the editor state into the dry-run rule shape, from the same body a
   * save sends. Re-evaluated each time Run is pressed so unsaved edits between
   * presses are picked up.
   */
  const severitySchema = z.enum(AlertRuleSeverity);

  function buildReplayRule() {
    return toReplayRule(
      editor,
      ruleId,
      settingOptionsQuery.current?.defaultGlucoseFreshnessMinutes,
    );
  }

  // Switching smart snooze on adds no condition. With none, the engine extends only a high or
  // low alert, and only while glucose moves back toward range; a seeded condition would replace
  // that with one the person did not choose.
  function toggleSmartSnooze(checked: boolean): void {
    editor.clientConfig.snooze.smartSnooze = checked;
  }

  function addSnoozeConditions(): void {
    editor.clientConfig.snooze.conditions = [
      { type: "composite", _uid: crypto.randomUUID(), composite: { operator: "and", conditions: [] } },
    ];
  }

  /** The conditions the history check reads, so it runs again when they change. */
  const conditionKey = $derived.by(() => {
    const body = buildBody(editor);
    return JSON.stringify([
      body.conditionType,
      body.conditionParams,
      body.autoResolveEnabled,
      body.autoResolveParams,
      body.onUnknown,
      body.glucoseFreshnessMinutes,
      body.clearGlucoseFreshness,
    ]);
  });

  beforeNavigate((navigation) => {
    if (loading || deleting || !isDirty || navigation.shallow) return;
    // A reload or a closed tab cannot show a prompt of ours; cancelling shows the browser's.
    if (navigation.willUnload || !confirm("Leave without saving your changes?")) {
      navigation.cancel();
    }
  });
</script>

<svelte:head>
  <title>{editor.name || "Alert"} · Nocturne</title>
</svelte:head>

<div class="@container container mx-auto p-3 @md:p-6 max-w-7xl max-md:pb-24">
  <!-- Header -->
  <EditorActionBar>
    {#snippet leading()}
      <Button
        type="button"
        variant="ghost"
        size="icon"
        onclick={() => goto(resolve('alerts'))}
        aria-label="Back to alerts"
      >
        <ArrowLeft class="h-4 w-4" />
      </Button>
      <div class="min-w-0">
        <h1 class="text-2xl font-bold truncate">
          {editor.name || "Alert"}
        </h1>
        <p class="text-sm text-muted-foreground">Edit alert</p>
      </div>
    {/snippet}
    {#snippet actions()}
      <Button
        type="button"
        variant="outline"
        size="sm"
        onclick={() => (confirmingDelete = true)}
        disabled={deleting}
      >
        {#if deleting}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {:else}
          <Trash2 class="h-4 w-4 mr-2" />
        {/if}
        Delete
      </Button>
      <Button type="button" onclick={requestSave} disabled={saving || loading || !isDirty}>
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {:else}
          <Save class="h-4 w-4 mr-2" />
        {/if}
        Save
      </Button>
    {/snippet}
  </EditorActionBar>

  {#if error}
    <div
      class="mb-4 rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
      role="alert"
    >
      {error}
    </div>
  {/if}

  {#if ruleQuery.current?.hold}
    {@const hold = ruleQuery.current.hold}
    <div
      class="mb-4 flex items-start gap-2 rounded-md border p-3 text-sm text-status-warning"
      data-testid="rule-hold"
    >
      <Hourglass class="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <div>
        <p>{holdSinceText(hold)} This rule cannot open an alert until usable data comes back. After 24 hours with no usable data, Nocturne closes an open alert.</p>
        {#each holdFactLines(hold) as line (line)}
          <p>{line}</p>
        {/each}
      </div>
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
        <Card data-testid="alert-identity-card">
          <CardHeader class="flex flex-row items-start justify-between gap-4">
            <div class="space-y-1.5">
              <CardTitle>Name and severity</CardTitle>
              <CardDescription>
                The notification shows the name.
              </CardDescription>
            </div>
            <div class="flex items-center gap-2 shrink-0">
              <Label class="cursor-pointer" for="rule-enabled">
                Enabled
              </Label>
              <Switch
                id="rule-enabled"
                data-testid="alert-enabled"
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
                placeholder="A note for yourself about this alert"
                value={editor.description}
                oninput={(e: Event & { currentTarget: HTMLTextAreaElement }) => {
                  editor.description = e.currentTarget.value;
                }}
              />
            </div>
            <div class="space-y-2">
              <Label for="rule-severity">Severity</Label>
              <Select.Root
                type="single"
                value={editor.severity}
                onValueChange={(v) => {
                  const parsed = severitySchema.safeParse(v);
                  if (parsed.success) editor.severity = parsed.data;
                }}
              >
                <Select.Trigger id="rule-severity" data-testid="alert-severity">{severityLabel(editor.severity)}</Select.Trigger>
                <Select.Content>
                  {#each severityOptions as o (o.value)}
                    <Select.Item value={o.value} label={o.label} />
                  {/each}
                </Select.Content>
              </Select.Root>
              <p class="text-sm text-muted-foreground">{severityDescription(editor.severity)}</p>
            </div>
            {#if editor.severity !== AlertRuleSeverity.Critical}
              <div class="flex items-center gap-2" data-testid="alert-allow-dnd">
                <Checkbox
                  id="rule-allow-dnd"
                  checked={editor.allowThroughDnd}
                  onCheckedChange={(c: boolean) => {
                    editor.allowThroughDnd = c === true;
                  }}
                />
                <Label class="cursor-pointer" for="rule-allow-dnd">
                  Also alert during Do Not Disturb
                </Label>
              </div>
            {/if}
          </CardContent>
        </Card>

        <!-- Condition tree -->
        <Card data-testid="alert-condition-card">
          <CardHeader>
            <CardTitle>Condition</CardTitle>
            <CardDescription>
              The alert starts when these conditions are true.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {#if editor.condition}
              <RuleBuilder
                bind:node={editor.condition}
                availableRules={availableRules}
              />
            {/if}
          </CardContent>
        </Card>

        <!-- Channels -->
        <Card data-testid="alert-channels-card">
          <CardHeader>
            <CardTitle>Channels</CardTitle>
            <CardDescription>
              Where the alert goes. It goes to each channel at the same time.
            </CardDescription>
          </CardHeader>
          <CardContent class="space-y-3">
            <ChannelsSection
              bind:channels={editor.channels}
              severity={editor.severity}
              {ruleId}
            />
            <OpenOnlyWarning channels={editor.channels} />
          </CardContent>
        </Card>

        <!-- Auto-resolve -->
        <Card data-testid="alert-auto-resolve-card">
          <CardHeader>
            <CardTitle>Auto-resolve</CardTitle>
          </CardHeader>
          <CardContent>
            <AutoResolveSection
              bind:enabled={editor.autoResolveEnabled}
              bind:condition={editor.autoResolveCondition}
              firingCondition={editor.condition}
              availableRules={availableRules}
            />
          </CardContent>
        </Card>

        <!-- Advanced -->
        <Card data-testid="alert-advanced-card">
          <CardContent class="pt-6">
            <AdvancedSection bind:editor />
          </CardContent>
        </Card>

        <!-- Smart snooze -->
        <Card data-testid="alert-smart-snooze-card">
          <CardHeader>
            <CardTitle>Smart snooze</CardTitle>
            <CardDescription>
              When you snooze this alert, Nocturne can extend the snooze for you. Each
              extension counts toward the snooze limit of the alert. If there is no recent
              glucose reading, the alert starts again.
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
              {#if editor.clientConfig.snooze.conditions.length > 0}
                <div class="space-y-2">
                  {#each editor.clientConfig.snooze.conditions as _c, i (i)}
                    <RuleBuilder
                      bind:node={editor.clientConfig.snooze.conditions[i]}
                      availableRules={availableRules}
                      purpose="snooze"
                    />
                  {/each}
                </div>
                <p class="text-sm text-muted-foreground">
                  With no conditions in this list, Nocturne extends only high and low glucose
                  alerts, and only while glucose moves back toward range.
                </p>
              {:else}
                <p class="text-sm text-muted-foreground" data-testid="smart-snooze-default">
                  Nocturne extends the snooze of a high or low glucose alert only while glucose
                  moves back toward range. The snooze of every other alert ends at its set time.
                </p>
                <Button type="button" variant="outline" size="sm" onclick={addSnoozeConditions}>
                  Set your own conditions
                </Button>
              {/if}
            {/if}
          </CardContent>
        </Card>
      {/if}
    </div>

    <!-- Right rail: test alert + historic firings -->
    <aside class="min-w-0 @3xl:sticky @3xl:top-[calc(var(--app-sticky-top,0px)_+_var(--editor-bar-height,0px)_+_1.5rem)] self-start space-y-4">
      <Card>
        <CardHeader>
          <CardTitle class="text-base">Check the alert</CardTitle>
        </CardHeader>
        <CardContent class="space-y-3">
          {#if !loading && editor.condition && !freshnessIssue(editor, settingOptionsQuery.current)}
            <HistoryCheck
              compact
              rule={buildReplayRule}
              condition={editor.condition}
              severity={editor.severity}
              watch={conditionKey}
              onOpenReplay={openReplay}
            />
          {/if}
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
              <Send class="h-4 w-4 mr-2" />
            {/if}
            Send a test alert
          </Button>
          {#if testSent && testNotSent.length > 0}
            <ChannelsNotSent messages={testNotSent} />
          {:else if testSent}
            <p class="flex items-center gap-1.5 text-sm text-success" role="status">
              <Check class="size-4" aria-hidden="true" />
              Test alert sent. Make sure that it arrived.
            </p>
          {:else if isDirty && !loading}
            <p class="text-sm text-muted-foreground">
              The test uses the saved alert. Save your changes first to test them.
            </p>
          {/if}
          <Button
            type="button"
            variant="outline"
            class="w-full justify-start"
            onclick={() => openReplay()}
            disabled={loading}
          >
            <PlayCircle class="h-4 w-4 mr-2" />
            Replay a past day
          </Button>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle class="text-base flex items-center gap-2">
            <HistoryIcon class="h-4 w-4" /> Past alerts
          </CardTitle>
          <CardDescription size="sm">
            The times this alert started. Select one to replay that day.
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
            <EmptyState
              art="clock"
              size="compact"
              variant="outline"
              title="This alert has not started yet"
            />
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
                    title="Replay this day"
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
                      <Check class="size-3 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <span class="sr-only">Acknowledged</span>
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
    </aside>
  </div>
</div>

<ConfirmDialog
  bind:open={confirmingDisable}
  title={`Disable "${editor.name}"?`}
  confirmLabel="Disable"
  destructive
  busy={saving}
  onConfirm={save}
>
  {#snippet description()}
    {lastLowCoverMessage}
  {/snippet}
</ConfirmDialog>

<ConfirmDialog
  bind:open={confirmingDelete}
  title={`Delete "${editor.name}"?`}
  confirmLabel="Delete"
  busy={deleting}
  onConfirm={destroy}
>
  {#snippet description()}
    {#if lastLowCover}
      {lastLowCoverMessage}
    {/if}
    This rule will stop firing immediately. Existing alert history is preserved. This action
    cannot be undone.
  {/snippet}
</ConfirmDialog>

<Dialog.Root bind:open={replayOpen}>
  <!-- eslint-disable-next-line shadcn/no-restyle -- the replay workbench fills the viewport; its bordered header and scrolling body pad themselves, so the dialog cannot -->
  <Dialog.Content class="flex h-[90vh] max-h-[90vh] w-[calc(100vw-1rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0 sm:w-[95vw] sm:max-w-7xl">
    <Dialog.Header class="border-b px-4 py-3">
      <Dialog.Title class="flex items-center gap-2">
        <PlayCircle class="h-4 w-4" /> Replay
      </Dialog.Title>
      <Dialog.Description>
        This alert and your other alerts, played against the glucose of that day. Nothing is
        sent.
      </Dialog.Description>
    </Dialog.Header>

    <div class="@container min-h-0 flex-1 overflow-hidden p-3 @md:p-4">
      <ReplayPanel
        initialCustomDate={replayInitialDate}
        rule={buildReplayRule}
        editingRuleId={ruleId}
        editingTree={editor.condition ?? undefined}
        availableRules={rulesQuery.current ?? []}
      />
    </div>
  </Dialog.Content>
</Dialog.Root>
