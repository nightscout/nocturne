import { describe, expect, it } from "vitest";
import { dropModulepreloads } from "./link-header";

const css = '</_app/immutable/assets/0.A.css>; rel="preload"; as="style"; nopush';
const font =
  '</_app/immutable/assets/f.A.woff2>; rel="preload"; as="font"; type="font/woff2"; crossorigin; nopush';
const js = (name: string) => `</_app/immutable/chunks/${name}.js>; rel="modulepreload"; nopush`;

describe("dropModulepreloads", () => {
  it("keeps the style and font preloads and drops every modulepreload", () => {
    const link = [js("a"), css, js("b"), font, js("c")].join(", ");
    expect(dropModulepreloads(link)).toBe([css, font].join(", "));
  });

  it("drops entry and node modules too", () => {
    const link = [
      '</_app/immutable/entry/start.A.js>; rel="modulepreload"; nopush',
      '</_app/immutable/nodes/3.A.js>; rel="modulepreload"; nopush',
      css,
    ].join(", ");
    expect(dropModulepreloads(link)).toBe(css);
  });

  it("does not split an entry on a comma inside its URL", () => {
    const comma =
      '</fonts/Cabin-VariableFont_wdth,wght.woff2>; rel="preload"; as="font"; crossorigin; nopush';
    const jsComma = '</_app/immutable/chunks/a,b.js>; rel="modulepreload"; nopush';
    expect(dropModulepreloads([jsComma, comma, js("c")].join(", "))).toBe(comma);
  });

  it("matches an unquoted rel", () => {
    expect(dropModulepreloads(`</a.js>; rel=modulepreload, ${css}`)).toBe(css);
  });

  it("returns an empty string when only modulepreloads remain", () => {
    expect(dropModulepreloads([js("a"), js("b")].join(", "))).toBe("");
  });

  it("leaves a header with no modulepreload unchanged", () => {
    const link = [css, font].join(", ");
    expect(dropModulepreloads(link)).toBe(link);
  });
});
