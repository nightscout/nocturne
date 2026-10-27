<!-- The real reports layout under a coach-mark provider, handing the context to the test. -->
<script lang="ts">
  import { CoachMarkProvider, type CoachMarkAdapter, type CoachMarkContext } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import { coachLabels } from "#lib/coach-marks/copy.svelte.js";
  import { sequences } from "#lib/coach-marks/sequences.js";
  import Probe from "./loop-analysis/CoachContextProbe.test.svelte";
  import Layout from "./+layout.svelte";

  let { oncontext }: { oncontext: (ctx: CoachMarkContext) => void } = $props();

  const adapter: CoachMarkAdapter = { fetchAll: async () => [], update: async () => {} };
</script>

<CoachMarkProvider {adapter} labels={coachLabels()} {sequences} settleDelay={10} seenDwellMs={100000}>
  <Probe {oncontext} />
  <Layout>
    <p>Report body</p>
  </Layout>
</CoachMarkProvider>
