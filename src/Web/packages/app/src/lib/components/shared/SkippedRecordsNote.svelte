<script lang="ts">
  import { resolve } from "$app/paths";
  import { formatNumber } from "$lib/utils/formatting";

  interface Props {
    /** Records left out because the user had deleted them. */
    deleted?: number;
    /** Records of a kind Nocturne does not store. */
    unsupported?: number;
    /** Records with a value Nocturne could not store, kept as rejected uploads. */
    refused?: number;
    class?: string;
  }

  const {
    deleted = 0,
    unsupported = 0,
    refused = 0,
    class: className = "",
  }: Props = $props();
</script>

{#if deleted > 0}
  <p class={className}>
    {#if deleted === 1}
      1 record was not added again because you had deleted it.
    {:else}
      {formatNumber(deleted)} records were not added again because you had deleted
      them.
    {/if}
  </p>
{/if}
{#if unsupported > 0}
  <p class={className}>
    {#if unsupported === 1}
      1 record was not added because Nocturne does not store that kind of
      record.
    {:else}
      {formatNumber(unsupported)} records were not added because Nocturne does not
      store that kind of record.
    {/if}
  </p>
{/if}
{#if refused > 0}
  <p class={className}>
    {#if refused === 1}
      1 record could not be stored. It is listed under
    {:else}
      {formatNumber(refused)} records could not be stored. They are listed under
    {/if}
    <a href={resolve("/reports/data-quality/rejected-uploads")} class="underline">Rejected Uploads</a>.
  </p>
{/if}
