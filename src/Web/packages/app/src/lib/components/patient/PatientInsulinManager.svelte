<script lang="ts">
  import { formatMediumDate } from "$lib/utils/formatting";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Dialog from "$lib/components/ui/dialog";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import {
    Syringe,
    Plus,
    Pencil,
    Trash2,
    Save,
    Loader2,
  } from "lucide-svelte";
  import {
    type PatientInsulin,
    type InsulinFormulation,
    InsulinCategory,
    InsulinRole,
  } from "$api";
  import {
    insulinCategoryLabels,
    insulinRoleLabels,
  } from "./labels";
  import { InsulinListState } from "./state.svelte";
  import InsulinFormFields from "./InsulinFormFields.svelte";

  interface Props {
    /** "inline" = wizard-style card forms, "dialog" = settings-style dialog CRUD */
    variant?: "inline" | "dialog";
  }

  let { variant = "dialog" }: Props = $props();

  const insulinList = new InsulinListState();

  // ── Helpers ──────────────────────────────────────────────────────

  function formatDate(date: Date | string | undefined): string {
    if (!date) return "";
    const d = new Date(date);
    return formatMediumDate(d);
  }

  /** Get formulations matching the selected category */
  function formulationsForCategory(category: string): InsulinFormulation[] {
    if (!category) return [];
    return insulinList.catalog.filter((f) => f.category === category);
  }

  /** Apply formulation defaults to form fields */
  function applyFormulation(formulation: InsulinFormulation) {
    return {
      name: formulation.name ?? "",
      dia: formulation.defaultDia ?? 4.0,
      peak: formulation.defaultPeak ?? 75,
      curve: formulation.curve ?? "rapid-acting",
      concentration: formulation.concentration ?? 100,
    };
  }

  // ── Inline variant state ────────────────────────────────────────

  let showInlineForm = $state(false);
  let inlineCategory = $state<string>("");
  let inlineFormulationId = $state("");
  let inlineName = $state("");
  let inlineDia = $state<number | null>(4.0);
  let inlinePeak = $state<number | null>(75);
  let inlineCurve = $state("rapid-acting");
  let inlineConcentration = $state<number | null>(100);
  let inlineRole = $state<string | InsulinRole>(InsulinRole.Bolus);

  let inlineFormulations = $derived(formulationsForCategory(inlineCategory));

  let canAddInline = $derived(inlineCategory !== "" && inlineName.trim() !== "");

  function onInlineCategoryChange() {
    // Reset formulation when category changes
    inlineFormulationId = "";
    inlineName = "";
    // Set defaults based on category
    const isLongActing = inlineCategory === InsulinCategory.LongActing
      || inlineCategory === InsulinCategory.UltraLongActing;
    inlineRole = isLongActing ? InsulinRole.Basal : InsulinRole.Bolus;
    inlineDia = isLongActing ? 24.0 : 4.0;
    inlinePeak = isLongActing ? 0 : 75;
    inlineCurve = isLongActing ? "bilinear" : "rapid-acting";
  }

  function onInlineFormulationChange() {
    const formulation = insulinList.catalog.find((f) => f.id === inlineFormulationId);
    if (formulation) {
      const defaults = applyFormulation(formulation);
      inlineName = defaults.name;
      inlineDia = defaults.dia;
      inlinePeak = defaults.peak;
      inlineCurve = defaults.curve;
      inlineConcentration = defaults.concentration;
    }
  }

  function resetInlineForm() {
    inlineCategory = "";
    inlineFormulationId = "";
    inlineName = "";
    inlineDia = 4.0;
    inlinePeak = 75;
    inlineCurve = "rapid-acting";
    inlineConcentration = 100;
    inlineRole = InsulinRole.Bolus;
    showInlineForm = false;
  }

  // ── Dialog variant state ────────────────────────────────────────

  let dialogOpen = $state(false);
  let editing = $state<PatientInsulin | null>(null);
  let deleteId = $state<string | null>(null);

  let insulinCategory = $state<string | InsulinCategory>(InsulinCategory.RapidActing);
  let insulinFormulationId = $state("");
  let insulinName = $state("");
  let insulinStartDate = $state("");
  let insulinEndDate = $state("");
  let insulinIsCurrent = $state(true);
  let insulinNotes = $state("");
  let insulinDia = $state<number | null>(4.0);
  let insulinPeak = $state<number | null>(75);
  let insulinCurve = $state("rapid-acting");
  let insulinConcentration = $state<number | null>(100);
  let insulinRole = $state<string | InsulinRole>(InsulinRole.Bolus);
  let insulinIsPrimary = $state(false);

  let dialogFormulations = $derived(formulationsForCategory(insulinCategory as string));

  const activeForm = $derived(editing?.id ? insulinList.updateForm : insulinList.createForm);
  const dialogSaving = $derived(!!insulinList.createForm.pending || !!insulinList.updateForm.pending);
  // updateInsulin's schema is { id, request: PatientInsulinSchema } — fields must nest under
  // "request." for SvelteKit's dot-path form parsing; createInsulin's schema is flat.
  const namePrefix = $derived(editing?.id ? "request." : "");

  function onDialogCategoryChange() {
    insulinFormulationId = "";
    insulinName = "";
    const isLongActing = insulinCategory === InsulinCategory.LongActing
      || insulinCategory === InsulinCategory.UltraLongActing;
    insulinRole = isLongActing ? InsulinRole.Basal : InsulinRole.Bolus;
    insulinDia = isLongActing ? 24.0 : 4.0;
    insulinPeak = isLongActing ? 0 : 75;
    insulinCurve = isLongActing ? "bilinear" : "rapid-acting";
  }

  function onDialogFormulationChange() {
    const formulation = insulinList.catalog.find((f) => f.id === insulinFormulationId);
    if (formulation) {
      const defaults = applyFormulation(formulation);
      insulinName = defaults.name;
      insulinDia = defaults.dia;
      insulinPeak = defaults.peak;
      insulinCurve = defaults.curve;
      insulinConcentration = defaults.concentration;
    }
  }

  function openDialog(insulin?: PatientInsulin) {
    if (insulin) {
      editing = insulin;
      insulinCategory = insulin.insulinCategory ?? InsulinCategory.RapidActing;
      insulinFormulationId = insulin.formulationId ?? "";
      insulinName = insulin.name ?? "";
      insulinStartDate = insulin.startDate
        ? new Date(insulin.startDate).toISOString().split("T")[0]
        : "";
      insulinEndDate = insulin.endDate
        ? new Date(insulin.endDate).toISOString().split("T")[0]
        : "";
      insulinIsCurrent = insulin.isCurrent ?? true;
      insulinNotes = insulin.notes ?? "";
      insulinDia = insulin.dia ?? 4.0;
      insulinPeak = insulin.peak ?? 75;
      insulinCurve = insulin.curve ?? "rapid-acting";
      insulinConcentration = insulin.concentration ?? 100;
      insulinRole = insulin.role ?? InsulinRole.Bolus;
      insulinIsPrimary = insulin.isPrimary ?? false;
    } else {
      editing = null;
      insulinCategory = InsulinCategory.RapidActing;
      insulinFormulationId = "";
      insulinName = "";
      insulinStartDate = "";
      insulinEndDate = "";
      insulinIsCurrent = true;
      insulinNotes = "";
      insulinDia = 4.0;
      insulinPeak = 75;
      insulinCurve = "rapid-acting";
      insulinConcentration = 100;
      insulinRole = InsulinRole.Bolus;
      insulinIsPrimary = false;
    }
    dialogOpen = true;
  }

  async function handleDelete() {
    if (!deleteId) return;
    await insulinList.remove(deleteId);
    deleteId = null;
  }

