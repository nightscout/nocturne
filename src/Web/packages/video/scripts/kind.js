/**
 * Reports or feature films, as a script's command line names them: `--features` picks the feature
 * films, and the remaining arguments are returned as `names`.
 */
import { existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { reports } from "../src/reports.js";
import { features } from "../src/features.js";

const REPORTS = {
  flag: "",
  list: reports,
  films: "src/films",
  config: "vite.config.js",
  env: "VIDEO_REPORT",
  composition: (name) => name,
};
const FEATURES = {
  flag: "--features",
  list: features,
  films: "src/features/films",
  config: "vite.features.config.js",
  env: "VIDEO_FEATURE",
  composition: (name) => `feature-${name}`,
};

export function kindOf(argv) {
  const forFeatures = argv.includes("--features");
  const kind = forFeatures ? FEATURES : REPORTS;
  const filmOf = (name) => `${kind.films}/${name}.js`;
  return {
    ...kind,
    forFeatures,
    names: argv.filter((arg) => arg !== "--features"),
    /** The film's path from the package root. */
    filmOf,
    hasFilm: (name) => existsSync(fileURLToPath(new URL(`../${filmOf(name)}`, import.meta.url))),
    loadFilm: async (name) => (await import(`../${filmOf(name)}`)).default,
  };
}
