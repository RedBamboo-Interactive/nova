import assert from "node:assert/strict"
import { createHash } from "node:crypto"
import { readFileSync as readFileRaw, readdirSync, statSync } from "node:fs"
import { join, resolve } from "node:path"
import test from "node:test"
import { buildMetadata, canonical, hashFile, validateInput } from "../scripts/release/metadata.mjs"

const root = resolve(import.meta.dirname, "..")
const readFileSync = (path, encoding) => {
  const value = readFileRaw(path, encoding)
  return typeof value === "string" ? value.replaceAll("\r\n", "\n") : value
}
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"))
const manifest = readJson("plugin.json")
const packageJson = readJson("web/package.json")
const producer = readJson("release/producer-input.v1.json")
const dotnetSdk = readJson("global.json")
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
  assert.equal(input.toolchain.node, "22.23.1")
  assert.equal(packageJson.engines.node, "22.23.1")
})

test("release input records only Leaf.Sdk and Nova's four actual shared package sources", () => {
  assert.deepEqual(producer.leafSdk, {
    repository: "RedBamboo-Interactive/redleaf",
    commit: "c7371b07594ae13fb7ebb4b24dbd860c20d3e14f",
    sourcePath: "src/Leaf.Sdk",
  })
  assert.equal(producer.redleafReleaseToolCommit, "c7371b07594ae13fb7ebb4b24dbd860c20d3e14f")
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
    builtAt: "2026-08-08T09:00:00.000Z",
  }
  const first = canonical(buildMetadata(input, manifest, source))
  assert.equal(first, canonical(buildMetadata(input, manifest, source)))
  assert.doesNotMatch(first, /stable|nightly|channel|run_id|run_attempt/i)
  const metadata = JSON.parse(first)
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

test("an unresolved RedLeaf pin fails closed before release output", () => {
  const placeholder = ["REPLACE", "WITH", "REDLEAF", "RELEASE", "TOOL", "COMMIT"].join("_")
  const unresolved = { ...structuredClone(producer), redleafReleaseToolCommit: placeholder }
  assert.throws(() => validateInput(unresolved, manifest, packageJson), /publication is blocked/)
  let placeholderCount = 0
  const walk = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if ([".git", "node_modules", "dist", "artifacts", "bin", "obj"].includes(entry.name)) continue
      const path = join(directory, entry.name)
      if (entry.isDirectory()) walk(path)
      else if (statSync(path).size < 1_000_000) placeholderCount += readFileSync(path, "utf8").split(placeholder).length - 1
    }
  }
  walk(root)
  assert.equal(placeholderCount, 0)
})

test("workflow uses immutable action SHAs and one channel-neutral RedLeaf ingestion", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  const actionRefs = [...workflow.matchAll(/^\s*uses:\s*[^@\s]+@([^\s#]+)/gm)].map((match) => match[1])
  assert.ok(actionRefs.length >= 7)
  for (const ref of actionRefs) assert.match(ref, /^[a-f0-9]{40}$/)
  assert.doesNotMatch(workflow, /@(main|master|v\d+)\b/i)
  assert.doesNotMatch(workflow, /github\.sha/)
  const candidateWorkflow = workflow.slice(workflow.indexOf("  candidate:"), workflow.indexOf("  bridge:"))
  assert.equal((candidateWorkflow.match(/github\.workflow_sha/g) ?? []).length, 3)
  assert.equal((workflow.match(/candidate ingest-extension/g) ?? []).length, 1)
  assert.doesNotMatch(workflow, /--channel|stable|nightly/i)
  assert.doesNotMatch(workflow, /candidate (build|finalize)|registry build|signer|id-token:\s*write/i)
  assert.doesNotMatch(workflow, /inputs\.artifact_url/i)
  assert.doesNotMatch(workflow, /central_release_tag|CENTRAL_RELEASE_TAG/)
  assert.match(workflow, /https:\/\/github\.com\/RedBamboo-Interactive\/nova\/releases\/download\/nova-unsigned-candidates\/\$artifactName/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/nova/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/redleaf/)
  assert.match(workflow, /repository: RedBamboo-Interactive\/redbamboo-packages/)
  assert.match(workflow, /node-version: 22\.23\.1/)
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
})

test("the unsigned prerelease bridge is serialized, append-only, and isolated from the candidate build", () => {
  const workflow = readFileSync(join(root, ".github/workflows/release-candidate.yml"), "utf8")
  const candidate = workflow.slice(workflow.indexOf("  candidate:"), workflow.indexOf("  bridge:"))
  const bridge = workflow.slice(workflow.indexOf("  bridge:"))
  assert.match(candidate, /contents: read/)
  assert.doesNotMatch(candidate, /contents: write|actions: read|GH_TOKEN|gh release/i)
  assert.match(bridge, /needs: candidate/)
  assert.match(bridge, /group: nova-unsigned-candidate-bridge/)
  assert.match(bridge, /cancel-in-progress: false/)
  assert.match(bridge, /actions: read/)
  assert.match(bridge, /contents: write/)
  assert.doesNotMatch(bridge, /id-token:|signing key|private key|REDLEAF_RELEASE_SIGNING_KEY/i)
  assert.match(bridge, /\$tag = 'nova-unsigned-candidates'/)
  assert.doesNotMatch(bridge, /visibility|already be public|public prerelease/i)
  assert.match(bridge, /\$\{candidateId\}\.candidate\.json/)
  assert.match(bridge, /bridge-assets\/\$artifactName/)
  assert.match(bridge, /status -ne 'unsigned'/)
  assert.match(bridge, /Extension -ceq '\.leafpkg'/)
  assert.match(bridge, /existingNames -notcontains \$file\.Name/)
  assert.doesNotMatch(bridge, /--clobber|release delete|release edit|release delete-asset/i)
  assert.match(bridge, /gh release download \$tag --pattern \$file\.Name/)
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
