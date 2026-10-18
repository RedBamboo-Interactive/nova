/** Display name for the application that owns a discussion Nova only shows. */
export function formatOwningApp(app: string | null | undefined): string {
  const name = app?.trim()
  if (!name) return "another app"
  return name.charAt(0).toUpperCase() + name.slice(1)
}
