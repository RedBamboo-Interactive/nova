import assert from "node:assert/strict"
import { createHash } from "node:crypto"
import { readFileSync as readFileRaw } from "node:fs"
import { join, resolve } from "node:path"
import test from "node:test"
import { buildMetadata, canonical, checkDependencyCommits, hashFile, validateExactVersionOverride, validateInput } from "../scripts/release/metadata.mjs"

const root = resolve(import.meta.dirname, "..")
const readFileSync = (path, encoding) => {
  const value = readFileRaw(path, encoding)
  return typeof value === "string" ? value.replaceAll("\r\n", "\n") : value
}
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"))
const manifest = readJson("plugin.json")
const packageJson = readJson("web/package.json")

test("git porcelain keeps its leading status column", () => {
  const metadataSource = readFileSync(join(root, "scripts/release/metadata.mjs"), "utf8")
  assert.match(metadataSource, /return result\.stdout\.trimEnd\(\)/)
  assert.doesNotMatch(metadataSource, /return result\.stdout\.trim\(\)/)
})
const producer = readJson("release/producer-input.v1.json")
const dotnetSdk = readJson("global.json")

test("cross-repository bridge retains the scoped producer token", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  assert.match(workflow, /GH_TOKEN: \$\{\{ github\.repository == 'RedBamboo-Interactive\/nova' && github\.token \|\| secrets\.CROSS_REPO_TOKEN \}\}/)
})
const validProducer = () => structuredClone(producer)

test("repository SDK selection is exact and the packer runs inside the component checkout", () => {
  assert.deepEqual(dotnetSdk, {
    sdk: {
      version: producer.toolchain.dotnetSdk,
      rollForward: "disable",
      allowPrerelease: false,
    },
  })
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8").replaceAll("\r\n", "\n")
  assert.match(workflow, /workflow_call:\n    inputs:\n      version:/)
  assert.match(workflow, /workflow_dispatch:\n    inputs:\n      version:/)
  assert.match(workflow, /Requested version already matches the committed source; no override is needed\./)
  assert.match(workflow, /Apply requested release version ephemerally/)
  assert.match(workflow, /\$producer\.component\.version = \$env:REQUESTED_VERSION/)
  assert.match(workflow, /\$package\.version = \$env:REQUESTED_VERSION/)
  assert.match(workflow, /name: Invoke [^\n]*RedLeaf[^\n]*\n\s+working-directory: nova/)
  assert.doesNotMatch(workflow, /-notcmatch/)
  assert.match(readFileSync(join(root, ".gitignore"), "utf8"), /^artifacts\/$/m)
})

test("Nova is a protected, versioned backend-plus-frontend extension", () => {
  const input = validProducer()
  assert.doesNotThrow(() => validateInput(input, manifest, packageJson))
  assert.deepEqual(input.component, { id: "nova", kind: "extension", version: manifest.version })
  assert.equal(manifest.backend.assembly, "Leaf.Plugins.Nova")
  assert.equal(manifest.frontend.package, "@redbamboo/plugin-nova")
  assert.equal(packageJson.version, manifest.version)
  assert.equal(input.classification, "protected")
  assert.equal(input.toolchain.node, "22.23.4")
  assert.equal(packageJson.engines.node, "22.23.4")
  assert.deepEqual(input.dependencies, [
    { id: "redleaf", versionRange: ">=0.1.25 <0.2.0" },
  ])
})

test("automated version stamping permits only the three exact version fields", () => {
  const head = { manifest, input: producer, packageJson }
  const current = structuredClone(head)
  current.manifest.version = "0.1.99"
  current.input.component.version = "0.1.99"
  current.packageJson.version = "0.1.99"
  assert.doesNotThrow(() => validateExactVersionOverride(head, current, "0.1.99"))

  const extraChange = structuredClone(current)
  extraChange.manifest.description = "unexpected mutation"
  assert.throws(() => validateExactVersionOverride(head, extraChange, "0.1.99"), /beyond the exact release version fields/)

  const inconsistent = structuredClone(current)
  inconsistent.packageJson.version = manifest.version
  assert.throws(() => validateExactVersionOverride(head, inconsistent, "0.1.99"), /must match the requested version/)
  assert.throws(() => validateExactVersionOverride(head, head, manifest.version), /must change the committed version/)
})

