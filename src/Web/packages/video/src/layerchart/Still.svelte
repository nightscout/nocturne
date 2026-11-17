<script>
  // `as` is the chart to draw; everything else passes through to it.
  let { as: Chart, props: inner = {}, ...rest } = $props();

  // A ready-made chart draws its default tooltip from inside layerchart, out of the shim's reach,
  // so its settings come in through `props.tooltip.root` instead.
  const still = $derived({
    ...inner,
    area: { ...inner.area, motion: "none" },
    xAxis: { ...inner.xAxis, motion: "none" },
    highlight: { ...inner.highlight, motion: "none" },
    tooltip: { ...inner.tooltip, root: { ...inner.tooltip?.root, motion: "none", fadeDuration: 0, portal: false } },
  });
</script>

<Chart {...rest} props={still} />
