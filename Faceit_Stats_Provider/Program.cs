using Faceit_Stats_Provider.Classes;
using Faceit_Stats_Provider.Interfaces;
using Faceit_Stats_Provider.Models;
using Faceit_Stats_Provider.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpClient();

builder.Services.AddHttpClient("Faceit", httpClient =>
{
    httpClient.BaseAddress = new Uri("https://open.faceit.com/data/");
    httpClient.DefaultRequestHeaders.Add("Authorization", builder.Configuration.GetValue<string>("FaceitAPI"));
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddHttpClient("FaceitV1", httpClient =>
{
    httpClient.BaseAddress = new Uri("https://api.faceit.com/stats/");
});

// Add memory cache
builder.Services.AddMemoryCache();

// Add Redis connection
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var configuration = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis"), true);
    configuration.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(configuration);
});

builder.Services.AddSingleton<GetTotalEloRetrievesCountFromRedis>();
builder.Services.AddSingleton<ILoadMoreMatches, LoadMoreMatchesService>();
builder.Services.AddSingleton<IRetryPolicy, RetryPolicyService>();
builder.Services.AddSingleton<IFetchMaxElo, FetchMaxEloService>();
builder.Services.AddSingleton<IGetMatchDetails, GetMatchDetailsService>();
builder.Services.AddSingleton<IOnlyCsGoStats, OnlyCsGoStatsService>();
builder.Services.AddSingleton<IToggleIncludeCsGoStats, ToggleIncludeCsGoStatsService>();
builder.Services.AddSingleton<ITogglePlayer, TogglePlayerService>();
builder.Services.AddSingleton<IPlayerStatistics, PlayerStatisticsService>();
builder.Services.AddSingleton<IHttpClientRetryService, HttpClientRetryService>();
builder.Services.AddSingleton<HttpClientManager>();

// Add response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// Use response compression middleware
app.UseResponseCompression();

app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "loadMoreMatches",
    pattern: "PlayerStats/LoadMoreMatches",
    defaults: new { controller = "PlayerStats", action = "LoadMatches" });

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
});

app.Run();
