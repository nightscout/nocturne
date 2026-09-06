<script lang="ts">
  import AlertBanner from "./AlertBanner.svelte";
  import FiringToast from "./FiringToast.svelte";
  import { pollActiveAlerts } from "./active-alerts-poll.svelte";

  // One poll for both surfaces — they read the same query. Mounting the poll with the surfaces
  // keeps the two from drifting apart: a host that must not show them never starts it either.
  pollActiveAlerts();
</script>

<AlertBanner />
<FiringToast />
