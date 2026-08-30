import { MODULE_IDS } from '@/setup/constants'
import type {
  ApplyModuleExtraDataRequest,
  IpContentMode,
  ModuleInstallChoiceGroup,
  ModuleInstallChoicesDto,
  ModuleInstallSelections,
} from '@/types/module-extra-data.types'

export function emptySelections(groups: ModuleInstallChoiceGroup[]): ModuleInstallSelections {
  const groupsMap: Record<string, string[]> = {}
  for (const group of groups) {
    if (group.kind === 'Exclusive') {
      const preferred =
        group.choices.find((choice) => choice.defaultSelected) ??
        (!group.allowNone ? group.choices[0] : undefined)
      groupsMap[group.id] = preferred ? [preferred.id] : []
    } else {
      groupsMap[group.id] = group.choices.filter((choice) => choice.defaultSelected).map((choice) => choice.id)
    }
  }
  return { groups: groupsMap }
}

export function defaultSelectionsByModule(
  modules: ModuleInstallChoicesDto[],
): Record<string, ModuleInstallSelections> {
  const initial: Record<string, ModuleInstallSelections> = {}
  for (const module of modules) {
    initial[module.moduleId] = emptySelections(module.groups)
  }
  return initial
}

type ModuleContentChoicesFormProps = {
  modules: ModuleInstallChoicesDto[]
  hasIpModule: boolean
  ipContentMode: IpContentMode
  onIpContentModeChange: (mode: IpContentMode) => void
  byModule: Record<string, ModuleInstallSelections>
  onChange: (next: Record<string, ModuleInstallSelections>) => void
}

export function buildApplyRequest(
  ipContentMode: IpContentMode,
  byModule: Record<string, ModuleInstallSelections>,
): ApplyModuleExtraDataRequest {
  return { ipContentMode, selectionsByModuleId: byModule }
}

export function ModuleContentChoicesForm({
  modules,
  hasIpModule,
  ipContentMode,
  onIpContentModeChange,
  byModule,
  onChange,
}: ModuleContentChoicesFormProps) {
  const hideIpExtras = ipContentMode === 'ServerWideProgression'
  const visibleModules = modules.filter(
    (module) =>
      !(hideIpExtras && module.moduleId === MODULE_IDS.individualProgression),
  )

  const setExclusive = (moduleId: string, groupId: string, choiceId: string | null) => {
    onChange({
      ...byModule,
      [moduleId]: {
        groups: {
          ...byModule[moduleId]?.groups,
          [groupId]: choiceId ? [choiceId] : [],
        },
      },
    })
  }

  const toggleIndependent = (moduleId: string, groupId: string, choiceId: string) => {
    const current = byModule[moduleId]?.groups[groupId] ?? []
    const next = current.includes(choiceId)
      ? current.filter((id) => id !== choiceId)
      : [...current, choiceId]
    onChange({
      ...byModule,
      [moduleId]: { groups: { ...byModule[moduleId]?.groups, [groupId]: next } },
    })
  }

  return (
    <div className="space-y-4 text-sm">
      {hasIpModule && (
        <fieldset className="space-y-2 rounded-md border border-amber-200 bg-white/70 p-3">
          <legend className="text-sm font-medium text-gray-900">Individual Progression content</legend>
          <p className="text-xs text-gray-600">
            Choose one. Standard IP uses the extra-data options below. Server Wide Progression skips those
            extras and uses the Patches tab instead.
          </p>
          <label className="flex items-start gap-2">
            <input
              type="radio"
              name="ip-content-mode"
              checked={ipContentMode === 'Standard'}
              onChange={() => onIpContentModeChange('Standard')}
            />
            <span>
              Standard Individual Progression
              <span className="block text-xs text-gray-500">
                SkillLine-family DBC always applied. Optional visuals, mana, and SQL checkboxes below.
              </span>
            </span>
          </label>
          <label className="flex items-start gap-2">
            <input
              type="radio"
              name="ip-content-mode"
              checked={ipContentMode === 'ServerWideProgression'}
              onChange={() => onIpContentModeChange('ServerWideProgression')}
            />
            <span>
              Sync with Server Wide Progression
              <span className="block text-xs text-gray-500">
                Skip IP optional extras. Prepare and sync progression patches after the first build.
              </span>
            </span>
          </label>
        </fieldset>
      )}

      {visibleModules.map((module) => (
        <div key={module.moduleId} className="space-y-3 rounded-md border border-amber-200 bg-white/70 p-3">
          <h4 className="font-medium text-gray-900">{module.moduleId}</h4>
          {module.groups.map((group) => (
            <fieldset key={group.id} className="space-y-1">
              <legend className="text-sm font-medium text-gray-800">{group.title}</legend>
              {group.description && <p className="text-xs text-gray-600">{group.description}</p>}
              {group.kind === 'Exclusive' ? (
                <div className="space-y-1">
                  {group.allowNone && (
                    <label className="flex items-center gap-2">
                      <input
                        type="radio"
                        name={`${module.moduleId}-${group.id}`}
                        checked={(byModule[module.moduleId]?.groups[group.id] ?? []).length === 0}
                        onChange={() => setExclusive(module.moduleId, group.id, null)}
                      />
                      None
                    </label>
                  )}
                  {group.choices.map((choice) => (
                    <label key={choice.id} className="flex items-start gap-2">
                      <input
                        type="radio"
                        name={`${module.moduleId}-${group.id}`}
                        checked={(byModule[module.moduleId]?.groups[group.id] ?? [])[0] === choice.id}
                        onChange={() => setExclusive(module.moduleId, group.id, choice.id)}
                      />
                      <span>
                        {choice.label}
                        {choice.description && (
                          <span className="block text-xs text-gray-500">{choice.description}</span>
                        )}
                      </span>
                    </label>
                  ))}
                </div>
              ) : (
                <div className="space-y-1">
                  {group.choices.map((choice) => (
                    <label key={choice.id} className="flex items-start gap-2">
                      <input
                        type="checkbox"
                        checked={(byModule[module.moduleId]?.groups[group.id] ?? []).includes(choice.id)}
                        onChange={() => toggleIndependent(module.moduleId, group.id, choice.id)}
                      />
                      <span>
                        {choice.label}
                        {choice.description && (
                          <span className="block text-xs text-gray-500">{choice.description}</span>
                        )}
                      </span>
                    </label>
                  ))}
                </div>
              )}
            </fieldset>
          ))}
        </div>
      ))}
    </div>
  )
}
