<script lang="ts">
	import { polar, RING_RADIUS } from "../geometry";

	const { x, y } = polar(0, RING_RADIUS);
</script>

<circle
	cx={x}
	cy={y}
	r="6"
	fill="var(--foreground)"
	fill-opacity="0.3"
/>
<circle cx={x} cy={y} r="2.5" fill="var(--foreground)" />
