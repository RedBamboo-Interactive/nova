import { createHash } from "node:crypto"
import { readFileSync, statSync, writeFileSync } from "node:fs"
import { resolve } from "node:path"
import { spawnSync } from "node:child_process"
import { fileURLToPath } from "node:url"

const PLACEHOLDER_PREFIX = "REPLACE_WITH_"
const INPUT_KEYS = ["backendProject", "classification", "component", "dependencies", "frontendDirectory", "inputType", "leafSdk", "redbamboo", "redleafReleaseToolCommit", "runtimeRequirements", "schemaVersion", "targetPlatform", "testProject", "toolchain"]

function fail(message) { throw new Error(message) }
function json(path) { return JSON.parse(readFileSync(path, "utf8")) }
function keys(value) { return Object.keys(value).sort() }
function exactKeys(value, expected, label) {
  if (!value || typeof value !== "object" || Array.isArray(value) || JSON.stringify(keys(value)) !== JSON.stringify([...expected].sort())) fail(`${label} has unexpected or missing fields.`)
}
function canonical(value) {
  const sort = (item) => Array.isArray(item) ? item.map(sort) : item && typeof item === "object"
    ? Object.fromEntries(keys(item).map((key) => [key, sort(item[key])])) : item
  return `${JSON.stringify(sort(value), null, 2)}\n`
}
function ordinal(a, b) { return a < b ? -1 : a > b ? 1 : 0 }
function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, encoding: "utf8", windowsHide: true })
  if (result.error || result.status !== 0) fail((result.stderr || result.stdout || result.error?.message || `${command} failed`).trim())
  return result.stdout.trim()
}
function git(root, ...args) { return run("git", ["-C", root, ...args]) }
function hashFile(path) { return createHash("sha256").update(readFileSync(path)).digest("hex") }
function facts(path) { return { sizeBytes: statSync(path).size, sha256: hashFile(path) } }
function cleanGit(root, label) {
  if (git(root, "status", "--porcelain=v1", "--untracked-files=all")) fail(`${label} checkout must be clean.`)
}
export function validateExactVersionOverride(head, current, expectedVersion) {
  const headVersion = head.manifest.version
  if (head.input.component.version !== headVersion || head.packageJson.version !== headVersion) fail("Committed Nova version fields are inconsistent.")
  if (expectedVersion === headVersion) fail("An ephemeral release override must change the committed version.")
  if (current.manifest.version !== expectedVersion || current.input.component.version !== expectedVersion || current.packageJson.version !== expectedVersion) fail("Ephemeral Nova version fields must match the requested version.")
  const restored = structuredClone(current)
  restored.manifest.version = headVersion
  restored.input.component.version = headVersion
  restored.packageJson.version = headVersion
  if (canonical(restored.manifest) !== canonical(head.manifest)
      || canonical(restored.input) !== canonical(head.input)
      || canonical(restored.packageJson) !== canonical(head.packageJson)) fail("Nova checkout contains changes beyond the exact release version fields.")
}
function cleanNovaGit(root, input, manifest, packageJson) {
  const status = git(root, "status", "--porcelain=v1", "--untracked-files=all")
  if (!status) return
  const expectedPaths = ["plugin.json", "release/producer-input.v1.json", "web/package.json"]
  const entries = status.split(/\r?\n/)
  if (entries.length !== expectedPaths.length
      || entries.some((entry) => !entry.startsWith(" M "))
      || entries.map((entry) => entry.slice(3)).sort(ordinal).some((path, index) => path !== expectedPaths[index])) fail("Nova checkout must be clean except for the exact workflow-owned version fields.")
  const atHead = (path) => JSON.parse(git(root, "show", `HEAD:${path}`))
  validateExactVersionOverride({
    manifest: atHead("plugin.json"),
    input: atHead("release/producer-input.v1.json"),
    packageJson: atHead("web/package.json"),
  }, { manifest, input, packageJson }, input.component.version)
}
function exactCommit(value, label) {
  if (!/^[a-f0-9]{40}$/.test(value)) fail(`${label} must be one full lowercase commit SHA.`)
}
function addOptional(target, key, value) { if (value !== undefined && value !== null) target[key] = value }

