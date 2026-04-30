<script lang="ts">
  import { onMount } from "svelte";
  import {
    createRule,
    getRules,
    updateRule,
  } from "$api/generated/alertRules.generated.remote";
  import { getSounds } from "$api/generated/alertCustomSounds.generated.remote";
  import { getChannelStatuses } from "$api/generated/systems.generated.remote";
  import {
    ChannelStatus,
    type ChannelStatusEntry,
    AlertRuleSeverity,
    ChannelType,
  } from "$api-clients";
  import type {
    AlertRuleResponse,
    AlertCustomSoundResponse,
    CreateAlertScheduleRequest,
    CreateAlertEscalationStepRequest,
    CreateAlertStepChannelRequest,
  } from "$api-clients";
  import * as Sheet from "$lib/components/ui/sheet";
  import * as Tabs from "$lib/components/ui/tabs";
  import { Button } from "$lib/components/ui/button";
  import { AlertCircle, History, Loader2 } from "lucide-svelte";
  import GeneralTab from "./GeneralTab.svelte";
  import PresentationTab from "./PresentationTab.svelte";
  import SnoozeTab from "./SnoozeTab.svelte";
  import SchedulesTab from "./SchedulesTab.svelte";
  import RuleBuilder from "./RuleBuilder.svelte";
  import AutoResolveSection from "./AutoResolveSection.svelte";
  import ReplayDialog from "./ReplayDialog.svelte";
  import {
    defaultClientConfig,
    defaultPayload,
    defaultSchedule,
    nodeToApi,
    parseRule,
    stripEditorFields,
  } from "./types";
  import type {
    ClientConfiguration,
    ConditionNode,
    EditableSchedule,
  } from "./types";

  interface Props {
    open: boolean;
    rule: AlertRuleResponse | null;
    onSave: () => void;
  }

  let { open = $bindable(), rule, onSave }: Props = $props();

  // --- State ---
  let activeTab = $state<string>("general");
  let saving = $state(false);
  let saveError = $state<string | null>(null);
  let replayOpen = $state(false);
  let customSounds = $state<AlertCustomSoundResponse[]>([]);
  let availableChannels = $state<ChannelStatusEntry[]>([]);
  let availableRules = $state<{ id: string; name: string }[]>([]);

  // General tab
  let name = $state("");
  let description = $state("");
  let severity = $state<AlertRuleSeverity>(AlertRuleSeverity.Warning);
  let sortOrder = $state(0);
  let isEnabled = $state(true);

  // Condition tree
  let condition = $state<ConditionNode | null>(defaultPayload("composite"));
  let autoResolveEnabled = $state(false);
  let autoResolveCondition = $state<ConditionNode | null>(null);

  // Presentation tab
  let clientConfig = $state<ClientConfiguration>(defaultClientConfig());

  // Schedules tab
  let schedules = $state<EditableSchedule[]>([defaultSchedule()]);

  // --- Computed ---
  let isEditMode = $derived(rule !== null);
  let title = $derived(isEditMode ? "Edit Rule" : "Create Rule");

  // Validation
  let conditionMissing = $derived(condition === null);
  let autoResolveMissing = $derived(
    autoResolveEnabled && autoResolveCondition === null,
  );
  let canSave = $derived(
    !saving &&
      name.trim().length > 0 &&
      !conditionMissing &&
      !autoResolveMissing,
  );

  // --- Initialization ---
  function applyState(r: AlertRuleResponse | null) {
    const s = parseRule(r);
    name = s.name;
    description = s.description;
    severity = s.severity;
    condition = s.condition;
    autoResolveEnabled = s.autoResolveEnabled;
    autoResolveCondition = s.autoResolveCondition;
    sortOrder = s.sortOrder;
    isEnabled = s.isEnabled;
    clientConfig = s.clientConfig;
    schedules = s.schedules;
    activeTab = "general";
    saveError = null;
  }

  $effect(() => {
    if (open) {
      applyState(rule);
      void refreshAvailableRules();
    }
  });

  async function refreshAvailableRules() {
    try {
      const result = await getRules();
      const list = Array.isArray(result) ? result : [];
      availableRules = list
        .filter((r) => r.id && r.id !== rule?.id)
        .map((r) => ({ id: r.id!, name: r.name ?? "(unnamed rule)" }));
    } catch {
      availableRules = [];
    }
  }

  // Load custom sounds and available channels on mount
  onMount(async () => {
    try {
      const result = await getSounds();
      customSounds = Array.isArray(result) ? result : [];
    } catch {
      // Sounds unavailable
    }

    getChannelStatuses()
      .then((res) => {
        availableChannels = (res?.channels ?? []).filter(
          (c) => c.status !== ChannelStatus.Unavailable,
        );
      })
      .catch(() => {});
  });

  // --- Save ---
  async function handleSave() {
    if (!canSave) return;
    saving = true;
    saveError = null;
    try {
      const schedulesPayload: CreateAlertScheduleRequest[] = schedules.map(
        (s) => ({
          name: s.name || undefined,
          isDefault: s.isDefault,
          daysOfWeek:
            s.daysOfWeek.length === 0 || s.daysOfWeek.length === 7
              ? undefined
              : s.daysOfWeek,
          startTime: s.isDefault ? undefined : s.startTime || undefined,
          endTime: s.isDefault ? undefined : s.endTime || undefined,
          timezone: s.timezone || undefined,
          escalationSteps: s.escalationSteps.map(
            (step): CreateAlertEscalationStepRequest => ({
              stepOrder: step.stepOrder,
              delaySeconds: step.delaySeconds,
              channels: step.channels.map(
                (ch): CreateAlertStepChannelRequest => ({
                  channelType: ch.channelType as ChannelType,
                  destination: ch.destination || undefined,
                  destinationLabel: ch.destinationLabel || undefined,
                }),
              ),
            }),
          ),
        }),
      );

      const conditionApi = nodeToApi(condition);

      // ASYMMETRY: the rule's main condition stores `conditionType` + `conditionParams`
      // (kind discriminator + kind-specific payload, two columns). Auto-resolve stores
      // a single `autoResolveParams` blob that the backend deserialises directly into
      // a `ConditionNode` envelope — so it must include the `type` discriminator and
      // the kind's payload field side by side, NOT just the inner payload. Strip the
      // editor-only `_uid` so it doesn't leak into stored configuration.
      const payload = {
        name,
        description: description || undefined,
        conditionType: conditionApi?.conditionType,
        conditionParams: conditionApi?.conditionParams,
        autoResolveEnabled,
        autoResolveParams:
          autoResolveEnabled && autoResolveCondition
            ? stripEditorFields(autoResolveCondition)
            : undefined,
        isEnabled,
        sortOrder,
        severity: severity || undefined,
        clientConfiguration: clientConfig,
        schedules: schedulesPayload,
      };

      if (isEditMode && rule?.id) {
        await updateRule({ id: rule.id, request: payload });
      } else {
        await createRule(payload);
      }

      onSave();
      open = false;
    } catch (err) {
      const status = (err as { status?: number })?.status;
      const message =
        (err as { body?: { message?: string }; message?: string })?.body
          ?.message ??
        (err as { message?: string })?.message ??
        "";
      if (status === 400) {
        saveError =
          message ||
          "The rule failed validation. Cyclical references between rules are not allowed.";
      } else if (status === 409) {
        saveError =
          message ||
          "Another rule references this one. Disable or remove the dependants first.";
      } else {
        saveError = message || "Failed to save rule. Please try again.";
      }
    } finally {
      saving = false;
    }
  }
