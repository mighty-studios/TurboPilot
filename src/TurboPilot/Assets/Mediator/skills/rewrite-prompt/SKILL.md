---
name: rewrite-prompt
description: 'Condenses a user prompt before forwarding it. Use for brevity and clarity without answering the request or losing constraints.'
license: Apache-2.0
---

# Reword a Prompt

## When to Use

Use when the input contains a `prompt` that will be forwarded unchanged in meaning.
The optional `summary` is background context, not a new request.

## Rules

Remove pleasantries, redundant wording, and repetition. Preserve every requested
action, condition, exception, identifier, literal, file path, number, and negation.
Leave ambiguous references ambiguous rather than guessing their meaning.
Do not add solutions, assumptions, or a plan.

Copy every string in `protectedText` exactly into the reduced prompt. Keep numbers
as written: `3` must not become `three`. Preserve the entire meaning of every
restriction, including what must not be edited or executed.

Return `{"prompt":"shorter equivalent wording","meaningPreserved":true}`.
If no safe reduction is possible, return the original prompt and set
`meaningPreserved` to false.

Example:
Input: "Please summarize README.md in 3 bullets without editing any files. Thanks."
Output: `{"prompt":"Summarize README.md in 3 bullets without editing any files.","meaningPreserved":true}`
Invalid: "Summarize README.md in three bullets" drops the no-edit restriction and changes a number.

## Gotchas

- Never rewrite code, commands, quoted literals, or paths.
- Shorter text that drops a requirement is not a valid reduction.
