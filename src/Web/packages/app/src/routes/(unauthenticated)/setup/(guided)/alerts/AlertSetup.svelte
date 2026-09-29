<script lang="ts">
  import { untrack } from "svelte";
  import { resolve } from "$app/paths";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import BellRing from "@lucide/svelte/icons/bell-ring";
  import Check from "@lucide/svelte/icons/check";
  import Info from "@lucide/svelte/icons/info";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import * as Alert from "$lib/components/ui/alert";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Switch } from "$lib/components/ui/switch";
  import { FormError } from "$lib/forms";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import {
    confirmSetupTestAlertReceived,
    getSetupTestAlert,
    saveAlertSetup,
    sendSetupTestAlert,
    setUrgentLowRecipient,
  } from "$api/generated/setupAlerts.generated.remote";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import {
    AlertRouting,
    ChannelType,
    StarterAlertKind,
    type AlertSetupStatus,
    type AlertSetupTest,
  } from "$api";
  import ChannelsSection from "$lib/components/alerts/ChannelsSection.svelte";
  import { findChannelMeta } from "$lib/components/alerts/channelMeta";
  import { toChannelDef, toChannelRequest, type ChannelDef } from "$lib/components/alerts/types";
  import {
    getNotificationPermission,
    requestNotificationPermission,
    showNotification,
  } from "$lib/audio/alarm-sounds";
  import { getUnitLabel } from "$lib/utils/formatting";
  import type { PatientVoice } from "$lib/onboarding/patient-voice.svelte";

  let { status: initial, voice }: { status: AlertSetupStatus; voice: PatientVoice } = $props();

  // The form edits its own copy; the server's answer after each save replaces it.
  const loaded = untrack(() => initial);
  let status = $state(loaded);
  let rules = $state(copyRules(loaded));
  let elsewhere = $state(!loaded.toThisDevice);
  let channels = $state<ChannelDef[]>((loaded.channels ?? []).map(toChannelDef));

  let test = $state<AlertSetupTest | null>(null);
  let answer = $state<"asking" | "no" | "yes" | null>(null);
  let busy = $state(false);
  let error = $state<string | undefined>(undefined);
  let permission = $state(getNotificationPermission());

  const unitLabel = $derived(getUnitLabel(status.glucoseUnits === "mmol" ? "mmol" : "mg/dl"));
  const urgentLowOff = $derived(rules.find((r) => r.kind === StarterAlertKind.UrgentLow)?.isEnabled === false);
  const anyOn = $derived(rules.some((r) => r.isEnabled));

  function copyRules(from: AlertSetupStatus) {
    return (from.rules ?? []).map((r) => ({
      kind: r.kind!,
      isEnabled: r.isEnabled ?? true,
      threshold: r.threshold ?? undefined,
    }));
  }

  function label(kind: StarterAlertKind): string {
    switch (kind) {
      case StarterAlertKind.UrgentLow:
        return "Urgent low";
      case StarterAlertKind.Low:
        return "Low";
      case StarterAlertKind.High:
        return "High";
      case StarterAlertKind.NoReadings:
        return "No readings for 20 minutes";
    }
  }

  function direction(kind: StarterAlertKind): string | null {
    if (kind === StarterAlertKind.High) return "Above";
    if (kind === StarterAlertKind.NoReadings) return null;
    return "Below";
  }

  function channelLabel(type: ChannelType | undefined): string {
    if (type === ChannelType.InApp && !elsewhere) return "This device";
    return findChannelMeta(type)?.label ?? String(type);
  }

  async function run(action: () => Promise<void>, fallback: string) {
    busy = true;
    error = undefined;
    try {
      await action();
    } catch (err) {
      error = describeSubmitError(err, fallback);
    } finally {
      busy = false;
    }
  }

  async function save() {
    status = await saveAlertSetup({
      rules: rules.map((r) => ({ ...r, threshold: r.threshold ?? undefined })),
      channels: elsewhere ? channels.map(toChannelRequest) : undefined,
    });
    rules = copyRules(status);
  }

  const saveOnly = () => run(save, "We couldn't save your alerts.");

  const sendTest = () =>
    run(async () => {
      if (!elsewhere && permission === "default") permission = await requestNotificationPermission();
      await save();
      test = await sendSetupTestAlert();
      answer = "asking";
      await followTest(test.instanceId!);
    }, "We couldn't send a test alert.");

  /** Follows the test until nothing is still sending, raising it here once this device's copy is sent. */
  async function followTest(instanceId: string) {
    let shown = false;
    for (let i = 0; i < 20; i++) {
      const current = await getSetupTestAlert(instanceId).run();
      if (test?.instanceId !== instanceId) return;
      test = current;
      const here = current.deliveries?.find((d) => d.channelType === ChannelType.InApp);
      if (!elsewhere && !shown && here?.status === "delivered") {
        shown = true;
        showNotification(
          `Test alert: ${current.ruleName}`,
          "This is how Nocturne alerts look on this device.",
          "setup-test-alert",
          false
        );
      }
      if (!current.deliveries?.some((d) => d.status === "pending")) return;
      await new Promise((r) => setTimeout(r, 1500));
    }
  }

  const confirmArrived = () =>
    run(async () => {
      status = await confirmSetupTestAlertReceived(test!.instanceId!);
      answer = "yes";
      await getSetupHub().refresh();
    }, "We couldn't record that.");

  const allowNotifications = async () => {
    permission = await requestNotificationPermission();
  };

  const toggleMember = (subjectId: string, alerted: boolean) =>
    run(async () => {
      status = await setUrgentLowRecipient({ subjectId, request: { alerted } });
    }, "We couldn't change that.");
