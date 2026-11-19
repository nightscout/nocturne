<script lang="ts">
  import { untrack } from "svelte";
  import { expoOut } from "svelte/easing";
  import { prefersReducedMotion } from "svelte/motion";
  import { fly } from "svelte/transition";
  import { beforeNavigate, goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import {
    createDefinition,
    getTemplates,
  } from "#lib/api/generated/trackers.generated.remote.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import WizardProgress from "#lib/components/wizard/WizardProgress.svelte";
  import { newDraftFrom, type TrackerDraft } from "#lib/components/trackers/draft.js";
  import TrackerDefinitionInputs from "#lib/components/trackers/TrackerDefinitionInputs.svelte";
  import TrackerRemindersStep from "#lib/components/trackers/wizard/TrackerRemindersStep.svelte";
  import TrackerSettingsStep from "#lib/components/trackers/wizard/TrackerSettingsStep.svelte";
  import TrackerTypeStep from "#lib/components/trackers/wizard/TrackerTypeStep.svelte";
  import {
    templateLabel,
    templatesFrom,
    type TrackerTemplateId,
  } from "#lib/components/trackers/wizard/templates.svelte.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { FormError, toastSaved, useSubmission } from "#lib/forms/index.js";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import X from "@lucide/svelte/icons/x";

  const TYPE = 0;
  const SETTINGS = 1;
  const REMINDERS = 2;

  const steps = $derived([
    { id: "type", label: "Type" },
    { id: "settings", label: "Settings" },
    { id: "reminders", label: "Reminders" },
  ]);

  const templatesQuery = getTemplates();
  const templates = $derived(templatesFrom(templatesQuery.current ?? []));

  let template = $state<TrackerTemplateId | null>(null);
  let draft = $state<TrackerDraft | null>(null);

  // The furthest step reached. A step past it cannot be opened from the browser's history.
  let reached = $state(TYPE);
  let step = $state(TYPE);
  // Which way the last move went: a step ahead comes in from the right, a step back from the left.
  let direction = $state(1);

  function show(index: number): void {
    if (index === step) return;
    direction = index > step ? 1 : -1;
    step = index;
  }

  // Each step is an entry in the browser's history, so Back on a phone goes to the step before.
  $effect(() => {
    const wanted = page.state.trackerWizardStep;
    untrack(() => {
      show(typeof wanted === "number" ? Math.max(TYPE, Math.min(wanted, reached)) : TYPE);
    });
  });

  let formElement = $state<HTMLFormElement | null>(null);

  function open(index: number): void {
    // A step's fields leave the page with it, so what they refuse has to stop the move ahead.
    if (index > step && formElement && !formElement.reportValidity()) return;
    reached = Math.max(reached, index);
    show(index);
    void goto(resolve("/(authenticated)/trackers/new"), {
      state: { trackerWizardStep: index },
      shallow: true,
    });
    window.scrollTo({ top: 0 });
  }

  function back(): void {
    // The step before is the history entry before, unless this step was not reached through one.
    if (page.state.trackerWizardStep === step) history.back();
    else show(Math.max(TYPE, step - 1));
  }

  function pickType(id: TrackerTemplateId): void {
    const picked = templates.find((t) => t.id === id);
    if (!picked) return;
    if (template !== id) {
      template = id;
      draft = newDraftFrom(picked.definition);
      // The steps after this one were set for a different type.
      reached = TYPE;
    }
    open(SETTINGS);
  }

  /** Why the current step cannot be left yet, or `null` when it can. */
  const blocked = $derived.by<string | null>(() => {
    if (!draft) return "Choose a tracker type.";
    if (step >= SETTINGS && draft.name.trim() === "") return "Enter a name.";
    return null;
  });

  // The steps differ in height, and a scrollbar that comes and goes moves the page sideways.
  $effect(() => {
    const root = document.documentElement;
    root.classList.add("[scrollbar-gutter:stable]");
    return () => root.classList.remove("[scrollbar-gutter:stable]");
  });

  // ---- Create ------------------------------------------------------------

  const form = createDefinition.for("wizard");
  const submission = useSubmission({ fallback: "Could not create the tracker. Try again." });
  let submitted = $state(false);
  let created = $state(false);

  async function submitStep(submit: () => Promise<boolean>): Promise<void> {
    if (blocked) return;
    // Enter in a field submits the form; before the last step that is a move to the next one.
    if (step < REMINDERS) {
      open(step + 1);
      return;
    }
    submitted = true;
    await submission.run(submit, async () => {
      created = true;
      const id = form.result?.id;
      toastSaved("The tracker is ready.");
      // The trackers page asks when the new tracker started.
      await goto(resolve("/(authenticated)/trackers"), {
        state: id ? { startTrackerId: id } : {},
      });
    });
  }

  beforeNavigate((navigation) => {
    // A move between steps keeps the page; only a move off it loses the tracker.
    if (!draft || created || navigation.shallow) return;
    // A reload or a closed tab cannot show a prompt of ours; cancelling shows the browser's.
    if (navigation.willUnload || !confirm("Leave without creating this tracker?")) {
      navigation.cancel();
    }
  });

  function heading(index: number): string {
    switch (index) {
      case SETTINGS:
        return "Name and settings";
      case REMINDERS:
        return "When does it remind you?";
      default:
        return "What do you want to track?";
    }
  }
</script>

<svelte:head>
  <title>New tracker · Nocturne</title>
</svelte:head>

<div class="@container mx-auto max-w-2xl p-3 @md:p-6 max-md:pb-28">
  <header class="mb-6 flex min-h-12 items-center gap-2">
    <Button
      variant="ghost"
      size="icon-touch"
      href={resolve("/(authenticated)/trackers")}
      aria-label="Close and go back to trackers"
    >
      <X class="size-4" />
    </Button>
    <div class="min-w-0">
      <h1 class="truncate text-2xl font-bold">New tracker</h1>
      <!-- The line is there on every step, so the type's name arrives without moving the page. -->
      <p class="min-h-5 truncate text-sm text-muted-foreground" data-testid="tracker-wizard-summary">
        {#if template && step > TYPE}{templateLabel(template)}{/if}
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

  <form bind:this={formElement} {...form.enhance(({ submit }) => submitStep(submit))}>
    {#if draft}
      <TrackerDefinitionInputs fields={form.fields} {draft} />
    {/if}

    {#key step}
      <div
        class="mt-8 space-y-6"
        in:fly={{
          x: direction * 20,
          duration: prefersReducedMotion.current ? 0 : 260,
          easing: expoOut,
        }}
      >
        <h2 class="text-lg font-semibold leading-tight" data-testid="tracker-wizard-heading">
          {heading(step)}
        </h2>

        {#if step === TYPE || !draft || !template}
          {#if templatesQuery.error}
            <p
              class="rounded-md border border-destructive/40 bg-destructive/5 p-3 text-sm text-destructive"
              role="alert"
            >
              {remoteErrorMessage(templatesQuery.error, "Could not load the tracker types.")}
            </p>
          {:else}
            <TrackerTypeStep
              selected={template}
              onPick={pickType}
              {templates}
              loading={templatesQuery.current === undefined}
            />
          {/if}
        {:else if step === SETTINGS}
          {#key template}
            <TrackerSettingsStep
              bind:draft
              choosesMode={template === "custom"}
              namePlaceholder={templateLabel(template)}
            />
          {/key}
        {:else}
          <TrackerRemindersStep bind:draft />
        {/if}

        <FormError
          issues={submission.error ?? (submitted ? form.fields.allIssues() : undefined)}
          focusOnShow
        />
      </div>
    {/key}

    <!-- md+: at the end of the step. Below md: pinned to the bottom, under the thumb. -->
    {#if draft}
      <div
        class="mt-8 flex flex-wrap items-center gap-x-3 gap-y-2 max-md:fixed max-md:inset-x-0 max-md:bottom-0 max-md:z-40 max-md:mt-0 max-md:border-t max-md:border-border max-md:bg-background max-md:px-4 max-md:py-3 md:border-t md:pt-6"
        data-testid="tracker-wizard-nav"
      >
        {#if step > TYPE}
          <Button type="button" variant="outline" size="touch" onclick={back}>
            <ArrowLeft aria-hidden="true" />
            Back
          </Button>
        {/if}
        {#if step > TYPE && blocked}
          <p
            class="min-w-0 flex-1 text-right text-sm text-muted-foreground max-md:order-first max-md:basis-full max-md:text-left"
            role="status"
          >
            {blocked}
          </p>
        {:else}
          <span class="flex-1"></span>
        {/if}
        {#if step === REMINDERS}
          <Button type="submit" size="touch" disabled={!!form.pending || blocked !== null}>
            {#if form.pending}
              <Loader2 class="animate-spin" aria-hidden="true" />
            {/if}
            Create tracker
          </Button>
        {:else}
          <Button
            type="button"
            size="touch"
            onclick={() => open(step + 1)}
            disabled={step > TYPE && blocked !== null}
          >
            Next
            <ArrowRight aria-hidden="true" />
          </Button>
        {/if}
      </div>
    {/if}
  </form>
</div>
