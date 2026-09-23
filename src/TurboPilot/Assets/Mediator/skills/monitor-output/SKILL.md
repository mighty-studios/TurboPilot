---
name: monitor-output
description: 'Flags possible repetition, contradictions, and unsupported claims in completed output. Use for cautious warnings grounded in the supplied text and evidence.'
license: Apache-2.0
---

# Monitor Output

## When to Use

Inspect `response` in light of `prompt`, `summary`, and any supplied `evidence`.

## Rules

Return `{"warnings":[{"kind":"repetition","message":"brief possible issue","quote":"exact excerpt from the response"}]}`.
Allowed kinds are `repetition`, `contradiction`, and `unsupported-claim`.
Return at most three warnings, or an empty array when no concrete issue is visible.

## Gotchas

- Every warning needs an exact supporting excerpt from the response.
- Lack of independent evidence does not prove a hallucination. Describe uncertainty.
- Do not invent verification results or claim access to files, tools, or the internet.
- Normal code repetition, quoted examples, and explicit caveats are not defects by themselves.
