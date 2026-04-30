<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import { createRule } from "$api/generated/alertRules.generated.remote";
  import type {
    CreateAlertRuleRequest,
    CreateAlertStepChannelRequest,
  } from "$api-clients";
  import { AlertRuleSeverity, ChannelType } from "$api-clients";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { Badge } from "$lib/components/ui/badge";
  import {
    ArrowLeft,
    ArrowRight,
    Check,
    TrendingDown,
    TrendingUp,
    Zap,
    WifiOff,
    AlertTriangle,
    Shield,
    Loader2,
  } from "lucide-svelte";
  import ChannelPicker from "$lib/components/alerts/ChannelPicker.svelte";
  import { nodeToApi, stripEditorFields } from "$lib/components/alerts/types";
  import type { ConditionNode } from "$lib/components/alerts/types";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import {
    bgValue,
    bgLabel,
    convertFromDisplayUnits,
  } from "$lib/utils/formatting";

  // Step management
  let currentStep = $state(1);
  const totalSteps = 3;

  // ---------------------------------------------------------------------------
  // Preset shape
  // ---------------------------------------------------------------------------
  //
  // Each preset is a self-contained recipe for a `CreateAlertRuleRequest`.
  // `threshold`/`thresholdField` drive the UI's editable number; the
  // `buildRequest` callback turns the current preset into the final API
  // payload using the new `ConditionNode` shape.

  type PresetKind = "glucose" | "duration";

  type Preset = {
    key: string;
    name: string;
    description: string;
    icon: typeof TrendingDown;
    kind: PresetKind;
    severity: AlertRuleSeverity;
    threshold: number;
    enabled: boolean;
    buildRule: (p: Preset) => {
      condition: ConditionNode;
      autoResolveEnabled: boolean;
      autoResolveCondition: ConditionNode | null;
    };
  };

  function thresholdNode(direction: "above" | "below", value: number): ConditionNode {
    return { type: "threshold", threshold: { direction, value } };
  }

  function compositeOf(child: ConditionNode): ConditionNode {
    return {
      type: "composite",
      composite: { operator: "and", conditions: [child] },
    };
  }

  function autoResolveAbove(value: number): ConditionNode {
    return compositeOf(thresholdNode("above", value));
  }

  function autoResolveBelow(value: number): ConditionNode {
    return compositeOf(thresholdNode("below", value));
  }

  let presets = $state<Preset[]>([
    {
      key: "urgent_low",
      name: "Urgent Low",
      description: "Critical low glucose alert for immediate attention",
      icon: AlertTriangle,
      kind: "glucose",
      severity: AlertRuleSeverity.Critical,
      threshold: 54,
      enabled: true,
      buildRule: (p) => ({
        condition: compositeOf(thresholdNode("below", p.threshold)),
        autoResolveEnabled: true,
        autoResolveCondition: autoResolveAbove(70),
      }),
    },
    {
      key: "low",
      name: "Low",
      description: "Low glucose warning before it becomes urgent",
      icon: TrendingDown,
      kind: "glucose",
      severity: AlertRuleSeverity.Warning,
      threshold: 70,
      enabled: true,
      buildRule: (p) => ({
        condition: compositeOf(thresholdNode("below", p.threshold)),
        autoResolveEnabled: true,
        autoResolveCondition: autoResolveAbove(80),
      }),
    },
    {
      key: "high",
      name: "High",
      description: "High glucose alert for sustained elevated readings",
      icon: TrendingUp,
      kind: "glucose",
      severity: AlertRuleSeverity.Warning,
      threshold: 250,
      enabled: false,
      buildRule: (p) => ({
        condition: compositeOf(thresholdNode("above", p.threshold)),
        autoResolveEnabled: true,
        autoResolveCondition: autoResolveBelow(180),
      }),
    },
    {
      key: "urgent_high",
      name: "Urgent High",
      description: "Critical high glucose alert requiring prompt action",
      icon: AlertTriangle,
      kind: "glucose",
      severity: AlertRuleSeverity.Critical,
      threshold: 300,
      enabled: false,
      buildRule: (p) => ({
        condition: compositeOf(thresholdNode("above", p.threshold)),
        autoResolveEnabled: true,
        autoResolveCondition: autoResolveBelow(250),
      }),
    },
    {
      key: "rapid_drop",
      name: "Rapid Drop",
      description: "Glucose falling faster than the configured rate",
      icon: Zap,
      kind: "glucose",
      severity: AlertRuleSeverity.Warning,
      // Threshold here is the BG floor that gates the rate-of-change check.
      threshold: 100,
      enabled: false,
      buildRule: (p) => ({
        condition: {
          type: "composite",
          composite: {
            operator: "and",
            conditions: [
              thresholdNode("below", p.threshold),
              {
                type: "rate_of_change",
                rate_of_change: { direction: "falling", rate: 3 },
              },
            ],
          },
        },
        autoResolveEnabled: true,
        autoResolveCondition: autoResolveAbove(p.threshold + 20),
      }),
    },
    {
      key: "signal_loss",
      name: "Signal Loss",
      description: "Alert when CGM data has been stale for too long",
      icon: WifiOff,
      kind: "duration",
      severity: AlertRuleSeverity.Warning,
      threshold: 15,
      enabled: false,
      buildRule: (p) => ({
        condition: compositeOf({
          type: "staleness",
          staleness: { operator: ">=", value: p.threshold },
        }),
        // Staleness clears when readings resume. The frontend models this as
        // "current staleness < small grace window".
        autoResolveEnabled: true,
        autoResolveCondition: compositeOf({
          type: "staleness",
          staleness: { operator: "<", value: 5 },
        }),
      }),
    },
  ]);

  // Step 2: Delivery channels
  let selectedChannels = $state<
    Array<{
      channelType: ChannelType;
      destination: string;
      destinationLabel: string;
    }>
  >([]);

  // Step 3: Saving state
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  const selectedPresets = $derived(presets.filter((p) => p.enabled));

  function isGlucosePreset(preset: Preset): boolean {
    return preset.kind === "glucose";
  }

  function displayThreshold(preset: Preset): number {
    return isGlucosePreset(preset) ? bgValue(preset.threshold) : preset.threshold;
  }

  function thresholdUnitLabel(preset: Preset): string {
    return isGlucosePreset(preset) ? bgLabel() : "minutes";
  }

  function updateThreshold(key: string, value: number) {
    const preset = presets.find((p) => p.key === key);
    if (!preset) return;
    preset.threshold = value;
  }

  function togglePreset(key: string) {
    const preset = presets.find((p) => p.key === key);
    if (preset) {
      preset.enabled = !preset.enabled;
    }
  }

  function severityLabel(severity: AlertRuleSeverity): string {
    switch (severity) {
      case AlertRuleSeverity.Critical:
        return "Critical";
      case AlertRuleSeverity.Warning:
        return "Warning";
      case AlertRuleSeverity.Info:
        return "Info";
      default:
        return severity;
    }
  }

  // Build a CreateAlertRuleRequest from a preset.
  // Info severity defaults to InApp-only channels per Task 19b's frontend
  // convention; Warning/Critical use whatever the wizard collected.
  function buildRequest(preset: Preset): CreateAlertRuleRequest {
    const built = preset.buildRule(preset);
    const conditionApi = nodeToApi(built.condition);

    const isInfo = preset.severity === AlertRuleSeverity.Info;
    // Info-severity rules default to InApp-only delivery (Task 19b convention).
    // The InAppProvider keys notifications by the recipient's auth subjectId;
    // empty destination would skip delivery silently.
    const channelsForPreset = isInfo
      ? [
          {
            channelType: ChannelType.InApp,
            destination: page.data.user?.subjectId ?? "",
            destinationLabel: page.data.user?.displayName ?? "Me",
          },
        ]
      : selectedChannels;

    const channels: CreateAlertStepChannelRequest[] = channelsForPreset
      .filter((c) => c.channelType !== ChannelType.Webhook || c.destination)
      .map((c) => ({
        channelType: c.channelType,
        destination: c.destination || undefined,
        destinationLabel: c.destinationLabel || undefined,
      }));

    // ASYMMETRY: conditionType + conditionParams are persisted as separate columns
    // (the params is just the kind-specific payload). autoResolveParams is a single
    // jsonb column the backend deserialises directly into a ConditionNode envelope —
    // it must include the `type` discriminator alongside the kind's payload field.
    return {
      name: preset.name,
      description: preset.description,
      conditionType: conditionApi?.conditionType,
      conditionParams: conditionApi?.conditionParams,
      autoResolveEnabled: built.autoResolveEnabled,
      autoResolveParams:
        built.autoResolveEnabled && built.autoResolveCondition
          ? stripEditorFields(built.autoResolveCondition)
          : undefined,
      isEnabled: true,
      sortOrder: presets.indexOf(preset),
      severity: preset.severity,
      schedules: [
        {
          name: "Default",
          isDefault: true,
          escalationSteps:
            channels.length > 0
              ? [
                  {
                    stepOrder: 0,
                    delaySeconds: 0,
                    channels,
                  },
                ]
              : undefined,
        },
      ],
    };
  }

  async function handleSave() {
    saving = true;
    saveError = null;

    try {
      for (const preset of selectedPresets) {
        await createRule(buildRequest(preset));
      }

      goto("/settings/alerts");
    } catch (err) {
      saveError =
        err instanceof Error ? err.message : "Failed to create alert rules";
    } finally {
      saving = false;
    }
  }
