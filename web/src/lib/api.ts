import { createExecutionTokenClient } from "@redbamboo/utility"

const BASE = ""
export const novaExecution = createExecutionTokenClient("nova")

function getDeviceId(): string {
  const key = "leaf:installation-id"
  let id = localStorage.getItem(key) ?? localStorage.getItem("nova-device-id")
  if (!id) {
    id = crypto.randomUUID()
  }
  localStorage.setItem(key, id)
  return id
}

const _deviceId = typeof localStorage !== "undefined" ? getDeviceId() : null

/**
 * Carries the HTTP status and the server's machine-readable `error` code
 * alongside the message. Callers that need to tell one failure from another —
 * e.g. a 409 `request_not_pending`, which is an expected race rather than a
 * problem to surface — can branch on those instead of matching on prose.
 */
export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message)
    this.name = "ApiError"
  }
}

async function request<T>(method: string, path: string, body?: unknown, extraHeaders?: Record<string, string>): Promise<T> {
  const headers: Record<string, string> = { ...extraHeaders }
  if (body) headers["Content-Type"] = "application/json"
  if (_deviceId) headers["X-Leaf-Installation-Id"] = _deviceId
  const res = await novaExecution.fetch(`${BASE}${path}`, {
    method,
    credentials: "include",
    headers: Object.keys(headers).length ? headers : undefined,
    body: body ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText }))
    // Prefer the server's human-readable message while preserving its stable
    // machine code for callers that need to branch on the failure.
    throw new ApiError(res.status, err.error ?? "", err.message ?? err.error ?? res.statusText)
  }
  return res.json() as Promise<T>
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body),
  postWithHeaders: <T>(path: string, body: unknown, headers: Record<string, string>) =>
    request<T>("POST", path, body, headers),
  put: <T>(path: string, body?: unknown) => request<T>("PUT", path, body),
  patch: <T>(path: string, body?: unknown) => request<T>("PATCH", path, body),
  delete: <T>(path: string) => request<T>("DELETE", path),
}
