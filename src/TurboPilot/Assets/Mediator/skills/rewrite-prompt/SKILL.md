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

Return `{"prompt":"shorter equivalent wording","meaningPreserved":true}`.
If no safe reduction is possible, return the original prompt and set
`meaningPreserved` to false.

## Gotchas

- Never rewrite code, commands, quoted literals, or paths.
- Shorter text that drops a requirement is not a valid reduction.
