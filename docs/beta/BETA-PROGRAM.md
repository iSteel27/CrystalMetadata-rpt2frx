# rpt2frx — Beta program playbook (for the maintainer)

## Distribution
| Piece | Where | Cost |
|---|---|---|
| Installer `rpt2frxSetup-0.1.0-beta.N.exe` | GitHub Releases (private repo + invite testers, or public "pre-release") | free |
| Landing page + sign-up form | Netlify free plan (static page linking to the release) | free |
| Docs | `docs/beta/GETTING-STARTED.md` published as a page on the same site | free |
| Feedback | Google Form / GitHub Issues template | free |

Build: `powershell -File installer\build-installer.ps1 -Version 0.1.0-beta.N`, then sign the installer if possible (otherwise tell testers to expect the SmartScreen "unknown publisher" warning). Publish a SHA-256 hash next to the download.

## Before inviting anyone
- [ ] Compile and test the installer on a clean Windows VM: with and without the SAP runtime, per-user install, PATH task, uninstall removes PATH entry.
- [ ] Confirm SAP licence position (you distribute only your own code; testers install SAP's runtime themselves — see THIRD-PARTY-NOTICES).
- [ ] Have a tester terms note (below) and a privacy statement: nothing leaves the PC.

## Tester terms (short)
- Pre-release software, provided as is, may produce incorrect templates — verify before production use.
- Do not send confidential reports or data; share screenshots with data masked.
- Testers need their own valid SAP Crystal runtime licence/terms.
- Feedback may be used to improve the product; no rights to the software are transferred.

## Sign-up form questions
Name/email · Windows version · Crystal version you use · how many reports to convert · biggest report types (subreports, groups, charts?) · target FastReport edition · may we contact you? · confidentiality acknowledged.

## Feedback form (after first use)
Did install work? · did `doctor` pass (paste output) · report converted OK? (1–5 layout accuracy) · what differed from Crystal (screenshots) · warnings seen · would you pay, and how much.

## Release notes template
`Version · date · new · fixed · known issues · upgrade note (install over previous)`.

## Support flow
Ask for: `rpt2frx --version`, `rpt2frx doctor` output, newest log from `%LOCALAPPDATA%\CrystalMetadata\logs`, the warning list. Consider adding a `rpt2frx support-bundle` command that zips those automatically.
