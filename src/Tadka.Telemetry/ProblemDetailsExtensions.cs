using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tadka.Telemetry;

/// <summary>
/// Shared RFC 7807 helpers so every service returns the same error envelope (ADR-006).
/// Day 3 introduces problem+json on Ordering; Day 8+ satellite services use the same shape
/// so students never see Auth-style <c>{ "error": "..." }</c> on one service and ProblemDetails on another.
/// </summary>
public static class ProblemDetailsExtensions
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Map unhandled exceptions to problem+json (minimal satellite services).</summary>
    public static IApplicationBuilder UseTadkaProblemDetails(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                var log = ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Tadka.ProblemDetails");
                log?.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);

                if (ctx.Response.HasStarted) throw;

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred",
                    Detail = "An unexpected error occurred. Please try again later.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                    Instance = ctx.Request.Path
                };
                ctx.Response.StatusCode = problem.Status.Value;
                ctx.Response.ContentType = "application/problem+json";
                await ctx.Response.WriteAsJsonAsync(problem, Json);
            }
        });

    public static IResult NotFoundProblem(string resource, object? id = null) =>
        Results.Json(new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Resource Not Found",
            Detail = id is null ? $"{resource} was not found." : $"{resource} '{id}' was not found.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
        }, Json, statusCode: StatusCodes.Status404NotFound);

    public static IResult UnauthorizedProblem(string detail = "Authentication is required.") =>
        Results.Json(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = detail,
            Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
        }, Json, statusCode: StatusCodes.Status401Unauthorized);

    public static IResult ServiceUnavailableProblem(string detail) =>
        Results.Json(new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Service Unavailable",
            Detail = detail,
            Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4"
        }, Json, statusCode: StatusCodes.Status503ServiceUnavailable);
}
