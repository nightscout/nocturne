<script lang="ts">
  import { onMount } from "svelte";
  import {
    createRule,
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
  import { Loader2 } from "lucide-svelte";
  import GeneralTab from "./GeneralTab.svelte";
  import PresentationTab from "./PresentationTab.svelte";
  import SnoozeTab from "./SnoozeTab.svelte";
  import SchedulesTab from "./SchedulesTab.svelte";
  import {
    defaultClientConfig,
    defaultPayload,
    defaultSchedule,
    nodeToApi,
    parseRule,
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
  let customSounds = $state<AlertCustomSoundResponse[]>([]);
  let availableChannels = $state<ChannelStatusEntry[]>([]);

  // General tab
  let name = $state("");
  let description = $state("");
  let severity = $state<AlertRuleSeverity>(AlertRuleSeverity.Warning);

  // Condition tree (edited via the upcoming RuleBuilder integration)
  let condition = $state<ConditionNode | null>(defaultPayload("threshold"));
  let autoResolveEnabled = $state(false);
  let autoResolveCondition = $state<ConditionNode | null>(null);

  let sortOrder = $state(0);
  let isEnabled = $state(true);

  // Presentation tab
  let clientConfig = $state<ClientConfiguration>(defaultClientConfig());

  // Schedules tab
  let schedules = $state<EditableSchedule[]>([defaultSchedule()]);

  // --- Computed ---
  let isEditMode = $derived(rule !== null);
  let title = $derived(isEditMode ? "Edit Rule" : "Create Rule");

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
  }

  $effect(() => {
    if (open) {
      applyState(rule);
    }
  });

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
          (c) => c.status !== ChannelStatus.Unavailable
        );
      })
      .catch(() => {});
  });

  // --- Save ---
  async function handleSave() {
    saving = true;
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
                })
              ),
            })
          ),
        })
      );

      const conditionApi = nodeToApi(condition);
      const autoResolveApi = autoResolveEnabled
        ? nodeToApi(autoResolveCondition)
        : null;

      const payload = {
        name,
        description: description || undefined,
        conditionType: conditionApi?.conditionType,
        conditionParams: conditionApi?.conditionParams,
        autoResolveEnabled,
        autoResolveParams: autoResolveApi?.conditionParams ?? undefined,
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
    } catch {
      // Error handled by remote function
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

    <div class="flex-1 overflow-y-auto px-1">
      <Tabs.Root bind:value={activeTab}>
        <Tabs.List class="w-full">
          <Tabs.Trigger value="general" class="flex-1">General</Tabs.Trigger>
          <Tabs.Trigger value="presentation" class="flex-1">
            Presentation
          </Tabs.Trigger>
          <Tabs.Trigger value="snooze" class="flex-1">Snooze</Tabs.Trigger>
          <Tabs.Trigger value="schedules" class="flex-1">
            Schedules
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

        <!-- Snooze Tab -->
        <Tabs.Content value="snooze" class="space-y-4 pt-4">
          <SnoozeTab bind:snooze={clientConfig.snooze} />
        </Tabs.Content>

        <!-- Schedules Tab -->
        <Tabs.Content value="schedules" class="space-y-4 pt-4">
          <SchedulesTab bind:schedules {availableChannels} />
        </Tabs.Content>
      </Tabs.Root>
    </div>

    <Sheet.Footer class="mt-4">
      <Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
      <Button onclick={handleSave} disabled={saving || !name.trim()}>
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
        {/if}
        {isEditMode ? "Update Rule" : "Create Rule"}
      </Button>
    </Sheet.Footer>
  </Sheet.Content>
</Sheet.Root>