</script>

<svelte:head>
  <title>Alert Setup - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-3xl p-6 space-y-6">
  <!-- Header -->
  <div>
    <Button
      variant="ghost"
      size="sm"
      class="mb-2"
      onclick={() => goto("/settings/alerts")}
    >
      <ArrowLeft class="h-4 w-4 mr-2" />
      Back to Alerts
    </Button>
    <h1 class="text-2xl font-bold tracking-tight">Alert Setup Wizard</h1>
    <p class="text-muted-foreground">
      Configure your glucose alert rules in a few simple steps
    </p>
  </div>

  <!-- Step Indicator -->
  <div class="flex items-center gap-2">
    {#each Array(totalSteps) as _, i (i)}
      {@const step = i + 1}
      <div class="flex items-center gap-2 flex-1">
        <div
          class="flex items-center justify-center h-8 w-8 rounded-full text-sm font-medium shrink-0 {step <= currentStep
            ? 'bg-primary text-primary-foreground'
            : 'bg-muted text-muted-foreground'}"
        >
          {#if step < currentStep}
            <Check class="h-4 w-4" />
          {:else}
            {step}
          {/if}
        </div>
        <span
          class="text-sm hidden sm:inline {step === currentStep
            ? 'font-medium'
            : 'text-muted-foreground'}"
        >
          {#if step === 1}
            Choose Presets
          {:else if step === 2}
            Delivery Channels
          {:else}
            Review & Save
          {/if}
        </span>
        {#if i < totalSteps - 1}
          <div
            class="flex-1 h-px {step < currentStep
              ? 'bg-primary'
              : 'bg-muted'}"
          ></div>
        {/if}
      </div>
    {/each}
  </div>

  <!-- Step 1: Choose Presets -->
  {#if currentStep === 1}
    <div class="space-y-4">
      <div>
        <h2 class="text-lg font-semibold">Choose Alert Presets</h2>
        <p class="text-sm text-muted-foreground">
          Select the alerts you want to enable. You can customize thresholds for
          each one.
        </p>
      </div>

      <div class="grid gap-3 sm:grid-cols-2">
        {#each presets as preset (preset.key)}
          {@const PresetIcon = preset.icon}
          <Card
            class="cursor-pointer transition-all {preset.enabled
              ? 'border-primary ring-1 ring-primary/20'
              : 'hover:border-primary/50'}"
          >
            <CardContent class="p-4">
              <button
                class="flex items-start gap-3 w-full text-left"
                onclick={() => togglePreset(preset.key)}
              >
                <div
                  class="flex items-center justify-center h-10 w-10 rounded-lg shrink-0 {preset.enabled
                    ? 'bg-primary/10 text-primary'
                    : 'bg-muted text-muted-foreground'}"
                >
                  <PresetIcon class="h-5 w-5" />
                </div>
                <div class="flex-1 min-w-0">
                  <div class="flex items-center justify-between mb-1">
                    <span class="font-medium">{preset.name}</span>
                    <Switch
                      checked={preset.enabled}
                      onCheckedChange={() => togglePreset(preset.key)}
                    />
                  </div>
                  <p class="text-xs text-muted-foreground">
                    {preset.description}
                  </p>
                </div>
              </button>

              {#if preset.enabled}
                <div class="mt-3 pt-3 border-t space-y-2">
                  <div class="flex items-center gap-2">
                    <Label class="text-xs w-20 shrink-0">Threshold</Label>
                    <Input
                      type="number"
                      value={displayThreshold(preset)}
                      class="h-8 text-sm"
                      step={isGlucosePreset(preset) &&
                      glucoseUnits.current === "mmol"
                        ? "0.1"
                        : "1"}
                      oninput={(e) => {
                        const val = parseFloat(e.currentTarget.value);
                        if (!Number.isNaN(val)) {
                          updateThreshold(
                            preset.key,
                            isGlucosePreset(preset)
                              ? convertFromDisplayUnits(
                                  val,
                                  glucoseUnits.current,
                                )
                              : val,
                          );
                        }
                      }}
                    />
                    <span class="text-xs text-muted-foreground shrink-0">
                      {thresholdUnitLabel(preset)}
                    </span>
                  </div>
                  <div
                    class="flex items-center gap-4 text-xs text-muted-foreground"
                  >
                    <span>Severity: {severityLabel(preset.severity)}</span>
                  </div>
                </div>
              {/if}
            </CardContent>
          </Card>
        {/each}
      </div>
    </div>
  {/if}

  <!-- Step 2: Delivery Channels -->
  {#if currentStep === 2}
    <div class="space-y-4">
      <div>
        <h2 class="text-lg font-semibold">Delivery Channels</h2>
        <p class="text-sm text-muted-foreground">
          Choose how you want to receive alert notifications.
        </p>
      </div>
      <Card>
        <CardContent class="p-4">
          <ChannelPicker bind:channels={selectedChannels} />
        </CardContent>
      </Card>
    </div>
  {/if}

  <!-- Step 3: Review & Save -->
  {#if currentStep === 3}
    <div class="space-y-4">
      <div>
        <h2 class="text-lg font-semibold">Review & Save</h2>
        <p class="text-sm text-muted-foreground">
          Review your alert configuration before saving.
        </p>
      </div>

      <!-- Selected Rules Summary -->
      <Card>
        <CardHeader>
          <CardTitle class="text-base">
            Selected Alert Rules ({selectedPresets.length})
          </CardTitle>
        </CardHeader>
        <CardContent class="space-y-2">
          {#if selectedPresets.length === 0}
            <p class="text-sm text-muted-foreground py-4 text-center">
              No presets selected. Go back to step 1 to select at least one
              alert.
            </p>
          {:else}
            {#each selectedPresets as preset (preset.key)}
              {@const PresetIcon = preset.icon}
              <div class="flex items-center gap-3 p-3 rounded-lg border">
                <PresetIcon class="h-4 w-4 text-primary shrink-0" />
                <div class="flex-1 min-w-0">
                  <span class="text-sm font-medium">{preset.name}</span>
                  {#if preset.severity === AlertRuleSeverity.Critical}
                    <Badge variant="destructive" class="ml-2 text-xs">
                      Critical
                    </Badge>
                  {/if}
                  <span class="text-xs text-muted-foreground ml-2">
                    {displayThreshold(preset)}
                    {thresholdUnitLabel(preset)}
                  </span>
                </div>
                <div class="text-xs text-muted-foreground">
                  {severityLabel(preset.severity)}
                </div>
              </div>
            {/each}
          {/if}
        </CardContent>
      </Card>

      <!-- Channels Summary -->
      <Card>
        <CardHeader>
          <CardTitle class="text-base">Delivery Channels</CardTitle>
        </CardHeader>
        <CardContent class="space-y-2">
          {#if selectedChannels.length === 0}
            <p class="text-sm text-muted-foreground py-2">
              No delivery channels configured. Alerts will still be visible in
              the dashboard, but no push notifications will be sent.
            </p>
          {:else}
            {#each selectedChannels as ch (ch.channelType)}
              <div class="flex items-center gap-2 text-sm">
                <span>{ch.destinationLabel || ch.channelType}</span>
                {#if ch.destination}
                  <span class="text-muted-foreground">({ch.destination})</span>
                {/if}
              </div>
            {/each}
          {/if}
        </CardContent>
      </Card>

      <!-- Disclaimer -->
      <Card class="border-amber-500/30 bg-amber-500/5">
        <CardContent class="flex gap-3 pt-6">
          <Shield class="h-5 w-5 text-amber-600 shrink-0 mt-0.5" />
          <div class="text-sm">
            <p class="font-medium text-amber-800 dark:text-amber-200 mb-1">
              Medical Disclaimer
            </p>
            <p class="text-muted-foreground">
              Nocturne alerts are not a substitute for professional medical
              advice, diagnosis, or treatment. Always consult your healthcare
              provider for medical decisions. Alert delivery depends on network
              connectivity, device availability, and third-party service
              reliability. Do not rely solely on these alerts for critical
              medical decisions.
            </p>
          </div>
        </CardContent>
      </Card>

      {#if saveError}
        <Card class="border-destructive">
          <CardContent class="flex items-center gap-3 pt-6">
            <AlertTriangle class="h-5 w-5 text-destructive" />
            <p class="text-sm text-destructive">{saveError}</p>
          </CardContent>
        </Card>
      {/if}
    </div>
  {/if}

  <!-- Navigation Buttons -->
  <div class="flex items-center justify-between pt-4 border-t">
    <Button
      variant="outline"
      onclick={() => {
        if (currentStep === 1) {
          goto("/settings/alerts");
        } else {
          currentStep--;
        }
      }}
    >
      <ArrowLeft class="h-4 w-4 mr-2" />
      {currentStep === 1 ? "Cancel" : "Previous"}
    </Button>

    {#if currentStep < totalSteps}
      <Button onclick={() => currentStep++}>
        Next
        <ArrowRight class="h-4 w-4 ml-2" />
      </Button>
    {:else}
      <Button
        onclick={handleSave}
        disabled={saving || selectedPresets.length === 0}
      >
        {#if saving}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
          Creating Rules...
        {:else}
          <Check class="h-4 w-4 mr-2" />
          Create {selectedPresets.length} Rule{selectedPresets.length !== 1
            ? "s"
            : ""}
        {/if}
      </Button>
    {/if}
  </div>
</div>
