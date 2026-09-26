# SignPath Foundation application preparation

Status: preparation only. No SignPath application has been submitted.

## Project details

- Project: QuotaArc
- Public repository: https://github.com/u2loveme/QuotaArc
- Website and download page: https://vivibureau.pp.ua/quotaarc
- License: Mozilla Public License 2.0
- Release page: https://github.com/u2loveme/QuotaArc/releases/tag/v0.9.0
  (expected URL; it is not live until publication).
- Purpose: local Windows dashboard for Codex quota status and locally observed
  token usage.
- Build: Windows x64, .NET 10 SDK, `dotnet publish` with the project command
  in the repository README; package using `scripts/package-quotaarc.ps1`.
- Release artifact: `QuotaArc-0.9.0-win-x64.zip`; checksum is published in
  `SHA256SUMS.txt`.
- GitHub Actions: no signing or release workflow is configured; the beta build
  is produced and validated locally with the documented scripts.

## Before applying

- Confirm the public repository and release are live and maintained.
- Enable MFA and configure team roles for the SignPath account.
- Publish a code-signing policy on the project website/download page.
- Confirm with SignPath that the bundled Windows App SDK and WebView2
  redistributables meet its Foundation eligibility rules.
- Add the approved SignPath workflow and verify a signed build before
  changing the beta download artifact.

This document does not claim approval, eligibility, or that any binary is
signed. The 0.9.0 beta ZIP is unsigned; users should verify its published
SHA-256 checksum and must not disable Windows security protections.
