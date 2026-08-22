import type { HydraProfile, HostConfig } from '../types'
import { deriveHostsFromLayout } from './layout'

export interface ValidationError {
  path: string
  message: string
}

function isUnconditional(p: HydraProfile): boolean {
  if (!p.conditions) return true
  const { ssid, screenCount, isPluggedIn } = p.conditions
  return !ssid && screenCount === undefined && isPluggedIn === undefined
}

// returns the effective hosts for a profile (from layout or explicit)
function effectiveHosts(p: HydraProfile): HostConfig[] {
  if (p.layoutItems && p.layoutItems.length > 0) {
    return deriveHostsFromLayout(p.layoutItems)
  }
  return p.hosts ?? []
}

export function validate(profiles: HydraProfile[]): ValidationError[] {
  const errors: ValidationError[] = []

  // check duplicate condition tuples
  const seen = new Set<string>()
  profiles.forEach((p, i) => {
    if (isUnconditional(p)) return
    const key = `${p.conditions?.ssid ?? ''}|${p.conditions?.screenCount ?? ''}|${p.conditions?.isPluggedIn ?? ''}`
    if (seen.has(key)) {
      errors.push({ path: `profiles[${i}].conditions`, message: 'duplicate condition combination' })
    }
    seen.add(key)
  })

  // check duplicate profile names
  const seenNames = new Set<string>()
  profiles.forEach((p, i) => {
    const name = p.profileName.trim().toLowerCase()
    if (!name) return
    if (seenNames.has(name)) {
      errors.push({ path: `profiles[${i}].profileName`, message: 'duplicate profile name' })
    }
    seenNames.add(name)
  })

  profiles.forEach((p, i) => {
    if (!p.profileName.trim()) {
      errors.push({ path: `profiles[${i}].profileName`, message: 'profile name is required' })
    }

    if (!p.mode) {
      errors.push({ path: `profiles[${i}].mode`, message: 'mode is required' })
    }

    if (p.networkType === 'embeddedStyx') {
      const server = p.embeddedStyx?.server.trim() ?? ''
      if (!server || (!server.toLowerCase().startsWith('auto://') && !/^https?:\/\//i.test(server))) {
        errors.push({ path: `profiles[${i}].embeddedStyx.server`, message: 'server must be auto://desk or an http(s) URL' })
      }
      if ((p.embeddedStyx?.password.length ?? 0) < 16) {
        errors.push({ path: `profiles[${i}].embeddedStyx.password`, message: 'shared secret must be at least 16 characters' })
      }
    }
    if (p.networkType === 'embeddedStyxServer') {
      const relay = p.embeddedStyxServer
      if (!relay || relay.port < 1024 || relay.port > 65535) {
        errors.push({ path: `profiles[${i}].embeddedStyxServer.port`, message: 'relay port must be between 1024 and 65535' })
      }
      if ((relay?.password.length ?? 0) < 16) {
        errors.push({ path: `profiles[${i}].embeddedStyxServer.password`, message: 'shared secret must be at least 16 characters' })
      }
      if ((relay?.discoveryName?.length ?? 0) > 64) {
        errors.push({ path: `profiles[${i}].embeddedStyxServer.discoveryName`, message: 'desk name must be 64 characters or fewer' })
      }
    }

    if (p.remoteOnly && p.mode !== 'Master') {
      errors.push({ path: `profiles[${i}].remoteOnly`, message: 'remoteOnly requires Master mode' })
    }

    if (p.syncScreensaver === false && p.mode !== 'Master') {
      errors.push({ path: `profiles[${i}].syncScreensaver`, message: 'syncScreensaver requires Master mode' })
    }

    const hosts = effectiveHosts(p)
    if (p.remoteOnly && hosts.filter(h => h.name.trim()).length === 0) {
      errors.push({ path: `profiles[${i}].remoteOnly`, message: 'remoteOnly requires at least one remote host' })
    }

    if (p.conditions?.screenCount !== undefined && p.conditions.screenCount < 1) {
      errors.push({ path: `profiles[${i}].conditions.screenCount`, message: 'screenCount must be at least 1' })
    }

    ;(p.screenDefinitions ?? []).forEach((s, si) => {
      if (!s.displayName && !s.outputName && !s.platformId) {
        errors.push({
          path: `profiles[${i}].screenDefinitions[${si}]`,
          message: 'at least one of displayName, outputName, or platformId is required',
        })
      }
    })

    if (p.displayRouting?.wakeDisplays && p.displayRouting?.sleepDisplays) {
      errors.push({ path: `profiles[${i}].displayRouting`, message: 'display routing cannot wake and sleep displays together' })
    }
    if (p.displayRouting?.settleDelayMs !== undefined && (p.displayRouting.settleDelayMs < 0 || p.displayRouting.settleDelayMs > 10000)) {
      errors.push({ path: `profiles[${i}].displayRouting`, message: 'settle delay must be between 0 and 10000 ms' })
    }
    ;(p.displayRouting?.inputs ?? []).forEach((input, di) => {
      if (!input.id.trim() || input.input < 0 || input.input > 255) {
        errors.push({ path: `profiles[${i}].displayRouting.inputs[${di}]`, message: 'monitor id is required and input must be between 0 and 255' })
      }
    })
  })

  return errors
}
