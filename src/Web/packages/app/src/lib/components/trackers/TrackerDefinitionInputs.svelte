<script lang="ts" module>
  import type {
    createDefinition,
    updateDefinition,
  } from "#lib/api/generated/trackers.generated.remote.js";

  /** The fields a definition is submitted under: the update form nests them under `request`. */
  export type DefinitionFields =
    | ReturnType<typeof createDefinition.for>["fields"]
    | ReturnType<typeof updateDefinition.for>["fields"]["request"];
</script>

<script lang="ts">
  import { TrackerMode } from "#lib/api/index.js";
  import {
    followsEvents,
    holdsInsulin,
    postedThresholds,
    showsCovers,
    type TrackerDraft,
  } from "./draft";

  interface Props {
    fields: DefinitionFields;
    draft: TrackerDraft;
  }

  let { fields, draft }: Props = $props();

  const isDuration = $derived(draft.mode === TrackerMode.Duration);

  // An empty list has to be posted as one blank value: no field at all means "leave the stored
  // list as it is". A trigger or an end only ever acts on a Duration tracker.
  const orBlank = (values: string[], applies: boolean) =>
    applies && values.length > 0 ? values : [""];

  const triggers = $derived(orBlank(draft.triggerEventTypes, isDuration));
  const ends = $derived(orBlank(draft.endEventTypes, isDuration));
  const covers = $derived(orBlank(draft.covers, showsCovers(draft)));
</script>

<!-- The whole definition as the form posts it. The controls that edit the draft carry no names. -->
<input {...fields.name.as("hidden", draft.name)} />
<input {...fields.description.as("hidden", draft.description)} />
<input {...fields.category.as("hidden", draft.category)} />
<input {...fields.mode.as("hidden", draft.mode)} />
<input {...fields.icon.as("hidden", draft.icon)} />
<input {...fields.dashboardVisibility.as("hidden", draft.dashboardVisibility)} />
{#if isDuration && draft.lifespanHours != null}
  <input {...fields.lifespanHours.as("hidden", draft.lifespanHours)} />
{/if}
{#each covers as capability, i (i)}
  <input {...fields.covers[i].as("hidden", capability)} />
{/each}
{#if holdsInsulin(draft)}
  {#if draft.lowReservoirUnits != null}
    <input {...fields.lowReservoirUnits.as("hidden", draft.lowReservoirUnits)} />
  {/if}
  <input {...fields.lowReservoirUrgency.as("hidden", draft.lowReservoirUrgency)} />
{/if}
{#each triggers as eventType, i (i)}
  <input {...fields.triggerEventTypes[i].as("hidden", eventType)} />
{/each}
{#each ends as eventType, i (i)}
  <input {...fields.endEventTypes[i].as("hidden", eventType)} />
{/each}
<input
  {...fields.triggerNotesContains.as("hidden", followsEvents(draft) ? draft.triggerNotesContains : "")}
/>
{#each postedThresholds(draft) as threshold, i (i)}
  <input {...fields.notificationThresholds[i].urgency.as("hidden", threshold.urgency)} />
  <input {...fields.notificationThresholds[i].hours.as("hidden", threshold.hours)} />
  <input {...fields.notificationThresholds[i].description.as("hidden", threshold.description ?? "")} />
  <input {...fields.notificationThresholds[i].displayOrder.as("hidden", threshold.displayOrder)} />
{/each}
{#if draft.isFavorite}
  <input {...fields.isFavorite.as("hidden", true)} />
{/if}
<input {...fields.startEventType.as("hidden", draft.startEventType ?? "")} />
<input {...fields.completionEventType.as("hidden", draft.completionEventType ?? "")} />
