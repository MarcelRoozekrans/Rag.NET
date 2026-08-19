# Project Conventions

> Written by `init-conventions`. Do not hand-edit — re-run the sub-skill instead; the Commit & Release Protocol reads these fields.

**Established:** 2026-08-19

## Stack

**Language / runtime:** .NET 10, C#
**Package manager:** NuGet
**Framework:** none (a library, not an application)
**Datastore:** n/a (pluggable — eight vector store packages; the GraphRAG graph store is SQLite)

## Commits

**Format:** conventional
**Scopes:** free
**Scope source:** n/a
**Fallback when scope not allowed:** omit scope

## Branching

**Model:** feature-branch
**PR required:** yes (ruleset "Main branch active" on `main` carries a `pull_request` rule; the legacy `/protection` endpoint 404s, so this was read from `repos/{owner}/{repo}/rules/branches/main`)
**Protected branches:** main

## Versioning & Release

**Scheme:** semver
**Released by:** release-please
**Milestone completion tags a release:** no
**Changelog:** auto

## Deployment

**Deploy target:** nuget.org
**Environments:** none
**Deployed by:** release-please and GitHub Actions
