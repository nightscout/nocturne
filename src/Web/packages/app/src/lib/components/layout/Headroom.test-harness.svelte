<script lang="ts">
  import Headroom from "./Headroom.svelte";
</script>

<div class="h-[400vh] [--headroom-top:56px]">
  <div class="h-40" data-testid="above"></div>
  <Headroom>
    <div class="h-20 bg-card" data-testid="panel">Panel</div>
  </Headroom>
</div>
