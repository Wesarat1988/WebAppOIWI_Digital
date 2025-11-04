using WepAppOIWI_Digital.Components;
using WepAppOIWI_Digital.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
{
    // Disable hosting startup injection so dotnet-watch's browser refresh script isn't wired up
    var hostingStartupEnvVar = $"ASPNETCORE_{WebHostDefaults.HostingStartupAssembliesKey.ToUpperInvariant()}";
    Environment.SetEnvironmentVariable(hostingStartupEnvVar, string.Empty);
}

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Disable hosting startup injection so dotnet-watch's browser refresh script isn't wired up
    builder.WebHost.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Empty);
}

builder.Services.AddScoped<WepAppOIWI_Digital.Services.SetupStateStore>();
builder.Services.Configure<DocumentCatalogOptions>(builder.Configuration.GetSection("DocumentCatalog"));
builder.Services.AddSingleton<DocumentCatalogService>();
builder.Services.AddSingleton<DocumentUploadService>();

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
