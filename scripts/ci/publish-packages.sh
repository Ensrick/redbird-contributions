#!/usr/bin/env bash
set -euo pipefail

version="${1:?semantic-release did not provide a version}"
: "${NUGET_ID_TOKEN:?GitLab did not provide the NUGET_ID_TOKEN OIDC token}"
: "${NUGET_USERNAME:?Set NUGET_USERNAME to your NuGet.org profile name (not an email address)}"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
  echo "Refusing to publish invalid package version: $version" >&2
  exit 1
fi

command -v curl >/dev/null || { echo 'Trusted publishing requires curl.' >&2; exit 1; }
command -v jq >/dev/null || { echo 'Trusted publishing requires jq.' >&2; exit 1; }

token_endpoint="${NUGET_TOKEN_ENDPOINT:-https://www.nuget.org/api/v2/token}"
source_url="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
response_file="$(mktemp)"
api_key=''

cleanup() {
  api_key=''
  rm -f -- "$response_file"
}
trap cleanup EXIT

request_body="$(jq -cn --arg username "$NUGET_USERNAME" '{username: $username, tokenType: "ApiKey"}')"
http_status="$(curl --silent --show-error \
  --output "$response_file" \
  --write-out '%{http_code}' \
  --request POST "$token_endpoint" \
  --header 'Content-Type: application/json' \
  --header "Authorization: Bearer $NUGET_ID_TOKEN" \
  --data "$request_body")"

if [[ ! "$http_status" =~ ^2[0-9][0-9]$ ]]; then
  echo "NuGet trusted-publishing token exchange failed with HTTP $http_status." >&2
  jq -r '.message // .error // "NuGet.org returned no error description."' "$response_file" >&2 || true
  exit 1
fi

if ! api_key="$(jq -er '.apiKey | select(type == "string" and length > 0)' "$response_file")"; then
  echo 'NuGet.org returned no temporary API key.' >&2
  exit 1
fi

shopt -s nullglob
packages=(artifacts/packages/*."$version".nupkg)
if [[ ${#packages[@]} -eq 0 ]]; then
  echo "No NuGet packages found for version $version." >&2
  exit 1
fi

for package in "${packages[@]}"; do
  # NuGet.org automatically publishes the matching .snupkg when the V3 source is used.
  dotnet nuget push "$package" --source "$source_url" --api-key "$api_key" --skip-duplicate
done

# Mirror release packages to this project's GitLab NuGet registry using GitLab's short-lived job
# token. No additional GitLab package-registry secret is required.
if [[ -n "${CI_JOB_TOKEN:-}" && -n "${CI_API_V4_URL:-}" && -n "${CI_PROJECT_ID:-}" ]]; then
  gitlab_source="$CI_API_V4_URL/projects/$CI_PROJECT_ID/packages/nuget/index.json"
  for package in "${packages[@]}"; do
    dotnet nuget push "$package" --source "$gitlab_source" --api-key "$CI_JOB_TOKEN" --skip-duplicate
  done
fi
