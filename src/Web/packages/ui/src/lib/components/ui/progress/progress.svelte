<script lang="ts" module>
  import { tv, type VariantProps } from "tailwind-variants";

  export const progressVariants = tv({
    slots: {
      root: "relative h-2 w-full overflow-hidden rounded-full",
      indicator:
        "h-full w-full flex-1 translate-x-(--progress-offset) transition-all",
    },
    variants: {
      // `in-range` is a share of time in range: the in-range glucose token. `warning` is a run
      // that stopped or left records out.
      tone: {
        default: { root: "bg-primary/20", indicator: "bg-primary" },
        warning: { root: "bg-warning/20", indicator: "bg-warning" },
        "in-range": {
          root: "bg-(--glucose-in-range)/20",
          indicator: "bg-(--glucose-in-range)",
        },
      },
      // `hatched` is a figure from data that is not reliable. The caller sets `--hatch` and
      // `--hatch-size` on the root, from the texture source of its charts.
      hatched: {
        true: { indicator: "bg-(image:--hatch) bg-size-(--hatch-size)" },
        false: {},
      },
    },
    defaultVariants: { tone: "default", hatched: false },
  });

  export type ProgressTone = VariantProps<typeof progressVariants>["tone"];
</script>

<script lang="ts">
  import { Progress as ProgressPrimitive } from "bits-ui";
  import { cn, type WithoutChildrenOrChild } from "../../../utils";

  let {
    ref = $bindable(null),
    class: className,
    max = 100,
    value,
    tone = "default",
    hatched = false,
    ...restProps
  }: WithoutChildrenOrChild<ProgressPrimitive.RootProps> & {
    tone?: ProgressTone;
    hatched?: boolean;
  } = $props();

  const styles = $derived(progressVariants({ tone, hatched }));
</script>

<ProgressPrimitive.Root
  bind:ref
  data-slot="progress"
  class={cn(styles.root(), className)}
  {value}
  {max}
  {...restProps}
>
  <div
    data-slot="progress-indicator"
    class={styles.indicator()}
    style="--progress-offset: -{100 - (100 * (value ?? 0)) / (max ?? 1)}%"
  ></div>
</ProgressPrimitive.Root>
