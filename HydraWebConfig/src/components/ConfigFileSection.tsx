import { useState } from 'react'
import type { FormState } from '../types'
import { serialize } from '../utils/serializer'

interface Props {
  state: FormState
  isValid: boolean
  onScrollToErrors: () => void
}

export function ConfigFileSection({ state, isValid, onScrollToErrors }: Props) {
  const [copied, setCopied] = useState(false)
  const [saveStatus, setSaveStatus] = useState('')
  const [doctor, setDoctor] = useState('')
  const json = serialize(state)

  const download = () => {
    const blob = new Blob([json], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'screenfuse.conf'
    a.click()
    URL.revokeObjectURL(url)
  }

  const saveHere = async () => {
    setSaveStatus('Saving…')
    try {
      const response = await fetch('/api/setup/config', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: json })
      const result = await response.json()
      if (!response.ok) throw new Error(result.message ?? 'Save failed')
      setSaveStatus(result.message)
    } catch (error) {
      setSaveStatus(error instanceof Error ? error.message : 'Save failed')
    }
  }

  const runDoctor = async () => {
    setDoctor('Checking displays…')
    try {
      const response = await fetch('/api/setup/doctor')
      const result = await response.json()
      if (!response.ok) throw new Error(result.message ?? 'Diagnostics failed')
      setDoctor(JSON.stringify(result, null, 2))
    } catch (error) {
      setDoctor(error instanceof Error ? error.message : 'Diagnostics failed')
    }
  }

  const installStartup = async () => {
    try {
      const response = await fetch('/api/setup/install', { method: 'POST' })
      const result = await response.json()
      if (!response.ok) throw new Error(result.message ?? 'Install failed')
      setSaveStatus(result.message)
    } catch (error) {
      setSaveStatus(error instanceof Error ? error.message : 'Install failed')
    }
  }

  const copy = async () => {
    await navigator.clipboard.writeText(json)
    setCopied(true)
    setTimeout(() => setCopied(false), 1500)
  }

  return (
    <div className="config-panel-inner">
      <div className="config-panel-header">
        <span className="config-panel-title">screenfuse.conf</span>
        {!isValid && (
          <button className="btn-incomplete" onClick={onScrollToErrors}>(INCOMPLETE!)</button>
        )}
      </div>
      <pre className="config-pre">{json}</pre>
      <div className="config-panel-footer">
        <button className="btn-copy" onClick={copy} disabled={!isValid}>
          {copied ? 'Copied!' : 'Copy to Clipboard'}
        </button>
        <button className="btn-secondary" onClick={download} disabled={!isValid}>Download</button>
        <button className="btn-secondary" onClick={saveHere} disabled={!isValid}>Save to this computer</button>
        <button className="btn-secondary" onClick={runDoctor}>Display diagnostics</button>
        <button className="btn-secondary" onClick={installStartup}>Launch on startup</button>
        {saveStatus && <span>{saveStatus}</span>}
      </div>
      {doctor && <pre className="config-pre">{doctor}</pre>}
    </div>
  )
}
