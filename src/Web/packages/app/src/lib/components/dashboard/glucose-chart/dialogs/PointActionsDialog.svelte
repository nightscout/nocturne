<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import type { EntryRecord } from "#lib/constants/entry-categories.js";
  import { entryCategoryName } from "#lib/constants/entry-category-labels.svelte.js";
  import { ENTRY_CATEGORIES } from "#lib/constants/entry-categories.js";
  import { entrySummary } from "#lib/utils/entry-summary.svelte.js";
  import { formatDateTimeCompact, time } from "#lib/utils/formatting.js";
  import type { InspectionPickerOption } from "../engine/point-inspection.svelte";
  import Activity from "@lucide/svelte/icons/activity";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Droplet from "@lucide/svelte/icons/droplet";
  import FileText from "@lucide/svelte/icons/file-text";
  import Plus from "@lucide/svelte/icons/plus";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Utensils from "@lucide/svelte/icons/utensils";
  import Waves from "@lucide/svelte/icons/waves";

  type InspectionType = InspectionPickerOption["type"];

  interface Props {
    open: boolean;
    timestamp: Date | null;
    /** The data at this time that has its own inspection view. */
    inspectOptions: InspectionPickerOption[];
    /** The entries recorded near this time. */
    entries: EntryRecord[];
    /** Offers a new entry at this time. */
    canLog: boolean;
    onInspect: (type: InspectionType) => void;
    onEdit: (entry: EntryRecord) => void;
    onLog: () => void;
    onClose: () => void;
  }

  let {
    open = $bindable(),
    timestamp,
    inspectOptions,
    entries,
    canLog,
    onInspect,
    onEdit,
    onLog,
    onClose,
  }: Props = $props();

  const inspectIcons = {
    glucose: Activity,
    delivery: Waves,
    treatment: Syringe,
  } as const;

  const entryIcons = {
    bolus: Syringe,
    carbs: Utensils,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
    basalInjection: Syringe,
  } as const;

  const title = $derived.by(() => {
    if (!timestamp) return "";
    const sameDay = timestamp.toDateString() === new Date().toDateString();
    return sameDay ? time(timestamp) : formatDateTimeCompact(timestamp);
  });

  const description = $derived.by(() => {
    if (inspectOptions.length > 0 && entries.length > 0)
      return "Select data to inspect or an entry to edit.";
    if (inspectOptions.length > 0) return "Select data to inspect.";
    if (entries.length > 0) return "Select an entry to edit.";
    return "No data to inspect or entries to edit at this time.";
  });
</script>

{#snippet row(
  Icon: typeof Activity,
  iconClass: string,
  label: string,
  detail: string,
  onclick: () => void,
)}
  <li>
    <Item variant="row" {onclick}>
      <Icon class="size-4 shrink-0 {iconClass}" aria-hidden="true" />
      <span class="min-w-0 flex-1">
        <span class="block text-sm font-medium">{label}</span>
        {#if detail}
          <span class="block truncate text-xs text-muted-foreground tabular-nums">{detail}</span>
        {/if}
      </span>
      <ChevronRight class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    </Item>
  </li>
{/snippet}

<Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
  <Dialog.Content class="max-w-md print:hidden">
    <Dialog.Header>
      <Dialog.Title class="tabular-nums">{title}</Dialog.Title>
      <Dialog.Description>{description}</Dialog.Description>
    </Dialog.Header>

    {#if inspectOptions.length > 0}
      <section class="space-y-1.5" aria-labelledby="point-actions-inspect">
        <h3 id="point-actions-inspect" class="text-xs font-medium text-muted-foreground">
          Inspect
        </h3>
        <ul class="divide-y overflow-hidden rounded-lg border">
          {#each inspectOptions as option (option.type)}
            {@render row(
              inspectIcons[option.type],
              "text-muted-foreground",
              option.label,
              option.preview,
              () => onInspect(option.type),
            )}
          {/each}
        </ul>
      </section>
    {/if}

    {#if entries.length > 0}
      <section class="space-y-1.5" aria-labelledby="point-actions-entries">
        <h3 id="point-actions-entries" class="text-xs font-medium text-muted-foreground">
          Edit an entry
        </h3>
        <ul class="divide-y overflow-hidden rounded-lg border">
          {#each entries as entry, i (entry.data.id ?? `${entry.data.mills}-${i}`)}
            {@const category = ENTRY_CATEGORIES[entry.kind]}
            {@render row(
              entryIcons[entry.kind],
              category.colorClass,
              entrySummary(entry) || entryCategoryName(entry.kind),
              [entryCategoryName(entry.kind), entry.data.mills ? time(entry.data.mills) : ""]
                .filter(Boolean)
                .join(" · "),
              () => onEdit(entry),
            )}
          {/each}
        </ul>
      </section>
    {/if}

    <Dialog.Footer>
      <Button variant="outline" onclick={onClose}>Cancel</Button>
      {#if canLog}
        <Button onclick={onLog} data-testid="point-log-event">
          <Plus aria-hidden="true" />
          Log event
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
