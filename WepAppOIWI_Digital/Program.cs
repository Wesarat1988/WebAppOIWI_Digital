using WepAppOIWI_Digital.Components;
using Microsoft.AspNetCore.Components;
using System.Net.Http;

var builder = WebApplication.CreateBuilder(args);

// DI: HttpClient สำหรับคอมโพเนนต์
builder.Services.AddScoped<HttpClient>(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(nav.BaseUri) };
});

// (ถ้าต้องมี MES)
// builder.Services.AddHttpClient("MES", c => c.BaseAddress = new Uri("https://your-mes-endpoint/"));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// app.UseHttpsRedirection(); // ปิดถ้ายังใช้ http

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// kill browser refresh ...
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.Contains("/_framework/aspnetcore-browser-refresh.js", StringComparison.OrdinalIgnoreCase)
        || path.Contains("aspnetcore-browser-refresh.js", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("// browser-refresh disabled");
        return;
    }
    await next();
});

app.Run();