test("release input names only Leaf.Sdk and Nova's four actual shared package sources, never their commits", () => {
  assert.deepEqual(producer.leafSdk, {
    repository: "RedBamboo-Interactive/redleaf",
    sourcePath: "src/Leaf.Sdk",
  })
  assert.equal("redleafReleaseToolCommit" in producer, false)
  assert.deepEqual(Object.keys(producer.redbamboo).sort(), ["inputs", "lockPath", "repository"])
  assert.deepEqual(producer.redbamboo.inputs.map(({ id, name, sourcePath }) => ({ id, name, sourcePath })), [
    { id: "redbamboo-chat", name: "@redbamboo/chat", sourcePath: "packages/chat" },
    { id: "redbamboo-ui", name: "@redbamboo/ui", sourcePath: "packages/ui" },
    { id: "redbamboo-utility", name: "@redbamboo/utility", sourcePath: "packages/utility" },
    { id: "redbamboo-workflow", name: "@redbamboo/workflow", sourcePath: "packages/workflow" },
  ])
  const project = readFileSync(join(root, "src/Leaf.Plugins.Nova/Leaf.Plugins.Nova.csproj"), "utf8")
  assert.match(project, /RestorePackagesWithLockFile>true/)
  assert.match(project, /ProjectReference Include="\$\(LeafSdkProject\)"/)
  assert.doesNotMatch(project, /AppHost/)
})

test("real NuGet and pnpm locks are the only dependency traceability", () => {
  const backendLock = readJson("src/Leaf.Plugins.Nova/packages.lock.json")
  const testsLock = readJson("tests/Leaf.Plugins.Nova.Tests/packages.lock.json")
  assert.equal(backendLock.dependencies["net9.0"]["leaf.sdk"].type, "Project")
  assert.equal(testsLock.dependencies["net9.0"]["Microsoft.NET.Test.Sdk"].resolved, "17.11.1")
  const lockPath = join(root, "web/pnpm-lock.yaml")
  assert.equal(hashFile(lockPath), createHash("sha256").update(readFileSync(lockPath)).digest("hex"))
  const lock = readFileSync(lockPath, "utf8")
  for (const packageName of ["chat", "ui", "utility", "workflow"]) assert.match(lock, new RegExp(`specifier: link:\\.\\./\\.\\./redbamboo-packages/packages/${packageName}`))
})

test("metadata bytes are deterministic and channel-neutral", () => {
  const input = validProducer()
  const source = {
    novaCommit: "b".repeat(40),
    novaUrl: "https://github.com/RedBamboo-Interactive/nova",
    leafSdkUrl: "https://github.com/RedBamboo-Interactive/redleaf",
    redbambooUrl: "https://github.com/RedBamboo-Interactive/redbamboo-packages",
    redleafCommit: "1".repeat(40),
    redbambooCommit: "2".repeat(40),
    builtAt: "2026-08-08T09:00:00.000Z",
  }
  const first = canonical(buildMetadata(input, manifest, source))
  assert.equal(first, canonical(buildMetadata(input, manifest, source)))
  assert.doesNotMatch(first, /stable|nightly|channel|run_id|run_attempt/i)
  const metadata = JSON.parse(first)
  // The builder-supplied commits are recorded exactly where the signed record and announcements read them.
  assert.equal(metadata.toolchain.releaseTool, "1".repeat(40))
  assert.deepEqual(Object.fromEntries(metadata.buildInputs.map((item) => [item.id, item.commit])), {
    "leaf-sdk": "1".repeat(40),
    "redbamboo-chat": "2".repeat(40),
    "redbamboo-ui": "2".repeat(40),
    "redbamboo-utility": "2".repeat(40),
    "redbamboo-workflow": "2".repeat(40),
  })
  assert.deepEqual(metadata.build, { backendProject: "src/Leaf.Plugins.Nova/Leaf.Plugins.Nova.csproj", frontendDirectory: "web" })
  assert.deepEqual(metadata.sboms, [])
  assert.deepEqual(metadata.dependencyLocks.map((item) => item.id).sort(), ["backend-nuget-lock", "redbamboo-pnpm-lock", "tests-nuget-lock", "web-pnpm-lock"])
  assert.deepEqual(metadata.dependencyLocks.map((item) => item.path), [
    "redbamboo-packages/pnpm-lock.yaml",
    "src/Leaf.Plugins.Nova/packages.lock.json",
    "tests/Leaf.Plugins.Nova.Tests/packages.lock.json",
    "web/pnpm-lock.yaml",
  ])
  assert.deepEqual(metadata.buildInputs.map((item) => item.id).sort(), ["leaf-sdk", "redbamboo-chat", "redbamboo-ui", "redbamboo-utility", "redbamboo-workflow"])
})

