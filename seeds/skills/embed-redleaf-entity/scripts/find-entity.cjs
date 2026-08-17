#!/usr/bin/env node

const REDLEAF_ORIGIN = "http://127.0.0.1:18804"
const DEFAULT_LIMIT = 50
const ALLOWED_ARGUMENTS = new Set(["query", "id", "type", "limit"])

class ResolverError extends Error {
  constructor(status, message) {
    super(message)
    this.status = status
  }
}

function parseArgs(argv) {
  const args = {}
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index]
    if (!token.startsWith("--")) throw new Error(`Unexpected argument: ${token}`)
    const key = token.slice(2)
    if (!ALLOWED_ARGUMENTS.has(key)) throw new Error(`Unexpected argument: --${key}`)
    const value = argv[index + 1]
    if (!value || value.startsWith("--")) throw new Error(`Missing value for --${key}`)
    args[key] = value
    index += 1
  }
  return args
}

function normalizeIdentity(value) {
  return String(value ?? "")
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLocaleLowerCase("en")
    .replace(/[^a-z0-9]+/g, "")
}

function escapeMarkdownLabel(value) {
  return String(value).replace(/[\\[\]]/g, "\\$&")
}

function encodeSegment(value) {
  return encodeURIComponent(String(value))
}

function identityFromEntity(entity, locator) {
  const id = String(entity?.id ?? "")
  const typeSlug = String(entity?.typeSlug ?? "")
  const name = String(entity?.name ?? "")
  const slug = String(entity?.slug ?? "")
  if (!id || !typeSlug || !name) throw new Error("RedLeaf returned an incomplete entity identity")

  const entityHref = `redleaf://${encodeSegment(typeSlug)}/${encodeSegment(id)}`
  const webHref = typeof locator?.web === "string" && locator.web
    ? locator.web
    : `/database/entities/${encodeSegment(typeSlug)}/${encodeSegment(id)}`

  return {
    id,
    typeSlug,
    slug,
    name,
    href: webHref,
    embedHref: entityHref,
    embed: `[${escapeMarkdownLabel(name)}](${entityHref})`,
  }
}

async function requestJson(path, options = {}) {
  const token = process.env.REDLEAF_EXECUTION_TOKEN?.trim()
  if (!token) {
    throw new ResolverError(
      "authentication_required",
      "REDLEAF_EXECUTION_TOKEN is required")
  }

  const url = new URL(path, REDLEAF_ORIGIN)
  if (url.origin !== REDLEAF_ORIGIN) {
    throw new Error("Refusing to forward REDLEAF_EXECUTION_TOKEN outside RedLeaf")
  }

  const response = await fetch(url, {
    ...options,
    headers: {
      Accept: "application/json",
      Authorization: `Bearer ${token}`,
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...options.headers,
    },
  })
  if (!response.ok) throw new Error(`RedLeaf returned HTTP ${response.status}`)
  return response.json()
}

function emit(value, exitCode = 0) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`)
  process.exitCode = exitCode
}

async function resolveById(id) {
  const descriptor = await requestJson(`/api/entities/${encodeSegment(id)}/inspect`)
  const entity = identityFromEntity(descriptor.entity)
  return { status: "matched", entity, embed: entity.embed, matchedBy: "id" }
}

async function resolveByQuery(query, typeSlug, limit) {
  const payload = { query, sources: ["entities"], pageSize: limit }
  if (typeSlug) payload.entityTypes = [typeSlug]

  const response = await requestJson("/api/search/query", {
    method: "POST",
    body: JSON.stringify(payload),
  })
  const results = Array.isArray(response?.entities?.items) ? response.entities.items : []
  const candidates = results.map((result) => identityFromEntity(result.entity, result.locator))
  const normalizedQuery = normalizeIdentity(query)
  const exact = candidates.filter((entity) =>
    normalizeIdentity(entity.name) === normalizedQuery
      || normalizeIdentity(entity.slug) === normalizedQuery)

  if (exact.length === 1) {
    return { status: "matched", entity: exact[0], embed: exact[0].embed, matchedBy: "name-or-slug" }
  }
  if (exact.length > 1) return { status: "ambiguous", candidates: exact.slice(0, 10) }
  if (candidates.length === 1) return { status: "candidate", candidates }
  if (candidates.length > 1) return { status: "ambiguous", candidates: candidates.slice(0, 10) }
  return { status: "not_found", candidates: [] }
}

async function main() {
  let args
  try {
    args = parseArgs(process.argv.slice(2))
  } catch (error) {
    emit({ status: "error", error: String(error?.message ?? error) }, 2)
    return
  }

  const query = args.query?.trim()
  const id = args.id?.trim()
  if ((!query && !id) || (query && id)) {
    emit({ status: "error", error: "Supply exactly one of --query or --id" }, 2)
    return
  }

  const parsedLimit = Number.parseInt(args.limit ?? String(DEFAULT_LIMIT), 10)
  const limit = Number.isFinite(parsedLimit) ? Math.min(Math.max(parsedLimit, 1), 100) : DEFAULT_LIMIT

  try {
    const result = id
      ? await resolveById(id)
      : await resolveByQuery(query, args.type?.trim(), limit)
    emit(result, result.status === "not_found" ? 1 : 0)
  } catch (error) {
    emit({
      status: error instanceof ResolverError ? error.status : "error",
      error: String(error?.message ?? error),
    }, 1)
  }
}

void main()
