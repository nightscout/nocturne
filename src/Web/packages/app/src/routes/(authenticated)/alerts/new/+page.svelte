<script lang="ts">
  import { untrack } from "svelte";
  import { expoOut } from "svelte/easing";
  import { prefersReducedMotion } from "svelte/motion";
  import { fly } from "svelte/transition";
  import { beforeNavigate, goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { toastSaved } from "#lib/forms/index.js";
  import {
    createRule,
    getPresets,
    getRules,
  } from "#lib/api/generated/alertRules.generated.remote.js";
  import { getDefinitions } from "#lib/api/generated/trackers.generated.remote.js";
  import type { ReplayRuleDefinition } from "@nocturne/api-client";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { conditionIssuesMessage } from "#lib/components/alerts/conditionIssues.svelte.js";
  import { oppositeCondition } from "#lib/components/alerts/oppositeCondition.js";
  import ReplayPanel from "#lib/components/alerts/ReplayPanel.svelte";
  import {
    buildBody,
    ensureCompositeRoot,
    nodeFromApi,
    parseRule,
    stripEditorFields,
    validateChannels,
    type ConditionNode,
    type RuleEditorState,
  } from "#lib/components/alerts/types.js";
  import CheckStep from "#lib/components/alerts/wizard/CheckStep.svelte";
  import DeliveryStep from "#lib/components/alerts/wizard/DeliveryStep.svelte";
  import SettingsStep from "#lib/components/alerts/wizard/SettingsStep.svelte";
  import TypeStep from "#lib/components/alerts/wizard/TypeStep.svelte";
  import WizardProgress from "#lib/components/wizard/WizardProgress.svelte";
  import {
    draftCondition,
    draftIssue,
    newDraft,
    suggestName,
    templateLabel,
    templatesFrom,
    templateTrackers,
    trackerLeaf,
    type AlertDraft,
    type TemplateId,
  } from "#lib/components/alerts/wizard/templates.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import PlayCircle from "@lucide/svelte/icons/circle-play";
  import X from "@lucide/svelte/icons/x";

  const TYPE = 0;
  const SETTINGS = 1;
  const DELIVERY = 2;
  const CHECK = 3;

  const steps = $derived([
    { id: "type", label: "Type" },
    { id: "settings", label: "Settings" },
    { id: "delivery", label: "Delivery" },
    { id: "check", label: "Check" },
  ]);

  const rulesQuery = getRules();
  const availableRules = $derived(
    (rulesQuery.current ?? []).map((r) => ({ id: r.id ?? "", name: r.name ?? "(unnamed)" })),
  );

  // The wizard edits the same state the rule editor does. A new rule's defaults
  // (the In-app destination, sound and snooze settings) come from there.
  let editor = $state<RuleEditorState>(parseRule(null));
  let draft = $state<AlertDraft | null>(null);
  let nameEdited = $state(false);

  const presetsQuery = getPresets();
  const templates = $derived(templatesFrom(presetsQuery.current ?? []));

  const trackersQuery = getDefinitions({});
  const trackerDefinitions = $derived(trackersQuery.current ?? []);
  const trackers = $derived(draft ? templateTrackers(draft.def, trackerDefinitions) : []);

  // A type chosen before the trackers loaded moves to its tracker when they arrive, unless the
  // person has changed its settings by then: those are theirs to keep.
  $effect(() => {
    const tracker = trackers[0];
    untrack(() => {
      if (!tracker || !draft?.leaf || draft.leaf.tracker_age) return;
      const built = draft.def.build();
      const untouched =
        built !== null &&
        JSON.stringify(stripEditorFields(draft.leaf)) === JSON.stringify(stripEditorFields(built));
      if (!untouched) return;
      draft.leaf = trackerLeaf(draft.def, tracker);
    });
  });

  // The furthest step reached. A step past it cannot be opened from the browser's history.
  let reached = $state(TYPE);
  let step = $state(TYPE);
  // Which way the last move went: a step ahead comes in from the right, a step back from the left.
  let direction = $state(1);

  // Each step is drawn again, which removes the control that had the focus. The focus goes to the
  // new step's heading, so a keyboard continues from there and a screen reader says the step.
  let headingEl = $state<HTMLElement | null>(null);
  let moved = false;

  $effect(() => {
    if (headingEl && moved) headingEl.focus({ preventScroll: true });
  });

  function show(index: number): void {
    if (index === step) return;
    direction = index > step ? 1 : -1;
    step = index;
    moved = true;
  }

  // Each step is an entry in the browser's history, so Back on a phone goes to the step before.
  $effect(() => {
    const wanted = page.state.alertWizardStep;
    untrack(() => {
      show(typeof wanted === "number" ? Math.max(TYPE, Math.min(wanted, reached)) : TYPE);
    });
  });

  const condition = $derived<ConditionNode | null>(draft ? draftCondition(draft) : null);
  const suggestedName = $derived(draft ? suggestName(draft, trackerDefinitions) : "");
  const name = $derived(nameEdited ? editor.name.trim() : suggestedName);

  // A type that closes on the opposite of its condition gets that as its resolve condition. The
  // API works the opposite out. It is asked when the last step opens: the condition is set on the
  // steps before it.
  let autoResolve = $state<ConditionNode | null>(null);
  let autoResolveAsked: Promise<ConditionNode | null> = Promise.resolve(null);
  // The history check plays the alert with its closing condition, so it waits for the answer.
  let autoResolveAnswered = $state(false);

  function askAutoResolve(): Promise<ConditionNode | null> {
    return draft?.def.autoResolve ? oppositeCondition(condition) : Promise.resolve(null);
  }

  $effect(() => {
    if (step !== CHECK) return;
    untrack(() => {
      const asked = askAutoResolve();
      autoResolveAsked = asked;
      autoResolve = null;
      autoResolveAnswered = false;
      asked.then(
        (closing) => {
          if (autoResolveAsked !== asked) return;
          autoResolve = closing;
          autoResolveAnswered = true;
        },
        // The last step shows no closing condition, and Create asks again.
        () => {
          if (autoResolveAsked === asked) autoResolveAnswered = true;
        },
      );
    });
  });

  function rule(closing: ConditionNode | null = autoResolve): RuleEditorState {
    return {
      ...editor,
      name,
      condition,
      autoResolveEnabled: closing !== null,
      autoResolveCondition: closing,
    };
  }

  function open(index: number): void {
    const from = step;
    reached = Math.max(reached, index);
    show(index);
    void goto(resolve("/(authenticated)/alerts/new"), {
      state: { alertWizardStep: index, alertWizardFrom: from },
      shallow: true,
    });
    window.scrollTo({ top: 0 });
  }

  function back(): void {
    // The step before is the history entry before only when this step was opened from it. A step
    // opened from a later one (a jump back in the indicator) has that later step behind it.
    const before = Math.max(TYPE, step - 1);
    if (page.state.alertWizardStep === step && page.state.alertWizardFrom === before) history.back();
    else open(before);
  }

  function pickType(id: TemplateId): void {
    const template = templates.find((t) => t.id === id);
    if (!template) return;
    if (draft?.template !== id) {
      draft = newDraft(template, trackerDefinitions);
      editor.severity = template.severity;
      // The steps after this one were set for a different type.
      reached = TYPE;
    }
    open(SETTINGS);
  }

  function deliveryIssue(): string | null {
    if (editor.channels.length === 0) return "Add at least one place for the alert to go.";
    return validateChannels(editor.channels);
  }

  /** Why the current step cannot be left yet, or `null` when it can. */
  const blocked = $derived.by<string | null>(() => {
    if (!draft) return "Choose an alert type.";
    if (step === SETTINGS) return draftIssue(draft);
    if (step === DELIVERY) return deliveryIssue();
    // The last step answers for the ones before it: nothing they refuse is created.
    if (step === CHECK) {
      return draftIssue(draft) ?? deliveryIssue() ?? (name === "" ? "Enter a name." : null);
    }
    return null;
  });


  let saving = $state(false);
  let created = $state(false);
  let error = $state<string | null>(null);

  async function create(): Promise<void> {
    saving = true;
    error = null;
    try {
      // An alert is not created without the closing condition its type has.
      const closing = await autoResolveAsked.catch(askAutoResolve);
      await createRule(buildBody(rule(closing)));
      created = true;
      toastSaved("The alert is on.");
      await goto(resolve("/(authenticated)/alerts"));
    } catch (e) {
      error =
        conditionIssuesMessage(e) ?? describeSubmitError(e, "Could not create the alert. Try again.");
    } finally {
      saving = false;
    }
  }

  beforeNavigate((navigation) => {
    // A move between steps keeps the page; only a move off it loses the alert.
    if (!draft || created || navigation.shallow) return;
    // A reload or a closed tab cannot show a prompt of ours; cancelling shows the browser's.
    if (navigation.willUnload || !confirm("Leave without creating this alert?")) {
      navigation.cancel();
    }
  });

  // An id for the unsaved alert, so the replay can list it beside the saved ones.
  const draftId = newGuid();
  let replayOpen = $state(false);
  let replayDay = $state<string | undefined>(undefined);

  function newGuid(): string {
    if (typeof crypto.randomUUID === "function") return crypto.randomUUID();
    // randomUUID exists only in secure contexts; a LAN install served over plain http has none.
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
    return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
  }

  function replayRule(): ReplayRuleDefinition {
    const body = buildBody(rule());
    return {
      id: draftId,
      name: body.name,
      conditionType: body.conditionType,
      conditionParams:
        body.conditionParams == null ? undefined : JSON.stringify(body.conditionParams),
      severity: body.severity,
      allowThroughDnd: body.allowThroughDnd,
      autoResolveEnabled: body.autoResolveEnabled,
      autoResolveParams: body.autoResolveParams ? JSON.stringify(body.autoResolveParams) : undefined,
    };
  }

  /** The alert's tree as the replay's rule list reads it: with an id on each condition. */
  function replayTree(): ConditionNode | undefined {
    const body = buildBody(rule());
    const node = nodeFromApi(body.conditionType, body.conditionParams);
    return node ? ensureCompositeRoot(node) : undefined;
  }

  function openReplay(day: Date): void {
    const pad = (n: number) => String(n).padStart(2, "0");
    replayDay = `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}`;
    replayOpen = true;
  }

  function heading(index: number): string {
    switch (index) {
      case SETTINGS:
        return "When does it alert?";
      case DELIVERY:
        return "How does it reach you?";
      case CHECK:
        return "Check the alert";
      default:
        return "What is the alert for?";
    }
  }
</script>

<svelte:head>
  <title>New alert · Nocturne</title>
</svelte:head>

<!-- The steps differ in height, and a scrollbar that comes and goes moves the page sideways, so
     the document keeps the scrollbar's gutter while this page is on it. -->
<div class="@container mx-auto max-w-2xl p-3 @md:p-6 max-md:pb-28 [:root:has(&)]:[scrollbar-gutter:stable]">
  <header class="mb-6 flex min-h-12 items-center gap-2">
    <Button
      variant="ghost"
      size="icon-touch"
      href={resolve("/(authenticated)/alerts")}
      aria-label="Close and go back to alerts"
    >
      <X class="size-4" />
    </Button>
    <div class="min-w-0">
      <h1 class="truncate text-2xl font-bold">New alert</h1>
      <!-- The line is there on every step, so the type's name arrives without moving the page. -->
      <p class="min-h-5 truncate text-sm text-muted-foreground" data-testid="alert-wizard-summary">
        {#if draft && step > TYPE}{templateLabel(draft.template)}{/if}
      </p>
    </div>
  </header>

  <WizardProgress
    {steps}
    current={step}
    {reached}
    locked={step > TYPE && blocked !== null}
    onJump={open}
  />

  {#key step}
  <div
    class="mt-8 space-y-6"
    in:fly={{
      x: direction * 20,
      duration: prefersReducedMotion.current ? 0 : 260,
      easing: expoOut,
    }}
  >
    <h2
      bind:this={headingEl}
      tabindex="-1"
      class="text-lg font-semibold leading-tight outline-none"
      data-testid="alert-wizard-heading"
    >
      {heading(step)}
    </h2>

    {#if presetsQuery.error}
      <p
        class="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
        role="alert"
      >
        Could not load the alert types. Load the page again.
      </p>
    {/if}

    {#if step === TYPE || !draft}
      <TypeStep
            selected={draft?.template ?? null}
            onPick={pickType}
            {templates}
            loading={presetsQuery.current === undefined && !presetsQuery.error}
            {trackerDefinitions}
          />
    {:else if step === SETTINGS}
      {#key draft.template}
        <SettingsStep bind:draft {availableRules} {trackers} />
      {/key}
    {:else if step === DELIVERY}
      <DeliveryStep bind:editor />
    {:else if condition}
      <CheckStep
        bind:editor
        bind:nameEdited
        {condition}
        {autoResolve}
        {autoResolveAnswered}
        {suggestedName}
        rule={replayRule}
        onOpenReplay={openReplay}
      />
    {/if}

    {#if error}
      <p
        class="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
        role="alert"
      >
        {error}
      </p>
    {/if}
  </div>
  {/key}

  <!-- md+: at the end of the step. Below md: pinned to the bottom, under the thumb. -->
  {#if draft}
    <div
      class="mt-8 flex flex-wrap items-center gap-x-3 gap-y-2 max-md:fixed max-md:inset-x-0 max-md:bottom-0 max-md:z-40 max-md:mt-0 max-md:border-t max-md:border-border max-md:bg-background max-md:px-4 max-md:py-3 md:border-t md:pt-6"
      data-testid="alert-wizard-nav"
    >
      {#if step > TYPE}
        <Button variant="outline" size="touch" onclick={back}>
          <ArrowLeft aria-hidden="true" />
          Back
        </Button>
      {/if}
      {#if step > TYPE && blocked}
        <p class="min-w-0 flex-1 text-right text-sm text-muted-foreground max-md:order-first max-md:basis-full max-md:text-left" role="status">
          {blocked}
        </p>
      {:else}
        <span class="flex-1"></span>
      {/if}
      {#if step === CHECK}
        <Button size="touch" onclick={create} disabled={saving || blocked !== null}>
          {#if saving}
            <Loader2 class="animate-spin" aria-hidden="true" />
          {/if}
          Create alert
        </Button>
      {:else}
        <Button size="touch" onclick={() => open(step + 1)} disabled={step > TYPE && blocked !== null}>
          Next
          <ArrowRight aria-hidden="true" />
        </Button>
      {/if}
    </div>
  {/if}
</div>

<Dialog.Root bind:open={replayOpen}>
  <!-- eslint-disable-next-line shadcn/no-restyle -- the replay workbench fills the viewport; its bordered header and scrolling body pad themselves, so the dialog cannot -->
  <Dialog.Content class="flex h-[90vh] max-h-[90vh] w-[calc(100vw-1rem)] max-w-6xl flex-col gap-0 overflow-hidden p-0 sm:w-[95vw] sm:max-w-7xl">
    <Dialog.Header class="border-b px-4 py-3">
      <Dialog.Title class="flex items-center gap-2">
        <PlayCircle class="h-4 w-4" /> Replay
      </Dialog.Title>
      <Dialog.Description>
        This alert and your other alerts, played against the glucose of that day. Nothing is sent.
      </Dialog.Description>
    </Dialog.Header>

    <div class="@container min-h-0 flex-1 overflow-hidden p-3 @md:p-4">
      <ReplayPanel
        initialCustomDate={replayDay}
        rule={replayRule}
        editingRuleId={draftId}
        editingTree={replayTree()}
        availableRules={rulesQuery.current ?? []}
      />
    </div>
  </Dialog.Content>
</Dialog.Root>
