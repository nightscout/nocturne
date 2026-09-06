<script lang="ts">
  import { getChartContext } from "layerchart";

  interface Props {
    /** Mutated in place so a test can read the tallies once rendering settles. */
    counter: { marks: number; components: number };
  }

  let { counter }: Props = $props();

  type ChartRegistrar = {
    registerMark: (info: unknown) => () => void;
    registerComponent: (options: unknown) => unknown;
  };

  // Shadow the instance methods before any sibling registers. registerComponent
  // runs during a child's init, so this component must come first in the tree;
  // registerMark runs later, from registerComponent's own $effect.
  const chartCtx = getChartContext() as unknown as ChartRegistrar;

  const registerMark = chartCtx.registerMark.bind(chartCtx);
  chartCtx.registerMark = (info) => {
    counter.marks += 1;
    return registerMark(info);
  };

  const registerComponent = chartCtx.registerComponent.bind(chartCtx);
  chartCtx.registerComponent = (options) => {
    counter.components += 1;
    return registerComponent(options);
  };
</script>
