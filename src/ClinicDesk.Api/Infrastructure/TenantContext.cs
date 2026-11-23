namespace ClinicDesk.Api.Infrastructure;

/// <summary>
/// The tenant and user of the current request, read from the JWT. Background jobs
/// leave it empty and query across tenants explicitly with IgnoreQueryFilters.
/// </summary>
public class TenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? UserId { get; private set; }

    public void Set(Guid tenantId, Guid? userId = null)
    {
        if (TenantId is { } current && current != tenantId)
            throw new InvalidOperationException("The tenant of a request can't change once set.");
        TenantId = tenantId;
        UserId = userId;
    }
}

public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantContext tenant)
    {
        if (http.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(http.User.FindFirst(Claims.Tenant)?.Value, out var tenantId))
        {
            Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId);
            tenant.Set(tenantId, userId == Guid.Empty ? null : userId);
        }
        await next(http);
    }
}
