<script lang="ts">
  // Test-only wrapper: FormField takes a snippet, which can't be passed from a
  // plain `render(Component, props)` call.
  import { Input } from "$lib/components/ui/input";
  import FormField from "./FormField.svelte";
  import type { FieldIssues } from "./field-messages";

  let {
    label,
    id,
    required = false,
    description,
    issues,
    second,
  }: {
    label: string;
    id?: string;
    required?: boolean;
    description?: string;
    issues?: FieldIssues;
    /** Renders a second field with this label, to check id uniqueness. */
    second?: string;
  } = $props();
</script>

<FormField {label} {id} {required} {description} {issues}>
  {#snippet control(field)}
    <Input {...field} name="first" />
  {/snippet}
</FormField>

{#if second}
  <FormField label={second}>
    {#snippet control(field)}
      <Input {...field} name="second" />
    {/snippet}
  </FormField>
{/if}
