# Contributing

## Prerequisites

- .NET 10 SDK (X64)
- PowerShell 7 on Windows
- Node.js 22.22.2 or newer for release-configuration validation
- A GitLab Runner tagged `windows` for the X64/X86 execution jobs

The X86 test script installs a repository-local .NET 10 X86 runtime under `.tools/`.
it does not modify the machine-wide SDK installation.

## Local validation

```powershell
dotnet restore RedBird.slnx
dotnet build RedBird.slnx -c Release --no-restore
./scripts/ci/run-tests.ps1 -Architecture X64
./scripts/ci/run-tests.ps1 -Architecture X86
./scripts/ci/build-docs.ps1
npm ci
npm run validate:config
```

Create local packages with an explicit non-release version:

```powershell
dotnet pack RedBird.slnx -c Release --no-build -p:PackageVersion=0.0.0-local.1 --output artifacts/packages
```

## Commit and release policy

Use Conventional Commits, for example:

- `fix(native-x64): relocate suspended instruction pointers`
- `feat(vectorcall): support four-member HVA returns`
- `docs: add transaction example`
- `feat!: remove obsolete dispatcher compatibility API`

`fix` produces a patch release, `feat` a minor release, and `!` or a `BREAKING CHANGE` footer a major release. 
Do not manually create release tags or edit package versions.