<script lang="ts">
  let {
    selectedDataSources = $bindable([]),
  }: { selectedDataSources?: string[] } = $props();

  // The stub only needs to accept the prop and stay bindable for its callers.
  void selectedDataSources;
</script>
