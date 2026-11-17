<!-- The real reports layout under a coach-mark provider, handing the context to the test. -->
<script lang="ts">
  import { CoachMarkProvider, type CoachMarkAdapter, type CoachMarkContext } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import { coachLabels } from "#lib/coach-marks/copy.svelte.js";
  import { coachAudience, sequences } from "#lib/coach-marks/sequences.js";
  import { page } from "$app/state";
  import Probe from "./loop-analysis/CoachContextProbe.test.svelte";
  import Layout from "./+layout.svelte";

  let { oncontext }: { oncontext: (ctx: CoachMarkContext) => void } = $props();

  const adapter: CoachMarkAdapter = { fetchAll: async () => [], update: async () => {} };

  const audience = $derived(coachAudience({ user: page.data.user, isGuestSession: page.data.isGuestSession }));
</script>

<CoachMarkProvider {adapter} labels={coachLabels()} {sequences} {audience} settleDelay={10} seenDwellMs={100000}>
  <Probe {oncontext} />
  <Layout>
    <p>Report body</p>
  </Layout>
</CoachMarkProvider>
