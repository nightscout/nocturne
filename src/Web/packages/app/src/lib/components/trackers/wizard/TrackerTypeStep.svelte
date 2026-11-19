<script lang="ts">
  import { TrackerCategoryIcon } from "#lib/components/icons/index.js";
  import WizardOptionList from "#lib/components/wizard/WizardOptionList.svelte";
  import {
    templateDescription,
    templateLabel,
    type TrackerTemplate,
    type TrackerTemplateId,
  } from "./templates.svelte";

  interface Props {
    selected: TrackerTemplateId | null;
    onPick: (id: TrackerTemplateId) => void;
    /** The types there are, from the API's templates. */
    templates: readonly TrackerTemplate[];
    /** Whether the templates are still on their way. */
    loading?: boolean;
  }

  let { selected, onPick, templates, loading = false }: Props = $props();

  const options = $derived(
    templates.map((t) => ({
      id: t.id,
      label: templateLabel(t.id),
      description: templateDescription(t.id),
    })),
  );
</script>

<WizardOptionList
  label="Tracker types"
  testIdPrefix="tracker-type-"
  {options}
  {selected}
  {onPick}
  {loading}
>
  {#snippet icon(id: TrackerTemplateId)}
    <TrackerCategoryIcon
      category={templates.find((t) => t.id === id)?.definition.category}
      class="size-5"
    />
  {/snippet}
</WizardOptionList>
