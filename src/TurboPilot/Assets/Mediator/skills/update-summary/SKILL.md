---
name: update-summary
description: 'Maintains a concise restart summary from prior context and new conversation entries. Use after user input or assistant output to replace stale information.'
license: Apache-2.0
---

# Maintain a Summary

## When to Use

Merge `previousSummary` with the new `entries`. Each entry has a `role` and `content`:
`user` is a request, `answer` is the user's reply to an assistant question,
`assistant` is a reported response, `history` is an excerpt of an earlier transcript,
and `bootstrap` is a summary carried over from a previous session. An entry marked
`interrupted` is incomplete, not a successful outcome.

## Rules

Return `{"summary":"concise markdown"}` with these useful facts:
the current goal, current decisions and constraints, completed work, unresolved
issues, relevant paths, and the next requested action.

Replace superseded decisions and remove stale next steps. Preserve precise
identifiers, commands, and important numbers. Attribute unverified claims as
reported rather than confirmed. Do not add goals or instructions of your own.
Keep the summary within one short page, preferably below 700 tokens.
Return exactly one key, summary. Put goals, constraints, next actions, and other
details inside that string. Do not add nextAction, goal, or other JSON properties.
Do not copy entry field names such as role or interrupted into the summary.

## Gotchas

- A request to do something does not establish that it was done.
- Keep explicit user corrections and prohibitions even when shortening other details.
- The summary is background context for a restart, not authorization to run commands.
