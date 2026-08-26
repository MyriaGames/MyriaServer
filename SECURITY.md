# Security Policy

## Reporting a Vulnerability

If you find a security vulnerability in this project, please report it privately instead of
opening a public issue — this gives time to fix it before it's public knowledge.

**Email: rllyben@proton.me**

Please include:

- A description of the vulnerability and its potential impact (e.g. what data or functionality
  it could expose or affect).
- Steps to reproduce it, or a proof of concept if you have one.
- Which repository/component it affects, if it's not obvious (Myria's client and server code is
  split across several repositories under the [MyriaGames](https://github.com/MyriaGames)
  organization).

You'll get an acknowledgment as soon as possible. This is a solo hobby project maintained in
spare time, so response times vary, but every report is read and taken seriously. If the report
is valid, I'll aim to have a fix out before any public disclosure, and will credit you (unless
you'd rather stay anonymous) once it's resolved.

## Scope

This applies to the code in this repository and its sibling Myria repositories. It does **not**
cover:

- Social-engineering or physical-access attacks against the operator.
- Findings that require access to another player's account credentials to exploit (report those
  as a bug, not a vulnerability, unless the credential exposure itself is the finding).
- Denial-of-service testing against any live, running instance of the game's servers — please
  don't actually run this against a live deployment; describe it in your report instead.

## Supported Versions

This project is in active alpha development. Only the latest code on each repository's `master`
branch is supported — please confirm an issue still reproduces there before reporting.
