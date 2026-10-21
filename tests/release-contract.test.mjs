import assert from "node:assert/strict"
import { createHash } from "node:crypto"
import { readFileSync } from "node:fs"
import { join, resolve } from "node:path"
import test from "node:test"
import { buildMetadata, canonical, hashFile, PERMISSIONS, SHARED, SLOTS, validateExactVersionOverride, validateInput } from "../scripts/release/metadata.mjs"

const root = resolve(import.meta.dirname, "..")
const read = (path) => readFileSync(join(root, path), "utf8").replaceAll("\r\n", "\n")
const readJson = (path) => JSON.parse(read(path))
const manifest = readJson("plugin.json")
const packageJson = readJson("web/package.json")
const producer = readJson("release/producer-input.v1.json")
const dotnetSdk = readJson("global.json")
const validProducer = () => structuredClone(producer)

test("automated version overrides can change only the three version fields", () => {
  const testVersion = manifest.version === "0.1.1" ? "0.1.2" : "0.1.1"
  const head = { manifest: structuredClone(manifest), input: structuredClone(producer), packageJson: structuredClone(packageJson) }
  const current = structuredClone(head)
  current.manifest.version = testVersion
  current.input.component.version = testVersion
  current.packageJson.version = testVersion
  assert.doesNotThrow(() => validateExactVersionOverride(head, current, testVersion))
  current.manifest.description = "unexpected mutation"
  assert.throws(() => validateExactVersionOverride(head, current, testVersion), /beyond the exact release version fields/)
  const inconsistent = structuredClone(head)
  inconsistent.manifest.version = testVersion
  assert.throws(() => validateExactVersionOverride(head, inconsistent, testVersion), /must match the requested version/)
  assert.throws(() => validateExactVersionOverride(head, head, manifest.version), /must change the committed version/)
})

test("Pixoo is one optional versioned backend-plus-frontend extension", () => {
  assert.doesNotThrow(() => validateInput(validProducer(), manifest, packageJson))
  assert.deepEqual(producer.component, { id: "pixoo", kind: "extension", version: manifest.version })
  assert.equal(manifest.backend.assembly, "Leaf.Plugins.Pixoo")
  assert.equal(manifest.frontend.package, "@redbamboo/plugin-pixoo")
  assert.equal(packageJson.version, manifest.version)
  assert.equal(producer.classification, "optional")
  assert.equal(producer.toolchain.node, packageJson.engines.node)
  assert.equal(packageJson.packageManager, `pnpm@${producer.toolchain.pnpm}`)
  assert.equal(dotnetSdk.sdk.version, producer.toolchain.dotnetSdk)
})

test("manifest compatibility, capabilities, slots, and permissions are reconciled exactly", () => {
  assert.equal(manifest.kernelApi, "^0.1.0")
  assert.equal(manifest.computeApi, undefined)
  assert.equal(manifest.productVersion, undefined)
  assert.deepEqual(manifest.requires, ["entities", "nova"])
  assert.deepEqual(manifest.permissions, PERMISSIONS)
  assert.deepEqual(manifest.frontend.extensions.map(({ id, target, slot }) => ({ surface: "frontend", role: "contributes", id, target, slot })), SLOTS)
})

test("permissions stay within the RedLeaf release definition permission ceiling", () => {
  // Mirrors redleaf release/definitions/pixoo.v1.json permissionCeiling.
  const ceiling = ["net:127.0.0.1:18800", "net:127.0.0.1:18804", "net:configured-pixoo-host", "os:issue-system-execution-token"]
  assert.deepEqual(PERMISSIONS.map((item) => `${item.kind}:${item.target}`).sort(), ceiling)
})

test("release inputs use Leaf.Sdk source and the shared RedBamboo packages as local links", () => {
  assert.equal(producer.leafSdk.repository, "RedBamboo-Interactive/redleaf")
  assert.equal(producer.leafSdk.sourcePath, "src/Leaf.Sdk")
  assert.equal(producer.redbamboo.repository, "RedBamboo-Interactive/redbamboo-packages")
  assert.deepEqual(producer.redbamboo.inputs.map((item) => [item.id, item.name, item.sourcePath]), SHARED)
  for (const [, name, sourcePath] of SHARED) assert.equal(packageJson.dependencies[name], `link:../../redbamboo-packages/${sourcePath}`)
  const projects = `${read(producer.backendProject)}\n${read(producer.testProject)}`
  assert.equal((projects.match(/RestorePackagesWithLockFile>true/g) ?? []).length, 2)
  assert.match(projects, /ProjectReference Include="\$\(LeafSdkProject\)"/)
  assert.doesNotMatch(projects, /AppHost|RedLeaf\.App/)
})

test("real NuGet and pnpm locks are the dependency traceability set", () => {
  for (const path of ["src/Leaf.Plugins.Pixoo/packages.lock.json", "tests/Leaf.Plugins.Pixoo.Tests/packages.lock.json", "web/pnpm-lock.yaml"]) {
    const independent = createHash("sha256").update(readFileSync(join(root, path))).digest("hex")
    assert.equal(hashFile(join(root, path)), independent)
  }
  for (const path of ["src/Leaf.Plugins.Pixoo/packages.lock.json", "tests/Leaf.Plugins.Pixoo.Tests/packages.lock.json"]) {
    assert.equal(readJson(path).dependencies["net9.0"]["leaf.sdk"].type, "Project")
  }
  assert.match(read("web/pnpm-lock.yaml"), /specifier: link:\.\.\/\.\.\/redbamboo-packages\/packages\/ui/)
})

test("metadata is deterministic, compact, manifest-derived, and channel-neutral", () => {
  const source = {
    pixooCommit: "b".repeat(40),
    pixooUrl: "https://github.com/RedBamboo-Interactive/leaf-pixoo.git",
    leafSdkUrl: "https://github.com/RedBamboo-Interactive/redleaf.git",
    redbambooUrl: "https://github.com/RedBamboo-Interactive/redbamboo-packages.git",
    builtAt: "2026-08-08T12:00:00.000Z",
  }
  const first = canonical(buildMetadata(validProducer(), manifest, source))
  assert.equal(first, canonical(buildMetadata(validProducer(), manifest, source)))
  assert.doesNotMatch(first, /stable|nightly|channel|run_id|run_attempt/i)
  const metadata = JSON.parse(first)
  assert.deepEqual(metadata.compatibility, { kernelApi: manifest.kernelApi })
  assert.deepEqual(metadata.sboms, [])
  assert.deepEqual(metadata.dependencyLocks.map((item) => item.id).sort(), ["backend-nuget-lock", "redbamboo-pnpm-lock", "tests-nuget-lock", "web-pnpm-lock"])
  assert.deepEqual(metadata.buildInputs.map((item) => item.id).sort(), ["leaf-sdk", "redbamboo-ui", "redbamboo-utility"])
})
