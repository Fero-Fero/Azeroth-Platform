import { useState } from 'react'
import {
  Loader2,
  Plus,
  Pencil,
  Trash2,
  X,
  Lock,
  ExternalLink,
  GitBranch,
  Package,
  FileText,
} from 'lucide-react'
import {
  useModuleCatalog,
  useCreateModule,
  useUpdateModule,
  useDeleteModule,
  useUploadModulePackage,
  useReplaceModulePackage,
  useModuleReadme,
} from '@/hooks/useModules'
import MarkdownView from '@/components/MarkdownView'
import type { ModuleDto, SaveModuleRequest } from '@/types/stack.types'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

type SourceType = 'git' | 'package'

const EMPTY_FORM: SaveModuleRequest = {
  id: '',
  name: '',
  description: '',
  repository: '',
  branch: 'master',
}

export default function ModuleCatalogPage() {
  const { data: modules, isLoading, error } = useModuleCatalog()
  const createModule = useCreateModule()
  const updateModule = useUpdateModule()
  const deleteModule = useDeleteModule()
  const uploadPackage = useUploadModulePackage()
  const replacePackage = useReplaceModulePackage()

  const [editing, setEditing] = useState<ModuleDto | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [sourceType, setSourceType] = useState<SourceType>('git')
  const [form, setForm] = useState<SaveModuleRequest>(EMPTY_FORM)
  const [packageFile, setPackageFile] = useState<File | null>(null)
  const [formError, setFormError] = useState<string | null>(null)

  const [readmeModule, setReadmeModule] = useState<ModuleDto | null>(null)

  const isEditing = editing !== null
  const busy = createModule.isPending || updateModule.isPending || uploadPackage.isPending || replacePackage.isPending

  const openCreate = () => {
    setEditing(null)
    setSourceType('git')
    setForm(EMPTY_FORM)
    setPackageFile(null)
    setFormError(null)
    setShowForm(true)
  }

  const openEdit = (module: ModuleDto) => {
    setEditing(module)
    setSourceType(module.sourceType === 'package' ? 'package' : 'git')
    setForm({
      id: module.id,
      name: module.name,
      description: module.description,
      repository: module.repository,
      branch: module.branch || 'master',
    })
    setPackageFile(null)
    setFormError(null)
    setShowForm(true)
  }

  const closeForm = () => {
    setShowForm(false)
    setEditing(null)
    setForm(EMPTY_FORM)
    setPackageFile(null)
    setFormError(null)
  }

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setFormError(null)
    try {
      if (isEditing) {
        await updateModule.mutateAsync({ moduleId: editing!.id, request: form })
        if (sourceType === 'package' && packageFile) {
          await replacePackage.mutateAsync({ moduleId: editing!.id, file: packageFile })
        }
      } else if (sourceType === 'package') {
        if (!packageFile) {
          setFormError('Please choose a .zip package to upload.')
          return
        }
        await uploadPackage.mutateAsync({
          fields: {
            id: form.id,
            name: form.name,
            description: form.description,
          },
          file: packageFile,
        })
      } else {
        await createModule.mutateAsync(form)
      }
      closeForm()
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  const handleDelete = async (module: ModuleDto) => {
    if (!window.confirm(`Delete module "${module.name}" (${module.id}) from the catalog?`)) return
    try {
      await deleteModule.mutateAsync(module.id)
    } catch (err) {
      window.alert(errorMessage(err))
    }
  }

  const builtIns = (modules?.filter((m) => m.isBuiltIn) ?? []).sort(recommendedFirst)
  const custom = (modules?.filter((m) => !m.isBuiltIn) ?? []).sort(recommendedFirst)

  return (
    <div className="max-w-4xl mx-auto">
      <div className="mb-6 flex items-start justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold text-gray-900">Module Catalog</h1>
          <p className="text-sm text-gray-500 mt-1">
            Modules available when creating a stack. Add your own from a git repository or by
            uploading a package. Click a module to read its README.
          </p>
        </div>
        <button
          onClick={openCreate}
          className="inline-flex shrink-0 items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
        >
          <Plus className="h-4 w-4" />
          Add module
        </button>
      </div>

      {isLoading && (
        <div className="flex items-center justify-center py-16">
          <Loader2 className="h-8 w-8 animate-spin text-blue-500" />
        </div>
      )}

      {error && (
        <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          Failed to load the module catalog.
        </div>
      )}

      {modules && (
        <div className="space-y-8">
          <ModuleSection title="Custom modules" emptyText="No custom modules yet. Click “Add module” to create one.">
            {custom.map((module) => (
              <ModuleCard
                key={module.id}
                module={module}
                onOpenReadme={() => setReadmeModule(module)}
                onEdit={() => openEdit(module)}
                onDelete={() => handleDelete(module)}
                deleting={deleteModule.isPending && deleteModule.variables === module.id}
              />
            ))}
          </ModuleSection>

          <ModuleSection title="Built-in modules">
            {builtIns.map((module) => (
              <ModuleCard key={module.id} module={module} onOpenReadme={() => setReadmeModule(module)} />
            ))}
          </ModuleSection>
        </div>
      )}

      {showForm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
          <div className="w-full max-w-lg rounded-lg bg-white shadow-xl">
            <div className="flex items-center justify-between border-b border-gray-200 px-6 py-4">
              <h2 className="text-lg font-semibold text-gray-900">
                {isEditing ? 'Edit module' : 'Add module'}
              </h2>
              <button onClick={closeForm} className="text-gray-400 hover:text-gray-600" aria-label="Close">
                <X className="h-5 w-5" />
              </button>
            </div>

            <form onSubmit={handleSubmit} className="space-y-4 px-6 py-4">
              {!isEditing && (
                <div className="flex gap-2">
                  <SourceTypeButton
                    active={sourceType === 'git'}
                    onClick={() => setSourceType('git')}
                    icon={<GitBranch className="h-4 w-4" />}
                    label="Git repository"
                  />
                  <SourceTypeButton
                    active={sourceType === 'package'}
                    onClick={() => setSourceType('package')}
                    icon={<Package className="h-4 w-4" />}
                    label="Upload package"
                  />
                </div>
              )}

              <Field label="Module id" hint="e.g. mod-my-feature. Used as the module folder name.">
                <input
                  type="text"
                  value={form.id}
                  onChange={(e) => setForm({ ...form, id: e.target.value })}
                  disabled={isEditing}
                  required
                  placeholder="mod-my-feature"
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:cursor-not-allowed disabled:bg-gray-50 disabled:text-gray-500"
                />
              </Field>

              <Field label="Name">
                <input
                  type="text"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  required
                  placeholder="My Feature"
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </Field>

              <Field label="Description">
                <textarea
                  value={form.description}
                  onChange={(e) => setForm({ ...form, description: e.target.value })}
                  rows={2}
                  placeholder="What this module does"
                  className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </Field>

              {sourceType === 'git' ? (
                <>
                  <Field label="Repository URL">
                    <input
                      type="url"
                      value={form.repository}
                      onChange={(e) => setForm({ ...form, repository: e.target.value })}
                      required
                      placeholder="https://github.com/owner/mod-my-feature"
                      className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </Field>
                  <Field label="Branch">
                    <input
                      type="text"
                      value={form.branch}
                      onChange={(e) => setForm({ ...form, branch: e.target.value })}
                      placeholder="master"
                      className="block w-full rounded-md border border-gray-300 bg-white px-3 py-2 font-mono text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                  </Field>
                </>
              ) : (
                <Field
                  label={isEditing ? 'Replace package (.zip)' : 'Package (.zip)'}
                  hint={
                    isEditing
                      ? 'Optional - choose a new .zip to replace the stored files.'
                      : 'Upload the module source as a .zip. A single wrapping folder (e.g. from “Download ZIP”) is unwrapped automatically.'
                  }
                >
                  <input
                    type="file"
                    accept=".zip,application/zip"
                    onChange={(e) => setPackageFile(e.target.files?.[0] ?? null)}
                    className="block w-full text-sm text-gray-700 file:mr-3 file:rounded-md file:border-0 file:bg-blue-50 file:px-3 file:py-2 file:text-sm file:font-medium file:text-blue-700 hover:file:bg-blue-100"
                  />
                  {packageFile && (
                    <p className="mt-1 text-xs text-gray-500">{packageFile.name}</p>
                  )}
                </Field>
              )}

              {formError && (
                <div className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                  {formError}
                </div>
              )}

              <div className="flex justify-end gap-3 pt-2">
                <button
                  type="button"
                  onClick={closeForm}
                  className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
                >
                  Cancel
                </button>
                <button
                  type="submit"
                  disabled={busy}
                  className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                >
                  {busy && <Loader2 className="h-4 w-4 animate-spin" />}
                  {isEditing ? 'Save changes' : 'Add module'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

      {readmeModule && (
        <ReadmeModal module={readmeModule} onClose={() => setReadmeModule(null)} />
      )}
    </div>
  )
}

function SourceTypeButton({
  active,
  onClick,
  icon,
  label,
}: {
  active: boolean
  onClick: () => void
  icon: React.ReactNode
  label: string
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex flex-1 items-center justify-center gap-2 rounded-md border px-3 py-2 text-sm font-medium transition-colors ${
        active
          ? 'border-blue-600 bg-blue-50 text-blue-700'
          : 'border-gray-300 bg-white text-gray-600 hover:bg-gray-50'
      }`}
    >
      {icon}
      {label}
    </button>
  )
}

function ModuleSection({
  title,
  emptyText,
  children,
}: {
  title: string
  emptyText?: string
  children: React.ReactNode[]
}) {
  return (
    <section>
      <h2 className="mb-3 text-sm font-semibold uppercase tracking-wide text-gray-500">{title}</h2>
      {children.length === 0 ? (
        <div className="rounded-md border border-dashed border-gray-300 py-8 text-center text-sm text-gray-500">
          {emptyText}
        </div>
      ) : (
        <div className="space-y-3">{children}</div>
      )}
    </section>
  )
}

function ModuleCard({
  module,
  onOpenReadme,
  onEdit,
  onDelete,
  deleting,
}: {
  module: ModuleDto
  onOpenReadme: () => void
  onEdit?: () => void
  onDelete?: () => void
  deleting?: boolean
}) {
  const isPackage = module.sourceType === 'package'
  return (
    <div
      onClick={onOpenReadme}
      className="flex cursor-pointer items-start justify-between gap-4 rounded-lg border border-gray-200 bg-white p-4 hover:border-blue-300 hover:shadow-sm"
    >
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="font-medium text-gray-900">{module.name}</h3>
          {module.recommended && <RecommendedBadge />}
          <code className="rounded bg-gray-100 px-1.5 py-0.5 font-mono text-xs text-gray-600">
            {module.id}
          </code>
          {module.isBuiltIn && (
            <span className="inline-flex items-center gap-1 rounded bg-gray-100 px-1.5 py-0.5 text-xs font-medium text-gray-500">
              <Lock className="h-3 w-3" /> Built-in
            </span>
          )}
          <span className="inline-flex items-center gap-1 rounded bg-gray-100 px-1.5 py-0.5 text-xs font-medium text-gray-500">
            {isPackage ? <Package className="h-3 w-3" /> : <GitBranch className="h-3 w-3" />}
            {isPackage ? 'Package' : 'Git'}
          </span>
        </div>
        {module.description && (
          <p className="mt-1 text-sm text-gray-600">{module.description}</p>
        )}
        {isPackage ? (
          <span className="mt-1 inline-flex items-center gap-1 text-xs text-gray-400">
            <FileText className="h-3 w-3" /> Uploaded package
          </span>
        ) : (
          <a
            href={module.repository}
            target="_blank"
            rel="noreferrer"
            onClick={(e) => e.stopPropagation()}
            className="mt-1 inline-flex items-center gap-1 text-xs text-blue-600 hover:underline"
          >
            {module.repository}
            <ExternalLink className="h-3 w-3" />
            <span className="text-gray-400">({module.branch})</span>
          </a>
        )}
      </div>

      {!module.isBuiltIn && (
        <div className="flex shrink-0 gap-1" onClick={(e) => e.stopPropagation()}>
          <button
            onClick={onEdit}
            className="rounded p-2 text-gray-400 hover:bg-gray-100 hover:text-gray-700"
            aria-label={`Edit ${module.name}`}
          >
            <Pencil className="h-4 w-4" />
          </button>
          <button
            onClick={onDelete}
            disabled={deleting}
            className="rounded p-2 text-gray-400 hover:bg-red-50 hover:text-red-600 disabled:opacity-50"
            aria-label={`Delete ${module.name}`}
          >
            {deleting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />}
          </button>
        </div>
      )}
    </div>
  )
}

function recommendedFirst<T extends { recommended?: boolean; name: string }>(a: T, b: T) {
  if (!!a.recommended !== !!b.recommended) return a.recommended ? -1 : 1
  return a.name.localeCompare(b.name)
}

function RecommendedBadge() {
  return (
    <span className="rounded-full bg-sky-50 px-2 py-0.5 text-[11px] font-medium text-sky-700 ring-1 ring-inset ring-sky-200">
      Recommended
    </span>
  )
}

function ReadmeModal({ module, onClose }: { module: ModuleDto; onClose: () => void }) {
  const { data, isLoading, error } = useModuleReadme(module.id)

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className="flex max-h-[90vh] w-full max-w-3xl flex-col rounded-lg bg-white shadow-xl">
        <div className="flex shrink-0 items-center justify-between border-b border-gray-200 px-6 py-4">
          <div className="flex items-center gap-2">
            <FileText className="h-5 w-5 text-gray-500" />
            <h2 className="text-lg font-semibold text-gray-900">{module.name}</h2>
            <code className="rounded bg-gray-100 px-1.5 py-0.5 font-mono text-xs text-gray-600">
              {module.id}
            </code>
          </div>
          <button onClick={onClose} className="text-gray-400 hover:text-gray-600" aria-label="Close">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-6 py-5">
          {isLoading && (
            <div className="flex items-center justify-center py-12">
              <Loader2 className="h-8 w-8 animate-spin text-blue-500" />
            </div>
          )}
          {error && (
            <div className="rounded-md border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              Failed to load the README.
            </div>
          )}
          {data && !data.found && (
            <div className="rounded-md border border-gray-200 bg-gray-50 px-4 py-8 text-center text-sm text-gray-500">
              No README available for this module.
            </div>
          )}
          {data && data.found && <MarkdownView content={data.content} baseUrl={data.baseUrl} />}
        </div>
      </div>
    </div>
  )
}

function Field({
  label,
  hint,
  children,
}: {
  label: string
  hint?: string
  children: React.ReactNode
}) {
  return (
    <div>
      <label className="mb-1 block text-sm font-medium text-gray-700">{label}</label>
      {children}
      {hint && <p className="mt-1 text-xs text-gray-500">{hint}</p>}
    </div>
  )
}
