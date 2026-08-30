using AzerothPlatform.Core.Services.Interfaces;
using Microsoft.AspNetCore.Http;

namespace AzerothPlatform.Api.Services;

public sealed class HttpCloudAuditActorProvider : ICloudAuditActorProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCloudAuditActorProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string GetActor()
    {
        var name = _httpContextAccessor.HttpContext?.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "admin" : name.Trim();
    }
}
