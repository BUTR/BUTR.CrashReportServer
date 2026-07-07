using AspNetCore.Authentication.Basic;

using BUTR.CrashReport.Server.Contexts;
using BUTR.CrashReport.Server.Migrations;
using BUTR.CrashReport.Server.Options;
using BUTR.CrashReport.Server.Services;
using BUTR.CrashReport.Server.v13;
using BUTR.CrashReport.Server.v14;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

using Npgsql;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

namespace BUTR.CrashReport.Server;

public class Startup
{
    public const string ReportsCachePolicyName = "Reports";
    public const string UploadRateLimitPolicyName = "upload";

    private readonly string _appName;
    private readonly IConfiguration _configuration;

    public Startup(IConfiguration configuration)
    {
        _appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "ERROR";
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public void ConfigureServices(IServiceCollection services)
    {
        var assemblyName = typeof(Startup).Assembly.GetName();
        var userAgent = $"{assemblyName.Name ?? "ERROR"} v{assemblyName.Version?.ToString() ?? "ERROR"} (github.com/BUTR)";

        services.Configure<AuthOptions>(_configuration.GetSection("Auth"));
        services.Configure<StorageOptions>(_configuration.GetSection("Storage"));
        services.Configure<CrashUploadOptions>(_configuration.GetSection("CrashUpload"));
        services.Configure<UptimeKumaOptions>(_configuration.GetSection("UptimeKuma"));
        services.Configure<CompressionOptions>(_configuration.GetSection("Compression"));

        services.AddHttpClient<IHealthCheckPublisher, UptimeKumaHealthCheckPublisher>().ConfigureHttpClient((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<UptimeKumaOptions>>().Value;

            if (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri))
                client.BaseAddress = uri;
            client.DefaultRequestHeaders.Add("User-Agent", userAgent);
        });

        services.AddTransient<RandomNumberGenerator>(_ => RandomNumberGenerator.Create());
        services.AddScoped<FileIdGenerator>();
        services.AddScoped<CrashReportStore>();
        services.AddScoped<CrashReportService>();
        services.AddScoped<LegacyHtmlToJsonMigrator>();
        services.AddScoped<HtmlHandlerV13>();
        services.AddScoped<JsonHandlerV13>();
        services.AddScoped<JsonHandlerV14>();
        services.AddSingleton<Base32Generator>();
        services.AddSingleton<RecyclableMemoryStreamManager>();
        services.AddSingleton<GZipCompressor>();
        services.AddSingleton<ZstdCompressionService>();
        services.AddScoped<DictionaryService>();
        //services.AddHostedService<DatabaseMigrator>();

        services.AddPooledDbContextFactory<AppDbContext>(x => x
            .UseNpgsql(_configuration.GetConnectionString("Main"), y => y.MigrationsAssembly("BUTR.CrashReport.Server"))
            .ReplaceService<IMigrationsSqlGenerator, CrashReportMigrationsSqlGenerator>()
            .ReplaceService<IRelationalAnnotationProvider, CrashReportAnnotationProvider>());

        services.AddSingleton(_ => NpgsqlDataSource.Create(_configuration.GetConnectionString("Main")!));
        services.AddSingleton<ReportBlobSchema>();

        services.AddSwaggerGen(opt =>
        {
            opt.SwaggerDoc("v1", new OpenApiInfo
            {
                Version = "v1",
                Title = "BUTR's Crash Report Server",
                Description = "BUTR's service used for ingesting crash reports",
            });

            var basicSecurityScheme = new OpenApiSecurityScheme
            {
                Scheme = BasicDefaults.AuthenticationScheme,
                Name = HeaderNames.Authorization,
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Description = "Basic Authorization header using the Bearer scheme.",
            };
            opt.AddSecurityDefinition(BasicDefaults.AuthenticationScheme, basicSecurityScheme);
            opt.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                { new OpenApiSecuritySchemeReference(BasicDefaults.AuthenticationScheme, document), new List<string>() }
            });

            opt.DescribeAllParametersInCamelCase();
            opt.SupportNonNullableReferenceTypes();

            var currentAssembly = typeof(Startup).Assembly;
            var xmlFilePaths = currentAssembly.GetReferencedAssemblies()
                .Append(currentAssembly.GetName())
                .Select(x => Path.Combine(Path.GetDirectoryName(currentAssembly.Location)!, $"{x.Name}.xml"))
                .Where(File.Exists)
                .ToList();
            foreach (var xmlFilePath in xmlFilePaths)
                opt.IncludeXmlComments(xmlFilePath);
        });

        services.AddAuthentication(BasicDefaults.AuthenticationScheme).AddBasic<BasicUserValidationService>(options =>
        {
            options.Realm = "BUTR.CrashReportServer";
            options.IgnoreAuthenticationIfAllowAnonymous = true;
        });

        services.AddControllers().AddJsonOptions(opts =>
        {
            opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            opts.JsonSerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });
        services.Configure<JsonSerializerOptions>(opts =>
        {
            opts.PropertyNameCaseInsensitive = true;
            opts.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        /*
        services.AddResponseCompression(opts =>
        {
            opts.EnableForHttps = true;
            opts.Providers.Add<BrotliCompressionProvider>();
            opts.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.SmallestSize;
        });
        */

        // Stored reports are immutable, so the public read endpoints are cached and the entry is
        // evicted by tag when a report is deleted (see ReportOutputCachePolicy / ReportController).
        services.AddOutputCache(options =>
        {
            options.SizeLimit = 32 * 1024 * 1024;
            options.MaximumBodySize = 8 * 1024 * 1024;
            options.AddPolicy(ReportsCachePolicyName, builder => builder.AddPolicy<ReportOutputCachePolicy>());
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = "CF-Connecting-IP";
            options.KnownNetworks.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        // Protect the public, anonymous crash-upload endpoint from abuse. Partitioned per client IP.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(UploadRateLimitPolicyName, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        services.AddHealthChecks()
            .AddNpgSql(_configuration.GetConnectionString("Main")!);
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseForwardedHeaders();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseSwagger();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", _appName));

        var wwwPath = Path.Combine(env.ContentRootPath, "www");
        if (Directory.Exists(wwwPath))
        {
            var wwwFiles = new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwPath),
                RequestPath = "",
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=86400";
                },
            };
            app.UseStaticFiles(wwwFiles);
        }

        app.UseHealthChecks("/healthz");

        app.UseRouting();

        app.UseRateLimiter();

        app.UseOutputCache();
        //app.UseResponseCompression();

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/version", (HttpContext ctx) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Text(Environment.GetEnvironmentVariable("BUILD_VERSION") ?? "unknown", "text/plain");
            }).AllowAnonymous();

            endpoints.MapControllers();
        });
    }
}