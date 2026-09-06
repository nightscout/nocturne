// The supported-SDK list is DERIVED from sdk/<lang>/config.yaml at build time
// (see scripts/generate-sdk-manifest.mjs) so it can never drift from what we
// actually publish. This module only adds the presentation details that aren't
// in those configs — display name, registry, and install command — keyed by the
// SDK directory name. A newly added SDK shows up automatically; if it has no
// presentation entry yet it still lists with its package name and a sane default.

import manifest from "./generated/manifest.json";

export interface SdkManifestEntry {
  /** SDK directory under sdk/ (also the stable presentation key). */
  dir: string;
  /** OpenAPI-generator name from config.yaml. */
  generator: string;
  /** Canonical published package identifier. */
  package: string;
}

export interface Sdk extends SdkManifestEntry {
  /** Human-readable language name. */
  name: string;
  /** Registry the package is published to. */
  registry: string;
  /** Link to the package on its registry. */
  registryUrl: string;
  /** Copy-pasteable install command / dependency line. */
  install: string;
}

interface Presentation {
  name: string;
  registry: string;
  registryUrl: (pkg: string) => string;
  install: (pkg: string) => string;
}

// Maven coordinates (group:artifact) map to a central.sonatype.com path.
const mavenUrl = (pkg: string) =>
  `https://central.sonatype.com/artifact/${pkg.replace(":", "/")}`;
const mavenGradle = (pkg: string) => `implementation("${pkg}:<version>")`;

const PRESENTATION: Record<string, Presentation> = {
  csharp: {
    name: "C# / .NET",
    registry: "NuGet",
    registryUrl: (p) => `https://www.nuget.org/packages/${p}/`,
    install: (p) => `dotnet add package ${p}`,
  },
  typescript: {
    name: "TypeScript / JavaScript",
    registry: "npm",
    registryUrl: (p) => `https://www.npmjs.com/package/${p}`,
    install: (p) => `npm install ${p}`,
  },
  python: {
    name: "Python",
    registry: "PyPI",
    registryUrl: (p) => `https://pypi.org/project/${p}/`,
    install: (p) => `pip install ${p}`,
  },
  rust: {
    name: "Rust",
    registry: "crates.io",
    registryUrl: (p) => `https://crates.io/crates/${p}`,
    install: (p) => `cargo add ${p}`,
  },
  java: {
    name: "Java",
    registry: "Maven Central",
    registryUrl: mavenUrl,
    install: mavenGradle,
  },
  kotlin: {
    name: "Kotlin",
    registry: "Maven Central",
    registryUrl: mavenUrl,
    install: mavenGradle,
  },
  swift: {
    name: "Swift",
    registry: "Swift Package Manager",
    // Published to its own GitHub repo rather than a package index.
    registryUrl: () => "https://github.com/nightscout/nocturne-swift",
    install: () =>
      `.package(url: "https://github.com/nightscout/nocturne-swift.git", from: "<version>")`,
  },
};

export const SDKS: Sdk[] = (manifest as SdkManifestEntry[]).map((entry) => {
  const p = PRESENTATION[entry.dir];
  return {
    ...entry,
    name: p?.name ?? entry.dir,
    registry: p?.registry ?? "—",
    registryUrl: p ? p.registryUrl(entry.package) : "#",
    install: p ? p.install(entry.package) : entry.package,
  };
});
