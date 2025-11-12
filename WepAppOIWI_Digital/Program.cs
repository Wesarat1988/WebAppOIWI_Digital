using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WepAppOIWI_Digital.Components;
using WepAppOIWI_Digital.Data;
using WepAppOIWI_Digital.Services;

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
{
    // Disable hosting startup injection so dotnet-watch's browser refresh script isn't wired up
    var hostingStartupEnvVar = $"ASPNETCORE_{WebHostDefaults.HostingStartupAssembliesKey.ToUpperInvariant()}";
    Environment.SetEnvironmentVariable(hostingStartupEnvVar, string.Empty);
}

var builder = WebApplication.CreateBuilder(args);

var catalogConnectionString = ResolveCatalogConnectionString(builder);

builder.Services.AddScoped<WepAppOIWI_Digital.Services.SetupStateStore>();
builder.Services.Configure<DocumentCatalogOptions>(builder.Configuration.GetSection("DocumentCatalog"));
builder.Services.Configure<OiwiOptions>(builder.Configuration.GetSection("Oiwi"));
builder.Services.Configure<OiwiIndexerOptions>(builder.Configuration.GetSection("OiwiIndexer"));
builder.Services.AddMemoryCache();
builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(catalogConnectionString));
builder.Services.AddSingleton<DocumentCatalogService>();
builder.Services.AddSingleton<DocumentUploadService>();
builder.Services.AddSingleton<IVersionStore, FilesystemVersionStore>();
builder.Services.AddHostedService<OiwiIndexer>();

var indexerEnabled = builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true);
if (indexerEnabled)
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

var indexerEnabled = builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true);
if (indexerEnabled)
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

var indexerEnabled = builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true);
if (indexerEnabled)
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

var indexerEnabled = builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true);
if (indexerEnabled)
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (IsOiwiIndexerEnabled(builder.Configuration))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (IsOiwiIndexerEnabled(builder))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

if (builder.Configuration.GetValue<bool>("OiwiIndexer:Enabled", true))
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

var indexerEnabled = IsOiwiIndexerEnabled(builder);
if (indexerEnabled)
{
    builder.Services.AddHostedService<OiwiIndexer>();
}

// DI: HttpClient ÊÓËÃÑº¤ÍÁâ¾à¹¹µì
builder.Services.AddScoped<HttpClient>(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// (¶éÒµéÍ§ÁÕ MES)
// builder.Services.AddHttpClient("MES", c => c.BaseAddress = new Uri("https://your-mes-endpoint/"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var migratorLogger = loggerFactory.CreateLogger("CatalogDbMigrator");
    await CatalogDbMigrator.EnsureSchemaAsync(factory, migratorLogger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// app.UseHttpsRedirection(); // »Ô´¶éÒÂÑ§ãªé http

// Disable the browser refresh script to avoid certificate prompts when running without HTTPS
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    if (path.Contains("/_framework/aspnetcore-browser-refresh.js", StringComparison.OrdinalIgnoreCase)
        || path.Contains("aspnetcore-browser-refresh.js", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("// browser-refresh disabled");
        return;
    }

    await next();
});

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/documents/preview/{token}", (HttpContext context, string token, DocumentCatalogService catalog, CancellationToken cancellationToken)
    => ServeDocumentAsync(context, token, catalog, cancellationToken, inline: true));

app.MapGet("/documents/download/{token}", (string token, DocumentCatalogService catalog, CancellationToken cancellationToken)
    => ServeDocumentAsync(null, token, catalog, cancellationToken, inline: false));

app.MapGet("/documents/file/{token}", (HttpContext context, string token, DocumentCatalogService catalog, CancellationToken cancellationToken)
    => ServeDocumentAsync(context, token, catalog, cancellationToken, inline: true));

app.MapGet("/documents/{token}/versions", async (string token, int? take, DocumentUploadService uploader, CancellationToken cancellationToken) =>
{
    if (!DocumentCatalogService.TryDecodeDocumentToken(token, out var normalizedPath))
    {
        return Results.BadRequest();
    }

    var history = await uploader.GetHistoryAsync(normalizedPath, take.GetValueOrDefault(5), cancellationToken).ConfigureAwait(false);
    return Results.Ok(history);
});

app.MapPost("/documents/{token}/versions/{versionId}/set-active", async (HttpContext httpContext, string token, string versionId, DocumentUploadService uploader, CancellationToken cancellationToken) =>
{
    if (!DocumentCatalogService.TryDecodeDocumentToken(token, out var normalizedPath))
    {
        return Results.BadRequest();
    }

    var actor = httpContext.User?.Identity?.Name;
    var result = await uploader.SetActiveVersionAsync(normalizedPath, versionId, actor, comment: null, cancellationToken).ConfigureAwait(false);

    if (!result.Succeeded)
    {
        return Results.BadRequest(new { message = result.ErrorMessage ?? "ไม่สามารถตั้งเวอร์ชันนี้ให้ใช้งานได้" });
    }

    return Results.Ok(new
    {
        versionId = result.ActiveVersionId,
        updatedAtUtc = result.UpdatedAtUtc?.UtcTicks,
        result.DocumentCode,
        result.NormalizedPath
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task<IResult> ServeDocumentAsync(HttpContext? context, string token, DocumentCatalogService catalog, CancellationToken cancellationToken, bool inline)
{
    if (!DocumentCatalogService.TryDecodeDocumentToken(token, out var normalizedPath))
    {
        return Results.BadRequest();
    }

    var handle = await catalog.TryGetDocumentFileAsync(normalizedPath, cancellationToken);
    if (handle is null)
    {
        return Results.NotFound();
    }

    if (inline)
    {
        if (context is not null && !string.IsNullOrEmpty(handle.FileName))
        {
            var encodedFileName = Uri.EscapeDataString(handle.FileName);
            context.Response.Headers[HeaderNames.ContentDisposition] = $"inline; filename*=UTF-8''{encodedFileName}";
        }

        return Results.File(handle.PhysicalPath, handle.ContentType, enableRangeProcessing: true);
    }

    return Results.File(handle.PhysicalPath, handle.ContentType, handle.FileName, enableRangeProcessing: true);
}

static string ResolveCatalogConnectionString(WebApplicationBuilder builder)
{
    var raw = builder.Configuration.GetConnectionString("CatalogDb");
    var contentRoot = builder.Environment.ContentRootPath;

    if (string.IsNullOrWhiteSpace(raw))
    {
        var defaultPath = Path.Combine(contentRoot, "App_Data", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
        return $"Data Source={defaultPath}";
    }

    if (!raw.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var targetPath = Path.IsPathRooted(raw) ? raw : Path.Combine(contentRoot, raw);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        return $"Data Source={targetPath}";
    }

    var builderConn = new SqliteConnectionStringBuilder(raw);
    if (!Path.IsPathRooted(builderConn.DataSource))
    {
        builderConn.DataSource = Path.Combine(contentRoot, builderConn.DataSource);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(builderConn.DataSource)!);
    return builderConn.ToString();
}

static bool IsOiwiIndexerEnabled(WebApplicationBuilder builder)
    => builder.Configuration.GetValue("OiwiIndexer:Enabled", true);

