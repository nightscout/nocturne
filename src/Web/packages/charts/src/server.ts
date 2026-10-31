// What a server needs to draw report panels as images: the theme read from the stylesheets, and the
// panel component in its primitives' static mode. The host supplies the canvas.
export { resolveChartTheme, themeColors, clinicalColorNames, STATIC_FONT_FAMILY } from "./theme/resolve-theme.js";
export { paint, staticText, type StaticTheme, type StaticRender } from "./theme/static-theme.js";
export { cssColorToHex, parseCssColor, readCustomProperties } from "./theme/css-color.js";
export { default as StaticReportPanel } from "./report/StaticReportPanel.svelte";
export { staticPrimitive, staticDefaultHeight } from "./report/static/static-primitive.js";
