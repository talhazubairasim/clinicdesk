using ClinicDesk.Domain;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClinicDesk.Api.Infrastructure;

/// <summary>Maps business-rule, validation and concurrency failures to ProblemDetails.</summary>
public class ErrorHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException v => new ValidationProblemDetails(
                v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            { Status = 400, Title = "Validation failed" },
            DomainException d => new ProblemDetails { Status = 409, Title = "Request conflicts with the clinic's rules", Detail = d.Message },
            DbUpdateConcurrencyException => new ProblemDetails { Status = 409, Title = "Conflict", Detail = "The record was changed by someone else. Reload and try again." },
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } } =>
                new ProblemDetails { Status = 409, Title = "Conflict", Detail = "That time overlaps another appointment for this practitioner." },
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                new ProblemDetails { Status = 409, Title = "Conflict", Detail = "A record with the same unique value already exists." },
            _ => null,
        };
        if (problem is null) return false;
        http.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem, Exception = exception });
    }
}

/// <summary>Endpoint filter that runs the FluentValidation validator for an argument of type T.</summary>
public class Validate<T>(IValidator<T> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var arg = ctx.Arguments.OfType<T>().FirstOrDefault();
        if (arg is not null) await validator.ValidateAndThrowAsync(arg);
        return await next(ctx);
    }
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validated<T>(this RouteHandlerBuilder b) => b.AddEndpointFilter<Validate<T>>();
}