export function validateInput(input, manifest, packageJson) {
  exactKeys(input, INPUT_KEYS, "producer input")
  exactKeys(input.component, ["id", "kind", "version"], "component")
  exactKeys(input.toolchain, ["dotnetSdk", "msbuild", "node", "pnpm"], "toolchain")
  exactKeys(input.targetPlatform, ["architecture", "operatingSystem"], "targetPlatform")
  exactKeys(input.leafSdk, ["commit", "repository", "sourcePath"], "Leaf.Sdk")
  exactKeys(input.redbamboo, ["commit", "inputs", "lockPath", "repository"], "redbamboo")
  if (input.schemaVersion !== 1 || input.inputType !== "nova-extension-release-producer-input") fail("Unsupported producer input.")
  if (input.classification !== "protected") fail("Nova must be classified protected.")
  if (input.redleafReleaseToolCommit.startsWith(PLACEHOLDER_PREFIX)) fail("RedLeaf release-tool commit pin is unresolved; publication is blocked.")
  exactCommit(input.redleafReleaseToolCommit, "RedLeaf release-tool pin")
  exactCommit(input.leafSdk.commit, "Leaf.Sdk pin")
  exactCommit(input.redbamboo.commit, "RedBamboo pin")
  if (input.leafSdk.repository !== "RedBamboo-Interactive/redleaf" || input.leafSdk.sourcePath !== "src/Leaf.Sdk") fail("Leaf.Sdk repository or source path is invalid.")
  if (input.redbamboo.repository !== "RedBamboo-Interactive/redbamboo-packages" || input.redbamboo.lockPath !== "pnpm-lock.yaml") fail("RedBamboo repository or lock path is invalid.")
  if (input.targetPlatform.operatingSystem !== "windows" || input.targetPlatform.architecture !== "x64") fail("Nova release layout must target windows/x64.")
  if (input.backendProject !== "src/Leaf.Plugins.Nova/Leaf.Plugins.Nova.csproj" || input.testProject !== "tests/Leaf.Plugins.Nova.Tests/Leaf.Plugins.Nova.Tests.csproj" || input.frontendDirectory !== "web") fail("Nova backend, test, or frontend release path is invalid.")
  if (input.component.id !== "nova" || input.component.kind !== "extension" || input.component.version !== manifest.version) fail("Producer component identity must match plugin.json.")
  if (manifest.id !== input.component.id || !manifest.kernelApi || manifest.backend?.assembly !== "Leaf.Plugins.Nova" || !manifest.frontend) fail("plugin.json must describe the Nova backend-plus-frontend extension.")
  if (manifest.frontend.package !== packageJson.name || packageJson.name !== "@redbamboo/plugin-nova" || packageJson.version !== manifest.version) fail("Nova frontend identity or version does not match plugin.json.")
  if (manifest.build !== undefined) fail("Source plugin.json must not contain release build evidence.")
  if (packageJson.packageManager !== `pnpm@${input.toolchain.pnpm}` || packageJson.engines?.node !== input.toolchain.node) fail("package.json Node/pnpm pins do not match producer input.")
  const expected = [
    ["redbamboo-chat", "@redbamboo/chat", "packages/chat"],
    ["redbamboo-ui", "@redbamboo/ui", "packages/ui"],
    ["redbamboo-utility", "@redbamboo/utility", "packages/utility"],
    ["redbamboo-workflow", "@redbamboo/workflow", "packages/workflow"],
  ]
  const shared = input.redbamboo.inputs
  if (!Array.isArray(shared) || JSON.stringify(shared.map((item) => [item.id, item.name, item.sourcePath])) !== JSON.stringify(expected)) fail("RedBamboo fan-in must be exactly chat, ui, utility, and workflow.")
  for (const [index, item] of shared.entries()) exactKeys(item, ["id", "name", "sourcePath"], `redbamboo.inputs[${index}]`)
  const declaredShared = Object.entries(packageJson.dependencies).filter(([name]) => name.startsWith("@redbamboo/"))
  if (JSON.stringify(declaredShared.map(([name]) => name).sort()) !== JSON.stringify(expected.map(([, name]) => name).sort())) fail("Frontend @redbamboo dependency fan-in drifted.")
  for (const [, name, sourcePath] of expected) {
    if (packageJson.dependencies[name] !== `link:../../redbamboo-packages/${sourcePath}`) fail(`${name} must remain the local development link to its declared source path.`)
  }
  if (!Array.isArray(input.dependencies) || input.dependencies.length !== 0 || !Array.isArray(input.runtimeRequirements) || input.runtimeRequirements.length !== 0) fail("Nova currently has no extension dependencies or packaged runtime requirements.")
}