</script>

{#if status.routing === AlertRouting.LeftForRecipient}
  <Alert.Root data-testid="alerts-left-for-recipient">
    <Info />
    <Alert.Description>The person you hand this over to will choose where alerts go</Alert.Description>
  </Alert.Root>
{:else}
  <div class="flex flex-col gap-8">
    {#if status.routing === AlertRouting.ToYouAsCaregiver}
      <p class="text-sm text-muted-foreground" data-testid="alerts-caregiver-note">
        {#if voice.kind === "named"}
          Alerts about {voice.name}'s glucose come to you, including overnight.
        {:else}
          Alerts come to you, including overnight.
        {/if}
      </p>
    {/if}

    <section class="flex flex-col gap-3">
      <h2 class="text-base font-medium">When to alert</h2>
      <p class="text-sm text-muted-foreground">
        These are common starting points. Set them to what you and your care team agreed.
      </p>
      <ul class="flex flex-col divide-y rounded-lg border">
        {#each rules as rule (rule.kind)}
          {@const dir = direction(rule.kind)}
          <li class="flex flex-wrap items-center gap-3 px-4 py-3" data-testid="starter-rule-{rule.kind}">
            <Switch id="rule-{rule.kind}" bind:checked={rule.isEnabled} aria-label={label(rule.kind)} />
            <Label for="rule-{rule.kind}" class="min-w-32 flex-1">{label(rule.kind)}</Label>
            {#if dir}
              <span class="flex items-center gap-2 text-sm text-muted-foreground">
                {dir}
                <Input
                  type="number"
                  size="sm"
                  class="w-20"
                  step={status.glucoseUnits === "mmol" ? 0.1 : 1}
                  aria-label="{label(rule.kind)} threshold"
                  disabled={!rule.isEnabled}
                  bind:value={rule.threshold}
                />
                {unitLabel}
              </span>
            {/if}
          </li>
        {/each}
      </ul>
      {#if urgentLowOff}
        <p class="flex items-center gap-2 text-sm text-warning" data-testid="urgent-low-off-note">
          <TriangleAlert class="h-4 w-4" />
          You won't be alerted to urgent lows
        </p>
      {/if}
    </section>

    <section class="flex flex-col gap-3">
      <h2 class="text-base font-medium">Where alerts go</h2>
      {#if elsewhere}
        <ChannelsSection bind:channels />
        <Button variant="link" size="inline" class="self-start" onclick={() => (elsewhere = false)}>
          Send to this device instead
        </Button>
      {:else}
        <div class="flex items-start gap-3 rounded-lg border px-4 py-3" data-testid="this-device">
          <BellRing class="mt-0.5 h-4 w-4 text-primary" />
          <div class="flex flex-col gap-0.5">
            <span class="text-sm font-medium">This device</span>
            <span class="text-sm text-muted-foreground">
              Notifications in this browser. They only arrive while Nocturne is open here.
            </span>
          </div>
        </div>
        <Button variant="link" size="inline" class="self-start" onclick={() => (elsewhere = true)}>
          Send somewhere else
        </Button>
      {/if}
    </section>

    <section class="flex flex-col gap-3" data-testid="alerts-test">
      {#if answer === "yes" || (status.verified && answer === null)}
        <p class="flex items-center gap-2 text-sm text-success" data-testid="alerts-verified">
          <Check class="h-4 w-4" />
          The test alert arrived. Alerts are set up.
        </p>
      {:else if answer === "asking" && test}
        <p class="text-base font-medium">Did it arrive?</p>
        <div class="flex gap-2">
          <Button disabled={busy} onclick={confirmArrived}>Yes</Button>
          <Button variant="outline" disabled={busy} onclick={() => (answer = "no")}>No</Button>
        </div>
      {:else if answer === "no" && test}
        <div class="flex flex-col gap-3 rounded-lg border px-4 py-3" data-testid="alerts-troubleshooting">
          <p class="text-sm font-medium">Things to check</p>
          <ul class="flex list-disc flex-col gap-2 pl-5 text-sm text-muted-foreground">
            {#if !elsewhere}
              <li data-testid="troubleshoot-permission">
                {#if permission === "granted"}
                  Notifications are allowed in this browser.
                {:else if permission === "denied"}
                  Notifications are blocked for this site. Allow them in your browser's site settings.
                {:else if permission === "unsupported"}
                  This browser can't show notifications. Try another browser, or send alerts somewhere else.
                {:else}
                  Notifications aren't allowed in this browser yet.
                  <Button variant="link" size="inline" onclick={allowNotifications}>
                    Allow notifications
                  </Button>
                {/if}
              </li>
            {/if}
            <li>Check that Do Not Disturb or Focus is off on the device you expected it on.</li>
            {#each test.deliveries ?? [] as delivery, i (i)}
              <li data-testid="troubleshoot-delivery">
                {channelLabel(delivery.channelType)}:
                {#if delivery.status === "delivered"}
                  Nocturne sent it.
                {:else if delivery.status === "failed"}
                  Nocturne couldn't send it{delivery.lastError ? ` (${delivery.lastError})` : ""}.
                {:else}
                  still sending.
                {/if}
              </li>
            {/each}
          </ul>
        </div>
      {/if}

      <div class="flex flex-wrap items-center gap-3">
        {#if answer !== "asking"}
          <Button disabled={busy || !anyOn} onclick={sendTest}>
            {test ? "Send another test" : "Send a test alert"}
          </Button>
        {/if}
        {#if status.saved}
          <Button variant="outline" disabled={busy} onclick={saveOnly}>Save changes</Button>
        {/if}
        <FormError issues={error} />
      </div>
      {#if !anyOn}
        <p class="text-sm text-muted-foreground">Switch on at least one alert to send a test.</p>
      {/if}
    </section>

    {#if status.routing === AlertRouting.ToYou && status.saved}
      <section class="flex flex-col gap-3" data-testid="alerts-someone-else">
        <h2 class="text-base font-medium">Alert someone else to urgent lows</h2>
        {#if (status.members ?? []).length > 0}
          <ul class="flex flex-col divide-y rounded-lg border">
            {#each status.members ?? [] as member (member.subjectId)}
              <li class="flex items-center gap-3 px-4 py-3">
                <Switch
                  id="member-{member.subjectId}"
                  checked={member.alertedToUrgentLows ?? false}
                  disabled={busy}
                  onCheckedChange={(on) => toggleMember(member.subjectId!, on)}
                />
                <Label for="member-{member.subjectId}">{member.name}</Label>
              </li>
            {/each}
          </ul>
          <p class="text-sm text-muted-foreground">They get urgent low alerts on their own account.</p>
        {:else}
          <p class="text-sm text-muted-foreground">
            Invite them from Sharing. Once they join, they appear here to add.
          </p>
          <Button variant="outline" class="self-start" href={resolve("/(unauthenticated)/setup/(guided)/[item]", { item: "sharing" })}>
            Go to Sharing
            <ArrowRight class="h-4 w-4" />
          </Button>
        {/if}
      </section>
    {/if}

    <div class="flex flex-wrap gap-x-6 gap-y-2 text-sm">
      <a class="text-primary underline underline-offset-4" href={resolve("/(authenticated)/alerts/dnd")}>
        Set quiet hours
      </a>
      <a class="text-primary underline underline-offset-4" href={resolve("/(authenticated)/alerts")}>
        Open the full rule builder
      </a>
    </div>
  </div>
{/if}
