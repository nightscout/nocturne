<script lang="ts">
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";

  interface Props {
    /** One message for each channel the test alert did not go to. */
    messages: string[];
    /** Replaces the default heading, to name the alert the test was for. */
    heading?: string;
  }

  let { messages, heading = "The test alert did not go to every channel." }: Props = $props();
</script>

<div class="space-y-1" role="status">
  <p class="text-sm font-medium">{heading}</p>
  <ul class="space-y-1" data-testid="alert-test-not-sent">
    {#each messages as message, i (i)}
      <li class="flex items-start gap-1.5 text-sm text-status-warning">
        <TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span>{message}</span>
      </li>
    {/each}
  </ul>
</div>
