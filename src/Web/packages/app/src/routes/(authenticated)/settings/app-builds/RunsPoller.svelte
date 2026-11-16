<script lang="ts">
  import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";

  interface Props {
    refresh: () => void;
    intervalMs: number;
  }

  let { refresh, intervalMs }: Props = $props();

  // The interval is fixed for the poller's life; it is mounted only while there is something to watch.
  // svelte-ignore state_referenced_locally
  pollWhileVisible(() => refresh(), intervalMs);
</script>
