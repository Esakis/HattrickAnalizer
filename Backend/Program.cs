using System.Text.Json;
using HattrickAnalizer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection();
builder.Services.AddHttpClient<HattrickApiService>();
builder.Services.AddHttpClient<OAuthService>();
builder.Services.AddScoped<AdvancedLineupOptimizer>();
builder.Services.AddScoped<CalibrationService>();
builder.Services.AddSingleton<CalibrationSnapshotStore>();
builder.Services.AddScoped<OpponentScoutService>();
builder.Services.AddScoped<LeagueSimulationService>();
builder.Services.AddScoped<TrainingService>();
builder.Services.AddScoped<MatchOrdersService>();
builder.Services.AddScoped<RatingEngine>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<PlayerHistoryService>();

var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:4200" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.Logger.LogInformation("Allowed CORS origins: {Origins}", string.Join(", ", allowedOrigins));
if (app.Configuration.GetValue<bool>("UseMockData"))
{
    app.Logger.LogWarning("UseMockData=true: the application is serving deterministic example data, not CHPP data.");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAngular");
app.UseApiExceptionHandling(app.Logger);
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { }

public static class ApiExceptionHandlingExtensions
{
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app, ILogger logger)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (UnauthorizedAccessException ex)
            {
                await WriteError(context, StatusCodes.Status401Unauthorized, "unauthorized", ex.Message);
            }
            catch (ArgumentException ex)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, "validation_error", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                await WriteError(context, StatusCodes.Status422UnprocessableEntity, "unprocessable_request", ex.Message);
            }
            catch (ChppApiException ex)
            {
                logger.LogError(ex, "CHPP API request failed");
                await WriteError(context, StatusCodes.Status502BadGateway, "upstream_error", ex.Message);
            }
        });
        return app;
    }

    private static Task WriteError(HttpContext context, int status, string code, string error)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error, code, status }));
    }
}
