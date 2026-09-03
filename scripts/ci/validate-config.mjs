import fs from 'node:fs';
import { parse } from 'yaml';

const requiredFiles = [
  '.gitlab-ci.yml',
  '.releaserc.json',
  '.config/dotnet-tools.json',
  'docs/docfx.json',
  'docs/images/logo.png',
  'README.md',
  'CHANGELOG.md',
  '.env.example',
  'scripts/ci/prepare-release.sh',
  'scripts/ci/publish-packages.sh',
  'scripts/ci/run-tests.ps1',
  'scripts/ci/validate-packages.sh'
];

for (const file of requiredFiles) {
  if (!fs.existsSync(file)) throw new Error(`Missing required release file: ${file}`);
}

const pipeline = parse(fs.readFileSync('.gitlab-ci.yml', 'utf8'));
for (const job of ['build', 'test:x64', 'test:x86', 'docs', 'package', 'pages', 'release']) {
  if (!pipeline[job]) throw new Error(`GitLab pipeline is missing job '${job}'.`);
}
if (pipeline.variables?.GIT_DEPTH !== '0') throw new Error('semantic-release requires GIT_DEPTH=0.');
const workflowRules = JSON.stringify(pipeline.workflow?.rules);
if (!workflowRules.includes('CI_OPEN_MERGE_REQUESTS')) {
  throw new Error('workflow.rules must suppress duplicate branch and merge-request pipelines.');
}
if (JSON.stringify(pipeline.build.script).includes('PACKAGE_VERSION')) {
  throw new Error('The regular build job must not consume the package-only version variable.');
}
const packageScript = JSON.stringify(pipeline.package.script);
if (!packageScript.includes('export PACKAGE_VERSION=') || !packageScript.includes('-p:Version=')) {
  throw new Error('The package job must stamp its CI version into assemblies and packages.');
}
if (!pipeline['test:x64'].artifacts?.reports?.junit || !pipeline['test:x86'].artifacts?.reports?.junit) {
  throw new Error('Both architecture jobs must publish JUnit reports.');
}
if (pipeline.pages?.pages?.publish !== 'docs/_site') {
  throw new Error('GitLab Pages must publish the generated DocFX site.');
}

const publishJob = pipeline.release;
if (publishJob.id_tokens?.NUGET_ID_TOKEN?.aud !== 'https://www.nuget.org') {
  throw new Error('The release job must request a NuGet.org-scoped GitLab OIDC token.');
}
if (publishJob.environment?.name !== 'nuget.org') {
  throw new Error('The release job must include the nuget.org environment claim.');
}
if (pipeline.variables?.NUGET_TOKEN_ENDPOINT !== 'https://www.nuget.org/api/v2/token') {
  throw new Error('The NuGet trusted-publishing token endpoint is missing or incorrect.');
}
if (pipeline.variables?.NUGET_SOURCE !== 'https://api.nuget.org/v3/index.json') {
  throw new Error('Use NuGet.org V3 so symbol packages are published with primary packages.');
}
if (!JSON.stringify(publishJob.script).includes('npm run release')) {
  throw new Error('The release job must use the repository-pinned semantic-release command.');
}

const release = JSON.parse(fs.readFileSync('.releaserc.json', 'utf8'));
if (!release.branches?.includes('main')) throw new Error('semantic-release must release from main.');
if (!JSON.stringify(release).includes('prepare-release.sh')) throw new Error('semantic-release does not prepare NuGet packages.');
if (!JSON.stringify(release).includes('publish-packages.sh')) throw new Error('semantic-release does not publish NuGet packages.');
if (!JSON.stringify(release).includes('artifacts/packages/*.snupkg')) {
  throw new Error('GitLab releases must include symbol-package assets.');
}
const pluginNames = release.plugins.map(plugin => Array.isArray(plugin) ? plugin[0] : plugin);
const gitIndex = pluginNames.indexOf('@semantic-release/git');
const execIndex = pluginNames.indexOf('@semantic-release/exec');
const gitlabIndex = pluginNames.indexOf('@semantic-release/gitlab');
if (!(gitIndex >= 0 && gitIndex < execIndex && execIndex < gitlabIndex)) {
  throw new Error('Release order must commit the changelog, build/publish packages, then create the GitLab release.');
}

const buildProps = fs.readFileSync('Directory.Build.props', 'utf8');
for (const property of ['IncludeSymbols', 'SymbolPackageFormat', 'PublishRepositoryUrl', 'Deterministic']) {
  if (!buildProps.includes(`<${property}>`)) {
    throw new Error(`Directory.Build.props is missing release property '${property}'.`);
  }
}
if (buildProps.includes('$(MSBuildThisFileDirectory)logo.png')) {
  throw new Error('The package icon path points to a nonexistent root logo.png.');
}

const publishScript = fs.readFileSync('scripts/ci/publish-packages.sh', 'utf8');
if (!publishScript.includes('NUGET_ID_TOKEN') || !publishScript.includes('NUGET_USERNAME')) {
  throw new Error('NuGet publishing is not configured for GitLab OIDC trusted publishing.');
}
if (publishScript.includes('NUGET_API_KEY')) {
  throw new Error('A long-lived NUGET_API_KEY is still referenced by the publishing script.');
}

const toolManifest = JSON.parse(fs.readFileSync('.config/dotnet-tools.json', 'utf8'));
if (!toolManifest.tools?.docfx) throw new Error('The local DocFX tool is not pinned.');

const docfx = JSON.parse(fs.readFileSync('docs/docfx.json', 'utf8'));
if (!docfx.metadata || !docfx.build) throw new Error('DocFX needs metadata and build sections.');

console.log('GitLab CI, semantic-release, and DocFX configuration parsed successfully.');
