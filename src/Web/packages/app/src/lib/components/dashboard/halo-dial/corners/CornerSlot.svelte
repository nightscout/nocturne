<script lang="ts" module>
	export interface CornerData {
		basalRate?: { rate: number; percent: number } | null;
		reservoir?: { units: number; percent: number; minutesRemaining: number } | null;
		sensorAge?: { startedAtMs: number; expiryMs?: number } | null;
		pumpSiteAge?: { startedAtMs: number; expiryMs?: number } | null;
		battery?: { percent: number; voltage?: number; minutesRemaining?: number } | null;
		loop?: { status: string } | null;
		direction?: { direction: string } | null;
		eventual?: { mgdl: number; minutesAhead: number } | null;
	}
</script>

<script lang="ts">
	import { HaloDialCornerElement } from "../config";
	import BasalRate from "./elements/BasalRate.svelte";
	import Reservoir from "./elements/Reservoir.svelte";
	import SensorAge from "./elements/SensorAge.svelte";
	import PumpSiteAge from "./elements/PumpSiteAge.svelte";
	import Battery from "./elements/Battery.svelte";
	import LoopLabel from "./elements/LoopLabel.svelte";
	import LoopDot from "./elements/LoopDot.svelte";
	import Direction from "./elements/Direction.svelte";
	import Eventual from "./elements/Eventual.svelte";

	interface Props {
		position: "tl" | "tr" | "bl" | "br";
		elements: HaloDialCornerElement[];
		data: CornerData;
		elementConfig: Partial<Record<HaloDialCornerElement, unknown>>;
	}

	const { position, elements, data, elementConfig }: Props = $props();

	const alignClass = $derived(
		position === "tl" || position === "bl"
			? "items-start text-left"
			: "items-end text-right",
	);

	function basalOpts(o: unknown): { format: "U/h" | "%" | "both" } {
		return (o as { format: "U/h" | "%" | "both" }) ?? { format: "U/h" };
	}
	function reservoirOpts(o: unknown): { format: "units" | "percent" | "time-left" } {
		return (
			(o as { format: "units" | "percent" | "time-left" }) ?? { format: "units" }
		);
	}
	function ageOpts(
		o: unknown,
	): { format: "days" | "days+hours" | "until-expiry" } {
		return (
			(o as { format: "days" | "days+hours" | "until-expiry" }) ?? {
				format: "days+hours",
			}
		);
	}
	function batteryOpts(
		o: unknown,
	): { format: "percent" | "voltage" | "time-left" } {
		return (
			(o as { format: "percent" | "voltage" | "time-left" }) ?? {
				format: "percent",
			}
		);
	}
	function eventualOpts(o: unknown): { format: "value" | "in-time-value" } {
		return (
			(o as { format: "value" | "in-time-value" }) ?? { format: "value" }
		);
	}
</script>

<div
	class="flex flex-col gap-1 {alignClass}"
	data-testid="corner-slot"
	data-position={position}
>
	{#each elements as element (element)}
		{#if element === HaloDialCornerElement.BasalRate}
			<BasalRate
				value={data.basalRate ?? null}
				options={basalOpts(elementConfig[element])}
			/>
		{:else if element === HaloDialCornerElement.Reservoir}
			<Reservoir
				value={data.reservoir ?? null}
				options={reservoirOpts(elementConfig[element])}
			/>
		{:else if element === HaloDialCornerElement.SensorAge}
			<SensorAge
				value={data.sensorAge ?? null}
				options={ageOpts(elementConfig[element])}
			/>
		{:else if element === HaloDialCornerElement.PumpSiteAge}
			<PumpSiteAge
				value={data.pumpSiteAge ?? null}
				options={ageOpts(elementConfig[element])}
			/>
		{:else if element === HaloDialCornerElement.Battery}
			<Battery
				value={data.battery ?? null}
				options={batteryOpts(elementConfig[element])}
			/>
		{:else if element === HaloDialCornerElement.LoopLabel}
			<LoopLabel value={data.loop ?? null} />
		{:else if element === HaloDialCornerElement.LoopDot}
			<LoopDot value={data.loop ?? null} />
		{:else if element === HaloDialCornerElement.Direction}
			<Direction value={data.direction ?? null} />
		{:else if element === HaloDialCornerElement.Eventual}
			<Eventual
				value={data.eventual ?? null}
				options={eventualOpts(elementConfig[element])}
			/>
		{/if}
	{/each}
</div>
