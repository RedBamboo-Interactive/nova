import { useState, useEffect, useRef } from "react"

interface Props {
  title: string
  onRename: (title: string) => void
}

/** Discussion title that turns into an inline input on click. Enter/blur saves, Escape cancels. */
export function EditableTitle({ title, onRename }: Props) {
  const [editing, setEditing] = useState(false)
  const [value, setValue] = useState(title)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    setValue(title)
  }, [title])

  useEffect(() => {
    if (editing) {
      inputRef.current?.focus()
      inputRef.current?.select()
    }
  }, [editing])

  const commit = () => {
    setEditing(false)
    const next = value.trim()
    if (next && next !== title) onRename(next)
    else setValue(title)
  }

  if (editing) {
    return (
      <input
        ref={inputRef}
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") commit()
          if (e.key === "Escape") {
            setValue(title)
            setEditing(false)
          }
        }}
        className="flex-1 min-w-0 bg-transparent text-[14px] font-medium text-contrast outline-none border-b border-border-a50"
        aria-label="Discussion title"
      />
    )
  }

  return (
    <button
      onClick={() => setEditing(true)}
      className="flex-1 min-w-0 flex items-center gap-2 text-left group/title"
      title="Rename discussion"
    >
      <span className="text-[14px] font-medium text-contrast truncate">{title}</span>
      <i className="fa-solid fa-pen text-[10px] text-text-muted opacity-0 group-hover/title:opacity-100 transition-opacity shrink-0" />
    </button>
  )
}
