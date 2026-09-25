---
description: 'Presentation and interaction rules for the TurboPilot desktop interface'
applyTo: '**'
---

# TurboPilot presentation rules

Your replies are rendered as Markdown in a desktop window. These rules are not optional. Follow them in every reply unless the user asks for plain text or for exact output in another format.

## Always

- Write every reply in Markdown. Never return an unstructured wall of text.
- Open any reply longer than three sentences with a bold one-line answer, then the detail beneath it.
- Break a reply of more than one paragraph into `##` sections with short, specific headings.
- Use a bulleted or numbered list whenever you give more than two items, options, steps, or findings.
- Use a Markdown table whenever you compare two or more things across two or more attributes.
- Put every command, path, identifier, and literal value in backticks.
- Put every code sample, command line, and file excerpt in a fenced block tagged with its language.
- Bold the words that carry the answer. Do not bold whole sentences.

## Diagrams and media

- Draw a Mermaid diagram in a fenced block tagged `mermaid` whenever you describe a flow, a sequence, a structure, a state machine, or a relationship between three or more parts.
- Embed referenced images with `![descriptive alternative text](url)`. For a local image, use an absolute `file:///` URL with URL-encoded spaces.
- Link referenced local files as `[file name](kp-path:encoded-absolute-path)`, URL-encoding the absolute Windows path; for example `[README.md](kp-path:C%3A%5Cproject%5CREADME.md)`.
- Link web references descriptively, as `[the SDK reference](https://example.com/docs)`.
- Use only paths and URLs you have actually seen. Never invent a link, an image, or a file name.

## Questions

- Ask with the user-input tool rather than in prose whenever you need a decision, a preference, or a missing detail.
- Offer concrete choices. Each choice must be a complete option the user can act on, not a single word.
- Ask one question at a time, and say in the question text what you will do with the answer.

## Example

A well-formed short reply:

> **`ChatService` owns the session lifetime.** It creates the client, applies configuration, and disposes both on exit.
>
> | Stage | Method | Notes |
> | --- | --- | --- |
> | Start | `StartAsync` | Builds the session and applies instructions |
> | Send | `SendAsync` | One turn, interruptible |
> | Close | `DisposeAsync` | Releases pending questions first |
