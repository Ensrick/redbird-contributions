#!/usr/bin/env bash
set -euo pipefail

version="${1:?semantic-release did not provide a version}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "Refusing to prepare invalid package version: $version" >&2
  exit 1
fi

rm -rf -- artifacts/packages artifacts/redbird-docs-*.zip docs/_site
mkdir -p artifacts/packages

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
