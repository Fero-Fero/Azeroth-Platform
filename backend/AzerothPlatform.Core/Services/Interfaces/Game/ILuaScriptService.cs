using AzerothPlatform.Core.Contracts;

namespace AzerothPlatform.Core.Services.Interfaces;

/// <summary>
/// Manages a stack's Lua scripts (served to the worldserver's Eluna engine). Scripts live under
/// <c>{stackId}/lua_scripts</c> on the host and are bind-mounted into the worldserver.
/// </summary>
public interface ILuaScriptService
{
    Task<LuaScriptListDto> ListAsync(string stackId, CancellationToken cancellationToken = default);

    Task<LuaScriptContentDto> ReadAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Creates or overwrites a single Lua script file.</summary>
    Task<LuaScriptListDto> SaveAsync(string stackId, string relativePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a file: a <c>.zip</c> is extracted (preserving folder structure); any other file is
    /// stored at <paramref name="relativePath"/> (or the uploaded file name when omitted).
    /// </summary>
    Task<LuaScriptListDto> UploadAsync(string stackId, string fileName, string? relativePath, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file or directory (recursively) within the lua_scripts tree.</summary>
    Task<LuaScriptListDto> DeleteAsync(string stackId, string relativePath, CancellationToken cancellationToken = default);
}