test("a committed dependency commit fails closed before release output", () => {
  const pinned = [
    { ...validProducer(), redleafReleaseToolCommit: "1".repeat(40) },
    { ...validProducer(), leafSdk: { ...producer.leafSdk, commit: "1".repeat(40) } },
    { ...validProducer(), redbamboo: { ...producer.redbamboo, commit: "2".repeat(40) } },
  ]
  for (const input of pinned) assert.throws(() => validateInput(input, manifest, packageJson), /builder inputs/)
})

test("the builder-supplied commits must be exact and name the checkouts that were built", () => {
  const redleaf = "1".repeat(40)
  const redbamboo = "2".repeat(40)
  const checkouts = { leafSdk: redleaf, redleaf, redbamboo }
  assert.doesNotThrow(() => checkDependencyCommits({ redleaf, redbamboo }, checkouts))
  assert.throws(() => checkDependencyCommits({ redleaf: "1".repeat(7), redbamboo }, checkouts), /--redleaf-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo: "A".repeat(40) }, checkouts), /--redbamboo-commit must be one full lowercase commit SHA/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, leafSdk: "3".repeat(40) }), /Leaf\.Sdk checkout is at 3{40}, but the builder supplied 1{40}/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redleaf: "3".repeat(40) }), /RedLeaf release tool checkout/)
  assert.throws(() => checkDependencyCommits({ redleaf, redbamboo }, { ...checkouts, redbamboo: "3".repeat(40) }), /RedBamboo checkout/)
})

