using AllCompassionateCare.src;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for file uploads
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    // Set max request body size to 50MB
    serverOptions.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure SignalR for large file transfers
builder.Services.Configure<HubOptions>(options =>
{
    options.MaximumReceiveMessageSize = 50 * 1024 * 1024; // 50MB
});

builder.Services.AddHttpClient();

var app = builder.Build();

// Add global exception handler
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GLOBAL ERROR HANDLER] Exception: {ex.GetType().Name}");
        Console.WriteLine($"[GLOBAL ERROR HANDLER] Message: {ex.Message}");
        Console.WriteLine($"[GLOBAL ERROR HANDLER] Stack Trace: {ex.StackTrace}");
        throw;
    }
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
