<script lang="ts">
  let { read }: { read: () => unknown } = $props();
  const found = $derived(read());
</script>

<p data-found={found ? "yes" : "no"}></p>