test("workflow uses immutable action SHAs and one channel-neutral RedLeaf ingestion", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  const actionRefs = [...workflow.matchAll(/^\s*uses:\s*[^@\s]+@([^\s#]+)/gm)].map((match) => match[1])
  assert.ok(actionRefs.length >= 7)
  for (const ref of actionRefs) assert.match(ref, /^[a-f0-9]{40}$/)
  assert.doesNotMatch(workflow, /@(main|master|v\d+)\b/i)
  assert.doesNotMatch(workflow, /github\.sha/)
  const candidateWorkflow = workflow.slice(workflow.indexOf("  candidate:"), workflow.indexOf("  bridge:"))
  assert.ok((candidateWorkflow.match(/inputs\.source_commit \|\| github\.workflow_sha/g) ?? []).length >= 3)
  assert.equal((workflow.match(/candidate ingest-extension/g) ?? []).length, 1)
  assert.doesNotMatch(workflow, /--channel|stable|nightly/i)
  assert.doesNotMatch(workflow, /candidate (build|finalize)|registry build|signer|id-token:\s*write/i)
  assert.doesNotMatch(workflow, /inputs\.artifact_url/i)
  assert.doesNotMatch(workflow, /central_release_tag|CENTRAL_RELEASE_TAG/)
  assert.match(workflow, /https:\/\/github\.com\/RedBamboo-Interactive\/nova\/releases\/download\/nova-unsigned-candidates\/\$artifactName/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/nova/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/redleaf/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/redbamboo-packages/)
  assert.match(workflow, /node-version: 22\.23\.4/)
  assert.match(workflow, /PSVersionTable\.PSVersion\.Major -ne 7/)
  assert.match(workflow, /corepack pnpm install --frozen-lockfile/g)
  const releaseToolRestore = workflow.indexOf("dotnet restore tools/RedLeaf.ReleaseTool/RedLeaf.ReleaseTool.csproj --locked-mode --nologo")
  const releaseToolBuild = workflow.indexOf("dotnet build tools/RedLeaf.ReleaseTool/RedLeaf.ReleaseTool.csproj --configuration Release --no-restore --nologo")
  const firstReleaseToolUse = Math.min(
    workflow.indexOf("../redleaf-release/scripts/build-leafpkg-release.ps1"),
    workflow.indexOf("../redleaf-release/tools/RedLeaf.ReleaseTool/bin/Release/net9.0/RedLeaf.ReleaseTool.dll"),
  )
  assert.ok(releaseToolRestore >= 0, "workflow must locked-restore the pinned RedLeaf release tool")
  assert.ok(releaseToolBuild > releaseToolRestore, "workflow must build the release tool after restoring it")
  assert.ok(firstReleaseToolUse > releaseToolBuild, "workflow must build the release tool before the packer or DLL is used")
  assert.match(workflow, /-AllowEphemeralVersionOverride/)
})

test("release production builds against RedLeaf and RedBamboo main resolved at dispatch and recorded", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8").replaceAll("\r\n", "\n")
  const candidate = workflow.slice(workflow.indexOf("  candidate:"), workflow.indexOf("  bridge:"))
  assert.doesNotMatch(workflow, /redleafReleaseToolCommit|leafSdk\.commit|redbamboo\.commit|producer pin/)
  assert.match(candidate, /repository: RedBamboo-Interactive\/redbamboo-packages\n\s+ref: main\n\s+path: redbamboo-packages\n/)
  assert.match(candidate, /repository: RedBamboo-Interactive\/redleaf\n\s+ref: main\n\s+path: redleaf-release\n/)
  // Leaf.Sdk is the same RedLeaf commit as the release tool, never a second resolution of main.
  assert.match(candidate, /repository: RedBamboo-Interactive\/redleaf\n\s+ref: \$\{\{ steps\.dependencies\.outputs\.redleaf \}\}\n\s+path: leaf-sdk\n/)
  assert.ok(candidate.indexOf("path: leaf-sdk") > candidate.indexOf("id: dependencies"))
  assert.match(candidate, /\$redleaf = \(git -C redleaf-release rev-parse HEAD\)\.Trim\(\)/)
  assert.match(candidate, /\$redbamboo = \(git -C redbamboo-packages rev-parse HEAD\)\.Trim\(\)/)
  assert.match(candidate, /GITHUB_STEP_SUMMARY/)
  // Both metadata modes receive the resolved commits, through the environment rather than an expression.
  for (const mode of ["generate", "validate"]) {
    const step = candidate.slice(candidate.indexOf(`node scripts/release/metadata.mjs ${mode}`))
    assert.match(step.slice(0, step.indexOf("throw")), /--redleaf-commit \$env:REDLEAF_COMMIT `\n\s+--redbamboo-commit \$env:REDBAMBOO_COMMIT `/)
  }
  assert.equal((candidate.match(/REDLEAF_COMMIT: \$\{\{ steps\.dependencies\.outputs\.redleaf \}\}/g) ?? []).length, 2)
  assert.equal((candidate.match(/REDBAMBOO_COMMIT: \$\{\{ steps\.dependencies\.outputs\.redbamboo \}\}/g) ?? []).length, 2)
})

test("release production rejects every frontend native-command failure", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8").replaceAll("\r\n", "\n")

  const checkedCommands = [
    ["corepack enable", "Corepack enable failed."],
    ["corepack prepare pnpm@11.0.9 --activate", "Corepack did not prepare pnpm 11.0.9."],
    ["corepack pnpm install --frozen-lockfile", "Shared package frozen restore failed."],
    ["corepack pnpm --filter @redbamboo/chat --filter @redbamboo/ui --filter @redbamboo/utility --filter @redbamboo/workflow build", "Shared package build failed."],
    ["corepack pnpm install --frozen-lockfile", "Nova frontend frozen restore failed."],
    ["corepack pnpm run typecheck", "Nova frontend typecheck failed."],
    ["corepack pnpm run build:pkg", "Nova frontend build failed."],
    ["corepack pnpm test", "Nova frontend tests failed."],
  ]
  for (const [command, error] of checkedCommands) {
    const guarded = `${command}\n          if ($LASTEXITCODE -ne 0) { throw '${error}' }`
    assert.ok(workflow.includes(guarded), `${command} must fail the release immediately`)
  }
})

test("the unsigned prerelease bridge is serialized, append-only, and isolated from the candidate build", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  const candidate = workflow.slice(workflow.indexOf("  candidate:"), workflow.indexOf("  bridge:"))
  const bridge = workflow.slice(workflow.indexOf("  bridge:"))
  assert.equal((workflow.match(/runs-on: windows-2025/g) ?? []).length, 1)
  assert.equal((workflow.match(/runs-on: ubuntu-24\.04/g) ?? []).length, 1)
  assert.equal((workflow.match(/enableCrossOsArchive: true/g) ?? []).length, 2)
  assert.match(candidate, /contents: read/)
  assert.doesNotMatch(candidate, /contents: write|actions: read|GH_TOKEN|gh release/i)
  assert.match(bridge, /needs: candidate/)
  assert.match(bridge, /group: nova-unsigned-candidate-bridge/)
  assert.match(bridge, /cancel-in-progress: false/)
  assert.match(bridge, /actions: read/)
  assert.match(bridge, /contents: write/)
  assert.match(candidate, /actions\/cache\/save@[a-f0-9]{40}/)
  assert.match(bridge, /actions\/cache\/restore@[a-f0-9]{40}/)
  assert.match(bridge, /fail-on-cache-miss: true/)
  assert.doesNotMatch(workflow, /actions\/(?:upload|download)-artifact@/)
  assert.doesNotMatch(bridge, /id-token:|signing key|private key|REDLEAF_RELEASE_SIGNING_KEY/i)
  assert.match(bridge, /\$tag = 'nova-unsigned-candidates'/)
  assert.doesNotMatch(bridge, /visibility|already be public|public prerelease/i)
  assert.match(bridge, /\$\{candidateId\}\.candidate\.json/)
  assert.match(bridge, /bridge-assets\/\$artifactName/)
  assert.match(bridge, /status -ne 'unsigned'/)
  assert.match(bridge, /Extension -ceq '\.leafpkg'/)
  assert.match(bridge, /\.Length -eq \[long\]\$candidate\.artifact\.sizeBytes/)
  assert.match(bridge, /Get-FileHash -LiteralPath \$_\.FullName -Algorithm SHA256/)
  assert.match(bridge, /existingNames -notcontains \$file\.Name/)
  assert.doesNotMatch(bridge, /--clobber|release delete|release edit|release delete-asset/i)
  assert.match(bridge, /gh release download \$tag --repo \$env:GH_REPO --pattern \$file\.Name/)
  assert.match(bridge, /Get-FileHash -LiteralPath \$downloaded -Algorithm SHA256/)
})

