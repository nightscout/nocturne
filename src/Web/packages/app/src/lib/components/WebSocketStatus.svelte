<script lang="ts">
  import { Card, CardContent } from "$lib/components/ui/card";
  import ConnectionStatus from "./ConnectionStatus.svelte";
</script>

<Card class="w-full max-w-sm">
  <CardContent class="p-4">
    <ConnectionStatus detailed />
  </CardContent>
</Card>
