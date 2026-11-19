# API reference pages

Markdown source for the pages of the Scalar API reference: the introduction, the guides,
the architecture pages and the overview of each tag. Each `.md` file is one
sidebar entry; `TagDescriptionDocumentTransformer` loads them at startup and renders each
as the description of a tag (GitHub-flavored markdown). ER diagrams from
`../diagrams/diagrams.yaml` are appended under a **Data Model** heading where mapped.

## Folder convention

- The **top-level folder** is the OpenAPI document the tag belongs to: `nocturne/`
  (V4 + Auth) or `nightscout/` (legacy V1–V3). Deeper subfolders are purely
  organizational — `nightscout/model-mapping/` groups the legacy-to-v4 mapping pages.
- The **file name** (without `.md`) is the name of a `[Tags]` area (`Glucose.md`), or, for a
  `standalone` page, the tag of the page (`model-mapping/ns-model-entries.md` → tag
  `ns-model-entries`).
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

## Introduction

`_introduction.md` in a document folder is not a tag. It is the `info.description` of that
document, which Scalar shows as the Introduction page. Scalar lists its headings in the
sidebar, so keep one `##` heading for each entry a reader looks for.

## Diagrams

A line that holds only `<!-- diagram: name.mmd -->` is replaced with that diagram from
`../diagrams/`, as a `mermaid` block. A diagram that is not on disk leaves nothing.

## Blocks

A fenced block with one of these names is replaced by a Svelte component from
`src/Web/packages/scalar-plugin`, which Scalar loads as a plugin. The components read the
OpenAPI document, so what they show comes from the code.

| Block | Shows |
| --- | --- |
| ` ```callout tip ` (or `info`, `warning`, `danger`) | The body as a note. Inline code, bold and links only. |
| ` ```resources <Group> ` | The entries of that sidebar group: label, first sentence of the class summary, operation count. |
| ` ```scopes ` | The OAuth scopes of the document's security scheme. |
| ` ```mermaid ` | The diagram, drawn when it scrolls into view. |

Without the plugin a block shows as a plain code block.

The API serves the built plugin from `wwwroot/scalar/nocturne-plugin.js`, which is tracked.
After a change to the package, rebuild it and commit the result; CI fails when the two differ:

    cd src/Web && pnpm --filter @nocturne/scalar-plugin build

## Links

Link to another page of the same document with `#<document>/tag/<tag-name>`, lower case and with
spaces as hyphens: `[Syncing](#nocturne/tag/syncing)`.

## What comes from the code

A page here says only what a controller cannot. The sidebar itself is read off the code:

- Each controller is one sidebar entry. Its label is the class name (or a `[DisplayName]` on
  the class), and its page is the class's XML `<summary>`.
- The `[Tags("<Area>")]` on the controller is its sidebar group.
- A file here named after an area (`Glucose.md`) is the **Overview** entry of that group, and
  its `displayName` is the label of the group.

Do not list operations, paths or response shapes in a page: Scalar generates those. Write
notes that span resources, and link to an entry with its tag.

`RuntimeOpenApiDocumentTests` fails when a controller has no summary or no area, when an
area has no overview page or no place in `TagDescriptionDocumentTransformer.Sidebar`, when a
page is shown nowhere, and when a page names an API path or links to a tag that the document
does not have.

Sidebar **grouping** (the "Guides" / "Health Data" sections) is configured separately
in `TagDescriptionDocumentTransformer.Sidebar`, not here.

These files are published to `wwwroot/api-descriptions/` by an MSBuild target in
`Nocturne.API.csproj` so they resolve inside the Docker image where the source tree is
absent.
