---
name: rewrite-prompt
description: 'Condenses a user prompt before forwarding it. Use for brevity and clarity without answering the request or losing constraints.'
license: Apache-2.0
---

# Reword a Prompt

## When to Use

Use when the input contains a `prompt` that will be forwarded unchanged in meaning.
`attachments` lists the names of attached files for reference only.

## Rules

Remove pleasantries, redundant wording, and repetition. Preserve every requested
action, condition, exception, identifier, literal, file path, number, and negation.
Keep every sentence's request, including when to stop, wait, or report back.
Leave ambiguous references ambiguous rather than guessing their meaning.
Do not add solutions, assumptions, names, or a plan.

Copy every string in `protectedText` exactly into the reduced prompt. It includes
constraint words such as `no`, `only`, `before`, `after`, and `yet`. Keep numbers
as written: `3` must not become `three`. Preserve the entire meaning of every
restriction, including what must not be edited or executed.

Return `{"prompt":"shorter equivalent wording","meaningPreserved":true}`.
If no safe reduction is possible, return the original prompt and set
`meaningPreserved` to false.

Example:
Input: "Please summarize README.md in 3 bullets without editing any files. Thanks."
Output: `{"prompt":"Summarize README.md in 3 bullets without editing any files.","meaningPreserved":true}`
Invalid: "Summarize README.md in three bullets" drops the no-edit restriction and changes a number.
Invalid: dropping "We will discuss them after you share your review" removes a request to stop and wait.

## Gotchas

- Never rewrite code, commands, quoted literals, or paths.
- Shorter text that drops a requirement is not a valid reduction.
- Do not name a project, product, or folder that the prompt itself does not name.
