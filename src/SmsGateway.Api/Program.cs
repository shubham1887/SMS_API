using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using Serilog;
using SmsGateway.Api.Data;
using SmsGateway.Api.Security;
using SmsGateway.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- logging ----------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext());

// ---------- database ----------
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default"),
        sql => sql.EnableRetryOnFailure(3)));

// ---------- options & services ----------
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
builder.Services.AddSingleton<TimeHelper>();
builder.Services.AddSingleton<PhoneNumberNormalizer>();
builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddScoped<SmsQueueService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<WebhookWorker>();
builder.Services.AddHttpClient("webhook");

// ---------- auth: three API-key schemes ----------
builder.Services
    .AddAuthentication()
    .AddScheme<KeyAuthOptions, KeyAuthHandler>(KeyAuthHandler.ClientScheme, null)
    .AddScheme<KeyAuthOptions, KeyAuthHandler>(KeyAuthHandler.DeviceScheme, null)
    .AddScheme<KeyAuthOptions, KeyAuthHandler>(KeyAuthHandler.AdminScheme, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Client", p => p.AddAuthenticationSchemes(KeyAuthHandler.ClientScheme).RequireRole("Client"))
    .AddPolicy("Device", p => p.AddAuthenticationSchemes(KeyAuthHandler.DeviceScheme).RequireRole("Device"))
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(KeyAuthHandler.AdminScheme).RequireRole("Admin"));

// ---------- MVC ----------
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SMS Gateway API",
        Version = "v1",
        Description = "Send SMS through your own Android phones. Clients use X-Api-Key, phones use X-Device-Key, management uses X-Admin-Key."
    });
    foreach (var (name, header) in new[]
             {
                 ("ApiKey", KeyAuthHandler.ClientHeader),
                 ("DeviceKey", KeyAuthHandler.DeviceHeader),
                 ("AdminKey", KeyAuthHandler.AdminHeader)
             })
    {
        c.AddSecurityDefinition(name, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = header,
            Description = $"Header {header}"
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = name } }] = []
        });
    }
    var xml = Path.Combine(AppContext.BaseDirectory, "SmsGateway.Api.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml);
});

var app = builder.Build();

// ---------- pipeline ----------
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "Internal server error", detail = app.Environment.IsDevelopment() ? ex?.Message : null });
}));
app.UseSerilogRequestLogging();
app.UseCors();

// Static downloads (Android APK etc.) from <content root>/app  ->  https://host/app/<file>
var downloadsDir = Path.Combine(app.Environment.ContentRootPath, "app");
Directory.CreateDirectory(downloadsDir);
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(downloadsDir),
    RequestPath = "/app",
    ContentTypeProvider = contentTypes,
    ServeUnknownFileTypes = true
});
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "SMS Gateway API v1");
    c.RoutePrefix = "swagger";
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await DbInitializer.InitializeAsync(app.Services);

app.Run();
