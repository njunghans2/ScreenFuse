import type { DisplayRoutingConfig, MonitorInputConfig } from '../types'

interface Props {
  routing: DisplayRoutingConfig
  onChange: (routing: DisplayRoutingConfig) => void
}
export function DisplayRoutingEditor({ routing, onChange }: Props) {
  const inputs = routing.inputs ?? []
  const updateInput = (index: number, patch: Partial<MonitorInputConfig>) =>
    onChange({ ...routing, inputs: inputs.map((item, i) => i === index ? { ...item, ...patch } : item) })

  return (
    <div className="section">
      <h2>Physical Display Routing</h2>
      <p className="hint">Commands run on this computer when this profile is activated as a desk scene. Add the same scene name to every computer.</p>
      {inputs.map((input, i) => (
        <div className="field-row" key={i}>
          <div className="field flex-grow">
            <label>Monitor ID</label>
            <input value={input.id} placeholder="* / DELL / 1 / bus:6 / display UUID"
              onChange={e => updateInput(i, { id: e.target.value })} />
          </div>
          <div className="field">
            <label>Input value</label>
            <input type="number" min={0} max={255} value={input.input}
              onChange={e => updateInput(i, { input: Number(e.target.value) })} />
          </div>
          <button className="btn-ghost" onClick={() => onChange({ ...routing, inputs: inputs.filter((_, n) => n !== i) })}>Remove</button>
        </div>
      ))}
      <button className="btn-ghost" onClick={() => onChange({ ...routing, inputs: [...inputs, { id: '*', input: 15 }] })}>+ Add monitor input</button>
      <div className="checkbox-group" style={{ marginTop: 16 }}>
        <label className="checkbox-label"><input type="checkbox" checked={routing.wakeDisplays === true}
          onChange={e => onChange({ ...routing, wakeDisplays: e.target.checked || undefined, sleepDisplays: e.target.checked ? undefined : routing.sleepDisplays })} />Wake all displays</label>
        <label className="checkbox-label"><input type="checkbox" checked={routing.sleepDisplays === true}
          onChange={e => onChange({ ...routing, sleepDisplays: e.target.checked || undefined, wakeDisplays: e.target.checked ? undefined : routing.wakeDisplays })} />Sleep all displays (auto-input fallback)</label>
      </div>
      <div className="field" style={{ marginTop: 12, maxWidth: 220 }}>
        <label>Settle delay (ms)</label>
        <input type="number" min={0} max={10000} value={routing.settleDelayMs ?? 500}
          onChange={e => onChange({ ...routing, settleDelayMs: Number(e.target.value) })} />
      </div>
    </div>
  )
}
