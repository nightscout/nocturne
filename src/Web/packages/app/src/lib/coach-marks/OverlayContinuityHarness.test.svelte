<script lang="ts">
  import {
    CoachMarkProvider,
    coachmark,
    type CoachMarkAdapter,
    type CoachMarkOptions,
    type SequenceConfig,
  } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import Starter from "./OverlayContinuityStarter.test.svelte";

  let { forced = false, multiStep = false }: { forced?: boolean; multiStep?: boolean } = $props();

  const adapter: CoachMarkAdapter = {
    fetchAll: async () => [],
    update: async () => {},
    deleteAll: async () => {},
  };

  const steps = ["tour.alpha", "tour.bravo", "tour.charlie"];

  // Forced mode gates the tour behind a sequence that can never finish, so only
  // the explicit startSequence raises it and the organic selector stays out of
  // the way.
  const sequences: SequenceConfig = $derived({
    gate: { priority: 1, steps: ["gate.unreachable"] },
    tour: forced
      ? { priority: 100, steps, prerequisite: "gate" }
      : { priority: 100, steps },
  });

  const alpha: CoachMarkOptions | CoachMarkOptions[] = $derived(
    multiStep
      ? [
          { key: "tour.alpha", step: 0, title: "Alpha", description: "First stop." },
          {
            key: "tour.alpha",
            step: 1,
            title: "Alpha again",
            description: "First stop, part two.",
          },
        ]
      : { key: "tour.alpha", title: "Alpha", description: "First stop." },
  );
</script>

<CoachMarkProvider {adapter} {sequences} settleDelay={10} seenDwellMs={100000}>
  <Starter sequence="tour" />
  <div {@attach coachmark(alpha)}>Alpha target</div>
  <div
    {@attach coachmark({ key: "tour.bravo", title: "Bravo", description: "Second stop." })}
  >
    Bravo target
  </div>
  <div
    {@attach coachmark({ key: "tour.charlie", title: "Charlie", description: "Third stop." })}
  >
    Charlie target
  </div>
</CoachMarkProvider>
