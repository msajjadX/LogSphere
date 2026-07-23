# Security Policy

LogSphere handles logs, audit trails and potentially sensitive operational data, so security
reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Instead, use GitHub's private
["Report a vulnerability"](../../security/advisories/new) form on this repository.

Include: affected version/commit, reproduction steps, and impact. You can expect an
acknowledgement within a few days.

## Scope notes for deployers

- The ingestion API authenticates via per-application keys (`X-LogSphere-Key`); tenant/project/
  application/environment identity is always resolved server-side from the key and cannot be
  spoofed by payloads.
- Sensitive values (passwords, tokens, card numbers, …) are sanitized server-side before storage;
  redaction rules are configurable per project.
- Set strong values for every entry in `deploy/.env` (`JWT_SECRET`, `KEY_PEPPER`,
  `SANITIZATION_HASH_KEY`, DB and admin passwords) — the examples are placeholders, not defaults.
- Keep the dashboard behind TLS in production and restrict database access to the API host.
