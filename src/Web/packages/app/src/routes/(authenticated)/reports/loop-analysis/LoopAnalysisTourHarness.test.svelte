<!-- A Loop Analysis page and its header under a coach-mark provider whose stored marks the test holds. -->
<script lang="ts">
  import type { Component } from "svelte";
  import { CoachMarkProvider, type CoachMarkAdapter, type CoachMarkContext, type MarkState } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import { coachLabels } from "#lib/coach-marks/copy.svelte.js";
  import { sequences } from "#lib/coach-marks/sequences.js";
  import Harness from "./LoopAnalysisHarness.test.svelte";
  import Probe from "./CoachContextProbe.test.svelte";

  interface Props {
    report: Component;
    /** The reader's stored mark states, kept across renders as the server keeps them. */
    store: Map<string, MarkState>;
    oncontext: (ctx: CoachMarkContext) => void;
  }

  let { report, store, oncontext }: Props = $props();

  const adapter: CoachMarkAdapter = {
    fetchAll: async () => [...store.values()],
    update: async (key, status) => {
      store.set(key, { id: key, markKey: key, status, seenAt: null, completedAt: null });
    },
  };
</script>

<CoachMarkProvider {adapter} labels={coachLabels()} {sequences} settleDelay={10} seenDwellMs={100000}>
  <Probe {oncontext} />
  <Harness {report} />
</CoachMarkProvider>