</script>

{#if variant === "inline"}
  <!-- ── Inline variant (wizard-style) ─────────────────────────── -->

  {#if insulinList.items.length > 0}
    <div class="space-y-3">
      {#each insulinList.items as insulin}
        <Card.Root>
          <Card.Header class="flex flex-row items-center gap-3 py-3">
            <div class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-muted">
              <Syringe class="h-4 w-4" />
            </div>
            <div class="flex-1 min-w-0">
              <Card.Title class="text-sm font-medium">
                {insulin.name || "Unknown Insulin"}
              </Card.Title>
              <Card.Description class="text-xs">
                {insulin.insulinCategory
                  ? (insulinCategoryLabels[insulin.insulinCategory] ?? insulin.insulinCategory)
                  : "Unknown Category"}
                {#if insulin.role}
                  &middot; {insulinRoleLabels[insulin.role] ?? insulin.role}
                {/if}
                {#if insulin.dia}
                  &middot; DIA {insulin.dia}h
                {/if}
                {#if insulin.isPrimary}
                  &middot; Primary
                {/if}
              </Card.Description>
            </div>
            {#if insulin.id}
              <Button
                variant="ghost"
                size="icon"
                class="shrink-0 text-muted-foreground hover:text-destructive"
                onclick={() => insulinList.remove(insulin.id!)}
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            {/if}
          </Card.Header>
        </Card.Root>
      {/each}
    </div>
  {/if}

  {#if showInlineForm}
    <Card.Root>
      <Card.Header>
        <Card.Title class="text-sm">Add an Insulin</Card.Title>
      </Card.Header>
      <form
        {...insulinList.createForm.enhance(async ({ submit }) => {
          await submit();
          if (insulinList.createForm.result) resetInlineForm();
        })}
      >
        <Card.Content class="space-y-4">
          <InsulinFormFields
            bind:category={inlineCategory}
            bind:formulationId={inlineFormulationId}
            bind:name={inlineName}
            bind:role={inlineRole}
            bind:dia={inlineDia}
            bind:peak={inlinePeak}
            bind:concentration={inlineConcentration}
            formulations={inlineFormulations}
            catalog={insulinList.catalog}
            showExtendedFields={false}
            onCategoryChange={onInlineCategoryChange}
            onFormulationChange={onInlineFormulationChange}
          />

          <!-- Hidden fields for form submission -->
          <input type="hidden" name="b:isCurrent" value="on" />
          <input type="hidden" name="n:dia" value={inlineDia} />
          <input type="hidden" name="n:peak" value={inlinePeak} />
          <input type="hidden" name="curve" value={inlineCurve} />
          <input type="hidden" name="n:concentration" value={inlineConcentration} />
          <input type="hidden" name="b:isPrimary" value={insulinList.items.length === 0 ? "on" : ""} />
          {#if inlineFormulationId}
            <input type="hidden" name="formulationId" value={inlineFormulationId} />
          {/if}

          {#each insulinList.createForm.fields.allIssues() ?? [] as issue}
            <p class="text-sm text-destructive">{issue.message}</p>
          {/each}
        </Card.Content>
        <Card.Footer class="flex justify-end gap-2">
          <Button type="button" variant="outline" onclick={resetInlineForm} disabled={!!insulinList.createForm.pending}>
            Cancel
          </Button>
          <Button type="submit" disabled={!canAddInline || !!insulinList.createForm.pending}>
            {insulinList.createForm.pending ? "Adding..." : "Add Insulin"}
          </Button>
        </Card.Footer>
      </form>
    </Card.Root>
  {:else}
    <Button variant="outline" onclick={() => (showInlineForm = true)}>
      <Plus class="h-4 w-4 mr-1" />
      Add Insulin
    </Button>
  {/if}

{:else}
  <!-- ── Dialog variant (settings-style) ───────────────────────── -->

  {#if insulinList.items.length === 0}
    <p class="text-sm text-muted-foreground py-4 text-center">
      No insulins added yet. Add your first insulin to get started.
    </p>
  {:else}
    <div class="space-y-3">
      {#each insulinList.items as insulin}
        <div
          class="flex items-center justify-between rounded-lg border p-3"
        >
          <div class="space-y-1 min-w-0 flex-1">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="font-medium text-sm">
                {insulin.name ?? "Unnamed"}
              </span>
              <Badge variant="secondary" class="text-xs">
                {insulinCategoryLabels[(insulin.insulinCategory ?? "") as InsulinCategory] ??
                  insulin.insulinCategory}
              </Badge>
              {#if insulin.role}
                <Badge variant="outline" class="text-xs">
                  {insulinRoleLabels[insulin.role as InsulinRole] ?? insulin.role}
                </Badge>
              {/if}
              {#if insulin.isCurrent}
                <Badge
                  variant="default"
                  class="text-xs bg-green-600 hover:bg-green-700"
                >
                  Current
                </Badge>
              {/if}
              {#if insulin.isPrimary}
                <Badge
                  variant="default"
                  class="text-xs"
                >
                  Primary
                </Badge>
              {/if}
            </div>
            <div class="flex items-center gap-3 text-xs text-muted-foreground flex-wrap">
              {#if insulin.dia}
                <span>DIA {insulin.dia}h</span>
              {/if}
              {#if insulin.startDate}
                <span>From {formatDate(insulin.startDate)}</span>
              {/if}
              {#if insulin.endDate}
                <span>Until {formatDate(insulin.endDate)}</span>
              {/if}
            </div>
          </div>
          <div class="flex items-center gap-1 ml-2">
            <Button
              variant="ghost"
              size="icon"
              onclick={() => openDialog(insulin)}
            >
              <Pencil class="h-4 w-4" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              onclick={() => (deleteId = insulin.id ?? null)}
            >
              <Trash2 class="h-4 w-4 text-destructive" />
            </Button>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  <!-- Add button for dialog variant -->
  <div class="pt-2">
    <Button size="sm" onclick={() => openDialog()}>
      <Plus class="mr-2 h-4 w-4" />
      Add Insulin
    </Button>
  </div>

  <!-- Insulin Dialog -->
  <Dialog.Root bind:open={dialogOpen}>
    <Dialog.Content class="sm:max-w-lg max-h-[90vh] overflow-y-auto">
      <Dialog.Header>
        <Dialog.Title>
          {editing ? "Edit Insulin" : "Add Insulin"}
        </Dialog.Title>
        <Dialog.Description>
          {editing
            ? "Update the details of this insulin."
            : "Add an insulin to your patient record."}
        </Dialog.Description>
      </Dialog.Header>

      <form
        {...activeForm.enhance(async ({ submit }) => {
          await submit();
          if (activeForm.result) dialogOpen = false;
        })}
      >
        {#if editing?.id}
          <input type="hidden" name="id" value={editing.id} />
        {/if}
        <div class="space-y-4 py-4">
          <InsulinFormFields
            {namePrefix}
            bind:category={insulinCategory}
            bind:formulationId={insulinFormulationId}
            bind:name={insulinName}
            bind:role={insulinRole}
            bind:dia={insulinDia}
            bind:peak={insulinPeak}
            bind:concentration={insulinConcentration}
            bind:startDate={insulinStartDate}
            bind:endDate={insulinEndDate}
            bind:isCurrent={insulinIsCurrent}
            bind:isPrimary={insulinIsPrimary}
            bind:notes={insulinNotes}
            formulations={dialogFormulations}
            catalog={insulinList.catalog}
            showExtendedFields={true}
            onCategoryChange={onDialogCategoryChange}
            onFormulationChange={onDialogFormulationChange}
          />

          <!-- Hidden fields for form submission -->
          <input type="hidden" name="n:{namePrefix}dia" value={insulinDia} />
          <input type="hidden" name="n:{namePrefix}peak" value={insulinPeak} />
          <input type="hidden" name="{namePrefix}curve" value={insulinCurve} />
          <input type="hidden" name="n:{namePrefix}concentration" value={insulinConcentration} />
          {#if insulinFormulationId}
            <input type="hidden" name="{namePrefix}formulationId" value={insulinFormulationId} />
          {/if}

          {#each activeForm.fields.allIssues() ?? [] as issue}
            <p class="text-sm text-destructive">{issue.message}</p>
          {/each}
        </div>

        <Dialog.Footer>
          <Button
            type="button"
            variant="outline"
            onclick={() => (dialogOpen = false)}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={dialogSaving}>
            {#if dialogSaving}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {:else}
              <Save class="mr-2 h-4 w-4" />
            {/if}
            {editing ? "Update" : "Add"} Insulin
          </Button>
        </Dialog.Footer>
      </form>
    </Dialog.Content>
  </Dialog.Root>

  <!-- Insulin Delete Confirmation -->
  <ConfirmDialog
    open={deleteId !== null}
    onOpenChange={(open) => {
      if (!open) deleteId = null;
    }}
    title="Delete Insulin"
    confirmLabel="Delete"
    destructive
    onConfirm={handleDelete}
  >
    {#snippet description()}
      Are you sure you want to delete this insulin? This action cannot be
      undone.
    {/snippet}
  </ConfirmDialog>
{/if}
