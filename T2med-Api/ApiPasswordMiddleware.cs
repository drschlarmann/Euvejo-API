using Microsoft.AspNetCore.Http;

namespace T2med_Api;

public sealed class ApiPasswordMiddleware(
    RequestDelegate next,
    ApiPasswordSecurity.PasswordVerifier verifier)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var values = context.Request.Headers[ApiPasswordSecurity.HeaderName];
        if (values.Count == 0)
        {
            values = context.Request.Headers[ApiPasswordSecurity.LegacyHeaderName];
        }
        if (values.Count != 1 || !verifier.Verify(values[0] ?? ""))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Euvejo-API-Kennwort fehlt oder ist ungueltig."
            });
            return;
        }

        await next(context);
    }
}