</script>

<Sheet.Root bind:open>
  <Sheet.Content side="right" class="w-full sm:max-w-xl overflow-y-auto">
    <Sheet.Header>
      <Sheet.Title>{title}</Sheet.Title>
      <Sheet.Description>
        {isEditMode
          ? "Modify the alert rule configuration"
          : "Configure a new alert rule"}
      </Sheet.Description>
    </Sheet.Header>

    {#if saveError}
      <div
        class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive"
        role="alert"
      >
        <AlertCircle class="h-4 w-4 mt-0.5 flex-none" />
        <p>{saveError}</p>
      </div>
    {/if}

    <div class="flex-1 overflow-y-auto px-1">
      <Tabs.Root bind:value={activeTab}>
        <Tabs.List class="w-full">
          <Tabs.Trigger value="general" class="flex-1">General</Tabs.Trigger>
          <Tabs.Trigger value="condition" class="flex-1">
            Condition
          </Tabs.Trigger>
          <Tabs.Trigger value="auto-resolve" class="flex-1">
            Auto-Resolve
          </Tabs.Trigger>
          <Tabs.Trigger value="schedules" class="flex-1">
            Schedules
          </Tabs.Trigger>
          <Tabs.Trigger value="snooze" class="flex-1">Snooze</Tabs.Trigger>
          <Tabs.Trigger value="presentation" class="flex-1">
            Presentation
          </Tabs.Trigger>
        </Tabs.List>

        <!-- General Tab -->
        <Tabs.Content value="general" class="space-y-4 pt-4">
          <GeneralTab
            bind:name
            bind:description
            bind:severity
            bind:sortOrder
            bind:isEnabled
          />
        </Tabs.Content>

        <!-- Condition Tab -->
        <Tabs.Content value="condition" class="space-y-3 pt-4">
          <div class="space-y-1">
            <h3 class="text-sm font-medium">Condition tree</h3>
            <p class="text-xs text-muted-foreground">
              Build the condition that fires this alert. Group conditions
              with AND/OR, negate them, or require sustained truth before
              firing.
            </p>
          </div>
          {#if condition !== null}
            <RuleBuilder bind:node={condition} {availableRules} />
          {:else}
            <div class="space-y-2">
              <p class="text-sm text-muted-foreground">
                No condition configured.
              </p>
              <Button
                variant="outline"
                size="sm"
                onclick={() => {
                  condition = defaultPayload("composite");
                }}
              >
                Add condition
              </Button>
            </div>
          {/if}
          {#if conditionMissing}
            <p class="text-sm text-destructive">
              A condition is required before saving.
            </p>
          {/if}
        </Tabs.Content>

        <!-- Auto-Resolve Tab -->
        <Tabs.Content value="auto-resolve" class="space-y-3 pt-4">
          <div class="space-y-1">
            <h3 class="text-sm font-medium">Auto-resolve</h3>
            <p class="text-xs text-muted-foreground">
              Automatically close active alerts when a separate condition is
              true. Useful for mirroring "alert clears when value returns to
              range".
            </p>
          </div>
          <AutoResolveSection
            bind:enabled={autoResolveEnabled}
            bind:condition={autoResolveCondition}
            {availableRules}
          />
          {#if autoResolveMissing}
            <p class="text-sm text-destructive">
              Auto-resolve is enabled — add a condition or turn it off.
            </p>
          {/if}
        </Tabs.Content>

        <!-- Schedules Tab -->
        <Tabs.Content value="schedules" class="space-y-4 pt-4">
          <SchedulesTab bind:schedules {availableChannels} />
        </Tabs.Content>

        <!-- Snooze Tab -->
        <Tabs.Content value="snooze" class="space-y-4 pt-4">
          <SnoozeTab bind:snooze={clientConfig.snooze} {availableRules} />
        </Tabs.Content>

        <!-- Presentation Tab -->
        <Tabs.Content value="presentation" class="space-y-6 pt-4">
          <PresentationTab
            bind:clientConfig
            {customSounds}
            onSoundsChanged={(sounds) => {
              customSounds = sounds;
            }}
          />
        </Tabs.Content>
      </Tabs.Root>
    </div>

    <Sheet.Footer class="mt-4">
      <Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
      <Button
        variant="outline"
        onclick={() => (replayOpen = true)}
        title="Replay enabled rules over a window"
      >
        <History class="h-4 w-4 mr-2" />
        Replay
      </Button>
      <Button onclick={handleSave} disabled={!canSave}>
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isEditMode ? "Update Rule" : "Create Rule"}
      </Button>
    </Sheet.Footer>
  </Sheet.Content>
</Sheet.Root>

<ReplayDialog bind:open={replayOpen} />
