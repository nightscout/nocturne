# API tag descriptions

Markdown source for the Scalar API-reference tag overviews. Each `.md` file is one
OpenAPI tag; `TagDescriptionDocumentTransformer` loads them at startup and renders each
as that tag's description (GitHub-flavored markdown). ER diagrams from
`../diagrams/diagrams.yaml` are appended under a **Data Model** heading where mapped.

## Folder convention

- The **top-level folder** is the OpenAPI document the tag belongs to: `nocturne/`
  (V4 + Auth) or `nightscout/` (legacy V1–V3). Deeper subfolders are purely
  organizational — `nightscout/model-mapping/` groups the legacy-to-v4 mapping pages.
- The **file name** (without `.md`) is the exact tag name, e.g. `OIDC Discovery.md` →
  tag `OIDC Discovery`, `model-mapping/ns-model-entries.md` → tag `ns-model-entries`.
- The file body is the markdown description.

## Optional YAML frontmatter

```
---
displayName: Nightscout V1   # sidebar label override (x-displayName); omit to use the tag name
standalone: true             # conceptual page with no operations — still rendered as its own sidebar entry
---
```

Both keys are optional; a file with no frontmatter is a plain description for a tag that
already has operations.

Sidebar **grouping** (the "Data Model" / "Health Data" sections) is configured separately
in `ScalarExtensionsDocumentTransformer` via `x-tagGroups`, not here.

These files are published to `wwwroot/api-descriptions/` by an MSBuild target in
`Nocturne.API.csproj` so they resolve inside the Docker image where the source tree is
absent.
