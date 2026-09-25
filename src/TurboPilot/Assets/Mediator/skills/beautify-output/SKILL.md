---
name: beautify-output
description: 'Identifies file links, image references, and headings in an assistant response. Use to improve rendered formatting without changing its wording.'
license: Apache-2.0
---

# Prepare Output

## When to Use

Use when `response` contains completed output to format for display.

## Rules

Return formatting suggestions only, not a rewritten response:

`{"links":[{"text":"path exactly as written","path":"the same exact path","image":false}],"headings":["exact standalone line"]}`

Identify local file paths already written in the response, such as `src\app.cs` or
`README.md`. Copy each path exactly into both `text` and `path`; the host links only
text that names an existing file. Never map a bare name such as README to a file name.
Mark a reference as an image only for an image file. Suggest headings only for short,
standalone plain-text lines that already appear as a whole line. Empty arrays are valid.
Always return both keys: links and headings. If there are no suggestions, return
exactly {"links":[],"headings":[]}. A response containing only headings is invalid.

## Gotchas

- Do not invent a path, a heading, or search for a file.
- Never modify code fences, commands, tables, existing links, or images.
- Do not change wording, facts, or conclusions. The host validates every suggestion.
