# Sherland.Aspire.Testing.Xunit Skills Distribution

This repository ships package-consumer AI guidance for `Sherland.Aspire.Testing.Xunit` in the NuGet package itself.

## Canonical Skill Source

The canonical source is:

- `source/Sherland.Aspire.Testing.Xunit/skills/SKILL.md`

Additional package guidance:

- No additional migration skill file is shipped.

## Packaging Contract

The package project packs `skills/**` into the nupkg under `skills/`.

This enables discovery by tools that scan package payloads, including `nuget-skills` and NuGet-backed Agent Skills tooling.

## Update Policy

When public behavior changes in `source/Sherland.Aspire.Testing.Xunit/*.cs`, update `skills/SKILL.md` in the same PR.

## Release Checklist

1. Pack the project and confirm `skills/SKILL.md` exists in the nupkg.
2. Verify `nuget-skills scan` detects `Sherland.Aspire.Testing.Xunit` as a local skill source.
3. Verify `nuget-skills load Sherland.Aspire.Testing.Xunit` shows current content.
4. Smoke test `agentskills-cli` NuGet-based consumption path for the package.
5. Confirm `source/Sherland.Aspire.Testing.Xunit/README.md` includes end-user onboarding commands for both toolchains.