test("release package inspection permits only the staged application layout and rejects private state", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  const inspection = workflow.slice(workflow.indexOf("Inspect exact package inventory"), workflow.indexOf("Invoke RedLeaf canonical candidate path"))
  assert.match(inspection, /plugin\\\.json\|backend\/.\+\|web\/dist\/.\+\|seeds\/.\+\|payload\/.\+\|provision\\\.json\|release\/extension-build-evidence\\\.v1\\\.json/)
  for (const forbidden of [
    "memory", "identity", "config", "discussion", "transcript", "events", "outfit", "image", "audio", "assets",
    "upload", "reference", "automation", "credential", "token", "provider", "cache", "log", "database",
    "node_modules", "scratch", "REDLEAF_SCRATCH_DIR", ".env", "environment",
  ]) assert.match(inspection, new RegExp(forbidden, "i"))
  assert.match(inspection, /nova-package-inventory\.txt/)
})

test("repository contains no competing candidate, signing, registry, or SBOM implementation", () => {
  const implementation = readFileSync(join(root, "scripts/release/metadata.mjs"), "utf8")
  assert.doesNotMatch(implementation, /candidateId|payloadSha256|signatureDomain|registry snapshot|channel pointer/i)
  assert.doesNotMatch(implementation, /private key|sign(?:ature|ing) input|cyclonedx/i)
})

test("Vite release layout is explicit", () => {
  const vite = readFileSync(join(root, "web/vite.config.ts"), "utf8")
  assert.match(vite, /target: "es2023"/)
  assert.match(vite, /emptyOutDir: true/)
  assert.match(vite, /sourcemap: false/)
  assert.match(vite, /fileName: \(\) => "plugin\.js"/)
})
