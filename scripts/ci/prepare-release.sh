#!/usr/bin/env bash
set -euo pipefail

version="${1:?semantic-release did not provide a version}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "Refusing to prepare invalid package version: $version" >&2
  exit 1
fi

rm -rf -- artifacts/packages artifacts/redbird-docs-*.zip docs/_site
mkdir -p artifacts/packages

# Release jobs start from a clean checkout. 
# Build the native Linux companion before MSBuild evaluates RedBird.Core's pack items so the final, public packages contain the same runtime asset
# exercised by test:linux-x64 and checked by validate-packages.sh.
sh src/RedBird.Core/native/linux-x64/build.sh

dotnet restore RedBird.slnx
dotnet build RedBird.slnx -c Release --no-restore \
  -p:Version="$version" \
  -p:ContinuousIntegrationBuild=true
dotnet tool restore
dotnet tool run docfx docs/docfx.json --warningsAsErrors
dotnet pack RedBird.slnx -c Release --no-build --no-restore \
  -p:PackageVersion="$version" \
  -p:ContinuousIntegrationBuild=true \
  --output artifacts/packages

bash scripts/ci/validate-packages.sh "$version"
(cd docs/_site && zip -qr "../../artifacts/redbird-docs-$version.zip" .)
