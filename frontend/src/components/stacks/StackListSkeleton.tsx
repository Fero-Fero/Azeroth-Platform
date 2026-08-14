export default function StackListSkeleton({ count = 3 }: { count?: number }) {
  return (
    <div className="space-y-4" aria-busy="true" aria-label="Loading stacks">
      {Array.from({ length: count }, (_, index) => (
        <div
          key={index}
          className="animate-pulse rounded-lg border border-gray-200 bg-white p-6 shadow-sm"
        >
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1 space-y-3">
              <div className="flex items-center gap-3">
                <div className="h-7 w-48 rounded bg-gray-200" />
                <div className="h-6 w-20 rounded-full bg-gray-200" />
                <div className="h-6 w-16 rounded-full bg-gray-100" />
              </div>
              <div className="h-4 w-40 rounded bg-gray-100" />
              <div className="flex gap-4">
                <div className="h-4 w-16 rounded bg-gray-100" />
                <div className="h-4 w-16 rounded bg-gray-100" />
                <div className="h-4 w-16 rounded bg-gray-100" />
              </div>
            </div>
            <div className="flex gap-2">
              <div className="h-9 w-20 rounded-md bg-gray-200" />
              <div className="h-9 w-20 rounded-md bg-gray-100" />
            </div>
          </div>
        </div>
      ))}
    </div>
  )
}
