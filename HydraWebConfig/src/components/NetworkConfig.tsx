import type { HydraProfile, NetworkType, EmbeddedStyxConfig, EmbeddedStyxServerConfig } from '../types'

interface Props {
  config: HydraProfile
  onChange: (patch: Partial<HydraProfile>) => void
}

const TYPE_LABELS: Record<NetworkType, string> = {
  config: 'Styx relay (base64)',
  embeddedStyx: 'Embedded Styx client',
  embeddedStyxServer: 'Embedded Styx server',
}

export function NetworkConfig({ config, onChange }: Props) {
  const type = config.networkType ?? 'config'

  function setType(t: NetworkType) {
    onChange({ networkType: t })
  }

  function patchStyx(patch: Partial<EmbeddedStyxConfig>) {
    onChange({ embeddedStyx: { server: '', password: '', ...config.embeddedStyx, ...patch } })
  }

  function patchStyxServer(patch: Partial<EmbeddedStyxServerConfig>) {
    onChange({ embeddedStyxServer: { port: 5000, password: '', ...config.embeddedStyxServer, ...patch } })
  }

  function secureSecret() {
    const bytes = crypto.getRandomValues(new Uint8Array(32))
    return btoa(String.fromCharCode(...bytes)).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '')
  }

  return (
    <div className="network-config">
      <div className="network-type-group">
        {(Object.keys(TYPE_LABELS) as NetworkType[]).map(t => (
          <label key={t} className={`network-type-btn${type === t ? ' active' : ''}`}>
            <input
              type="radio"
              name="networkType"
              value={t}
              checked={type === t}
              onChange={() => setType(t)}
            />
            {TYPE_LABELS[t]}
          </label>
        ))}
      </div>

      {type === 'config' && (
        <div className="field mt-10">
          <textarea
            value={config.networkConfig ?? ''}
            placeholder="Base64-encoded config string from Styx"
            onChange={e => onChange({ networkConfig: e.target.value || undefined })}
            rows={3}
          />
        </div>
      )}

      {type === 'embeddedStyx' && (
        <div className="field-row mt-10">
          <div className="field flex-grow">
            <label>Server</label>
            <input
              type="text"
              value={config.embeddedStyx?.server ?? ''}
              placeholder="auto://studio or http://192.168.1.10:5000"
              onChange={e => patchStyx({ server: e.target.value })}
            />
          </div>
          <div className="field flex-grow">
            <label>Password</label>
            <input
              type="password"
              value={config.embeddedStyx?.password ?? ''}
              placeholder="shared relay password"
              onChange={e => patchStyx({ password: e.target.value })}
            />
            <button type="button" className="btn-secondary" onClick={() => patchStyx({ password: secureSecret() })}>Generate secure secret</button>
          </div>
        </div>
      )}

      {type === 'embeddedStyxServer' && (
        <div className="field-row mt-10">
          <div className="field flex-grow">
            <label>LAN desk name</label>
            <input
              type="text"
              maxLength={64}
              value={config.embeddedStyxServer?.discoveryName ?? ''}
              placeholder="studio (enables auto://studio discovery)"
              onChange={e => patchStyxServer({ discoveryName: e.target.value || undefined })}
            />
          </div>
          <div className="field">
            <label>Port</label>
            <input
              type="number"
              min="1024"
              max="65535"
              value={config.embeddedStyxServer?.port ?? 5000}
              onChange={e => patchStyxServer({ port: Number(e.target.value) })}
            />
          </div>
          <div className="field flex-grow">
            <label>Password</label>
            <input
              type="password"
              value={config.embeddedStyxServer?.password ?? ''}
              placeholder="shared relay password"
              onChange={e => patchStyxServer({ password: e.target.value })}
            />
            <button type="button" className="btn-secondary" onClick={() => patchStyxServer({ password: secureSecret() })}>Generate secure secret</button>
          </div>
        </div>
      )}
    </div>
  )
}
