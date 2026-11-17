<script lang="ts">
  import { untrack } from "svelte";
  import { getCoachMarkContext } from "@nocturne/coach";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import {
    TrackerCategory,
    type LegacyConsumableKind,
    type LegacyConversionDto,
    type LegacyFallbackDto,
  } from "#lib/api/index.js";
  import { createFromLegacy } from "#lib/api/generated/trackerLegacyFallbacks.generated.remote.js";
  import { TrackerCategoryIcon } from "#lib/components/icons/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { formatLocale } from "#lib/utils/formatting.js";
  import { formatLifespan } from "./age.svelte";
  import { legacyKindLabel } from "./legacy-kind-label.svelte";
  import Check from "@lucide/svelte/icons/check";
  import CircleAlert from "@lucide/svelte/icons/circle-alert";
  import Plus from "@lucide/svelte/icons/plus";

  interface Props {
    open?: boolean;
    /** Every legacy age no tracker covers. */
    fallbacks: LegacyFallbackDto[];
    /** The kind selected when the dialog opens. */
    initialKind?: LegacyConsumableKind;
    /** Called once a conversion created at least one tracker. */
    onCreated?: () => void;
  }

  let { open = $bindable(false), fallbacks, initialKind, onCreated }: Props = $props();

  const TRACKER_INTRO = "tracker-intro";

  const coach = getCoachMarkContext();

  let selected = $state<LegacyConsumableKind[]>([]);
  let submitting = $state(false);
  let errorMessage = $state<string | null>(null);
  let result = $state.raw<LegacyConversionDto | null>(null);

  $effect(() => {
    if (!open) return;
    untrack(() => {
      selected = initialKind ? [initialKind] : [];
      errorMessage = null;
      result = null;
    });
  });

  const failedNames = $derived((result?.failedKinds ?? []).map(legacyKindLabel));

  function toggle(kind: LegacyConsumableKind, checked: boolean) {
    selected = checked ? [...selected, kind] : selected.filter((k) => k !== kind);
  }

  function lastChange(at: string | undefined): string {
    if (!at) return "";
    return new Date(at).toLocaleString(formatLocale(), {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  async function submit() {
    if (selected.length === 0) return;
    submitting = true;
    errorMessage = null;
    try {
      result = await createFromLegacy({ kinds: selected });
      if ((result.definitions?.length ?? 0) > 0) onCreated?.();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Could not create the trackers. Try again.");
    } finally {
      submitting = false;
    }
  }

  function finish() {
    const created = (result?.definitions?.length ?? 0) > 0;
    if (created) coach?.startSequence(TRACKER_INTRO);
  }

  function close() {
    open = false;
    finish();
  }
</script>

<Dialog.Root bind:open onOpenChange={(next) => !next && finish()}>
  <Dialog.Content data-testid="create-trackers-from-legacy-dialog">
    <Dialog.Header>
      <Dialog.Title>Create trackers</Dialog.Title>
      <Dialog.Description>
        {#if result}
          The status of each item.
        {:else}
          Select the items to track. Each tracker starts at the last change of its item.
        {/if}
      </Dialog.Description>
    </Dialog.Header>

    {#if result}
      <div class="space-y-3 py-2" data-testid="legacy-conversion-result">
        {#if (result.definitions?.length ?? 0) > 0}
          <div>
            <p class="text-sm font-medium">Created</p>
            <ul class="mt-1 space-y-1">
              {#each result.definitions ?? [] as definition (definition.id)}
                <li class="flex items-center gap-2 text-sm">
                  <Check class="h-4 w-4 text-primary" aria-hidden="true" />
                  {definition.name}
                </li>
              {/each}
            </ul>
          </div>
        {/if}
        {#if failedNames.length > 0}
          <div>
            <p class="text-sm font-medium text-destructive">Not created</p>
            <ul class="mt-1 space-y-1">
              {#each failedNames as name (name)}
                <li class="flex items-center gap-2 text-sm">
                  <CircleAlert class="h-4 w-4 text-destructive" aria-hidden="true" />
                  {name}
                </li>
              {/each}
            </ul>
            <p class="mt-2 text-xs text-muted-foreground">
              Nocturne could not create these trackers. Try again later.
            </p>
          </div>
        {/if}
        {#if (result.definitions?.length ?? 0) === 0 && failedNames.length === 0}
          <p class="text-sm text-muted-foreground">Trackers for these items exist already.</p>
        {/if}
      </div>
      <Dialog.Footer>
        <Button onclick={close}>Done</Button>
      </Dialog.Footer>
    {:else}
      <ul class="space-y-2 py-2">
        {#each fallbacks as fallback (fallback.kind)}
          {@const kind = fallback.kind}
          {#if kind}
            {@const id = `legacy-kind-${kind}`}
            <li class="flex items-start gap-3 rounded-md border border-border p-3">
              <Checkbox
                {id}
                checked={selected.includes(kind)}
                onCheckedChange={(checked) => toggle(kind, checked === true)}
              />
              <div class="grid gap-1">
                <Label for={id} variant="option" class="cursor-pointer">
                  <span class="flex items-center gap-2">
                    <TrackerCategoryIcon
                      category={fallback.spec?.category ?? TrackerCategory.Custom}
                      class="h-4 w-4"
                    />
                    {legacyKindLabel(kind)}
                  </span>
                </Label>
                <p class="text-xs text-muted-foreground">
                  Lifespan: {formatLifespan(fallback.spec?.lifespanHours)}. Starts from the
                  last change: {lastChange(fallback.latestEventAt)}.
                </p>
              </div>
            </li>
          {/if}
        {/each}
      </ul>
      {#if errorMessage}
        <p class="text-sm text-destructive" role="alert">{errorMessage}</p>
      {/if}
      <Dialog.Footer>
        <Button variant="outline" onclick={close} disabled={submitting}>Cancel</Button>
        <Button onclick={submit} disabled={submitting || selected.length === 0}>
          <Plus class="mr-2 h-4 w-4" />
          Create trackers
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>