export function buildMetadata(input, manifest, source) {
  const compatibility = { kernelApi: manifest.kernelApi }
  addOptional(compatibility, "computeApi", manifest.computeApi)
  addOptional(compatibility, "productVersion", manifest.productVersion)
  return {
    schemaVersion: 1,
    metadataType: "redleaf-extension-release-metadata",
    classification: input.classification,
    compatibility,
    targetPlatform: input.targetPlatform,
    buildId: `source-${source.novaCommit}`,
    builtAt: source.builtAt,
    repository: { id: input.component.id, repositoryUrl: source.novaUrl, commit: source.novaCommit },
    toolchain: { ...input.toolchain, releaseTool: input.redleafReleaseToolCommit },
    build: { backendProject: input.backendProject, frontendDirectory: input.frontendDirectory },
    buildInputs: [
      { id: "leaf-sdk", repositoryUrl: source.leafSdkUrl, commit: input.leafSdk.commit, sourcePath: input.leafSdk.sourcePath },
      ...input.redbamboo.inputs.map((item) => ({ id: item.id, repositoryUrl: source.redbambooUrl, commit: input.redbamboo.commit, sourcePath: item.sourcePath })),
    ].sort((a, b) => ordinal(a.id, b.id)),
    dependencyLocks: [
      { id: "backend-nuget-lock", ecosystem: "nuget", path: "src/Leaf.Plugins.Nova/packages.lock.json" },
      { id: "redbamboo-pnpm-lock", ecosystem: "npm", path: "redbamboo-packages/pnpm-lock.yaml" },
      { id: "tests-nuget-lock", ecosystem: "nuget", path: "tests/Leaf.Plugins.Nova.Tests/packages.lock.json" },
      { id: "web-pnpm-lock", ecosystem: "npm", path: "web/pnpm-lock.yaml" },
    ].sort((a, b) => ordinal(a.path, b.path)),
    sboms: [],
    dependencies: input.dependencies,
    runtimeRequirements: input.runtimeRequirements,
  }
}

export function validateDescriptor(descriptor, metadata, artifactPath, lockFiles, expectedVersion) {
  const artifact = facts(artifactPath)
  if (descriptor.componentId !== "nova" || descriptor.version !== expectedVersion || descriptor.classification !== "protected" || descriptor.componentKind !== "extension") fail("Descriptor Nova identity/classification is invalid.")
  if (canonical(descriptor.compatibility) !== canonical(metadata.compatibility) || canonical(descriptor.evidence.buildInputs) !== canonical(metadata.buildInputs)) fail("Descriptor compatibility or build inputs drifted from metadata.")
  if (descriptor.evidence.repository.commit !== metadata.repository.commit || descriptor.evidence.build.backendProject !== "src/Leaf.Plugins.Nova/Leaf.Plugins.Nova.csproj" || descriptor.evidence.build.frontendDirectory !== "web") fail("Descriptor source or build layout is invalid.")
  if (descriptor.artifact.sha256 !== artifact.sha256 || descriptor.artifact.sizeBytes !== artifact.sizeBytes) fail("Descriptor artifact facts do not match actual bytes.")
  const expectedLocks = metadata.dependencyLocks.map((item) => ({ ...item, ...facts(lockFiles[item.id]) })).sort((a, b) => ordinal(a.path, b.path))
  if (canonical(descriptor.evidence.dependencyLocks) !== canonical(expectedLocks)) fail("Descriptor lock hashes do not match actual lockfiles.")
  if (descriptor.sboms.length !== 0) fail("Nova does not declare an audit/SBOM output.")
}

function args(values) {
  const parsed = new Map()
  for (let i = 0; i < values.length; i += 2) {
    if (!values[i]?.startsWith("--") || values[i + 1] === undefined) fail("Arguments must use --name value.")
    parsed.set(values[i].slice(2), values[i + 1])
  }
  return (name, optional = false) => parsed.get(name) ?? (optional ? undefined : fail(`--${name} is required.`))
}

