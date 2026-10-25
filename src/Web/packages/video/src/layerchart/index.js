export * from "layerchart";
import { Tooltip as Base } from "layerchart";
import Root from "./TooltipRoot.svelte";
import Highlight from "./Highlight.svelte";
import AreaChart from "./AreaChart.svelte";
import BarChart from "./BarChart.svelte";
import LineChart from "./LineChart.svelte";
import PieChart from "./PieChart.svelte";

// The renderer seeks frames out of order, so nothing may ease on the browser clock: a tooltip or
// highlight lands where the pointer is on the same frame, and charts draw without tweening in.
export const Tooltip = { ...Base, Root };
export { AreaChart, BarChart, Highlight, LineChart, PieChart };
