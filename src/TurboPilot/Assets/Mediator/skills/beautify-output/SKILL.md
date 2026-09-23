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

`{"links":[{"text":"exact text from the response","path":"the referenced path","image":false}],"headings":["exact standalone line"]}`

Identify local file references already present in the response. Mark a reference
as an image only for an image file. Suggest headings only for short, standalone
plain-text section titles. Empty arrays are valid.
Always return both keys: links and headings. If there are no suggestions, return
exactly {"links":[],"headings":[]}. A response containing only headings is invalid.

## Gotchas

- Do not invent a path or search for a file.
- Never modify code fences, commands, tables, existing links, or images.
- Do not change wording, facts, or conclusions. The host validates every suggestion.