function collect(get) {
  const repository = resolve(get("repository"))
  const redbamboo = resolve(get("redbamboo"))
  const leafSdk = resolve(get("leaf-sdk"))
  const redleaf = resolve(get("redleaf"))
  const input = json(get("input"))
  const manifest = json(resolve(repository, "plugin.json"))
  const packageJson = json(resolve(repository, "web/package.json"))
  validateInput(input, manifest, packageJson)
  cleanNovaGit(repository, input, manifest, packageJson)
  cleanGit(redbamboo, "RedBamboo")
  cleanGit(leafSdk, "Leaf.Sdk")
  cleanGit(redleaf, "RedLeaf release tool")
  const novaCommit = git(repository, "rev-parse", "HEAD")
  const redbambooCommit = git(redbamboo, "rev-parse", "HEAD")
  const leafSdkCommit = git(leafSdk, "rev-parse", "HEAD")
  const redleafCommit = git(redleaf, "rev-parse", "HEAD")
  if (redbambooCommit !== input.redbamboo.commit || leafSdkCommit !== input.leafSdk.commit || redleafCommit !== input.redleafReleaseToolCommit) fail("A checkout does not match its immutable producer pin.")
  git(leafSdk, "cat-file", "-e", `HEAD:${input.leafSdk.sourcePath}`)
  for (const item of input.redbamboo.inputs) git(redbamboo, "cat-file", "-e", `HEAD:${item.sourcePath}`)
  const sourceEpoch = get("source-date-epoch")
  if (!/^\d+$/.test(sourceEpoch) || sourceEpoch !== git(repository, "show", "-s", "--format=%ct", "HEAD")) fail("SOURCE_DATE_EPOCH must equal the exact Nova commit timestamp.")
  const builtAt = new Date(Number(sourceEpoch) * 1000).toISOString()
  const pnpm = process.platform === "win32"
    ? run(process.env.ComSpec ?? "cmd.exe", ["/d", "/s", "/c", "corepack.cmd pnpm --version"])
    : run("corepack", ["pnpm", "--version"])
  const actual = { node: process.version.slice(1), pnpm, dotnetSdk: run("dotnet", ["--version"]), msbuild: run("dotnet", ["msbuild", "-version", "-nologo"]).split(/\r?\n/).at(-1) }
  for (const [name, value] of Object.entries(input.toolchain)) if (actual[name] !== value) fail(`Expected ${name} ${value}, got ${actual[name]}.`)
  const metadata = buildMetadata(input, manifest, {
    novaCommit,
    novaUrl: git(repository, "config", "--get", "remote.origin.url"),
    leafSdkUrl: git(leafSdk, "config", "--get", "remote.origin.url"),
    redbambooUrl: git(redbamboo, "config", "--get", "remote.origin.url"),
    builtAt,
  })
  return {
    metadata,
    locks: {
      "backend-nuget-lock": resolve(repository, "src/Leaf.Plugins.Nova/packages.lock.json"),
      "redbamboo-pnpm-lock": resolve(redbamboo, input.redbamboo.lockPath),
      "tests-nuget-lock": resolve(repository, "tests/Leaf.Plugins.Nova.Tests/packages.lock.json"),
      "web-pnpm-lock": resolve(repository, "web/pnpm-lock.yaml"),
    },
  }
}

function main() {
  const mode = process.argv[2]
  const get = args(process.argv.slice(3))
  const state = collect(get)
  for (const id of Object.keys(state.locks).sort()) process.stdout.write(`${id} sha256=${hashFile(state.locks[id])}\n`)
  if (mode === "generate") {
    writeFileSync(get("output"), canonical(state.metadata), { encoding: "utf8", flag: "wx" })
  } else if (mode === "validate") {
    const metadata = json(get("metadata"))
    if (canonical(metadata) !== canonical(state.metadata)) fail("Generated metadata is not deterministic for the checked-out inputs.")
    validateDescriptor(json(get("descriptor")), metadata, get("artifact"), state.locks, json(resolve(get("repository"), "plugin.json")).version)
  } else fail("Mode must be generate or validate.")
}

if (process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1])) {
  try { main() } catch (error) { process.stderr.write(`release metadata: ${error.message}\n`); process.exitCode = 1 }
}

export { canonical, facts, hashFile }
