using DdoDatApi.Caching;
using DdoDatApi.Converters;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DdoDatApi;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.

        builder.Services.AddControllersWithViews()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.Converters.Add(new IPropertyJsonConverter());
            });

        builder.Services.RegisterSingletonService<ICacheBuilderService, CacheBuilderService>();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DDOStudioViewer", policy =>
                policy.WithOrigins("https://viewer.ddo", "https://ddocache.ddo")
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        var app = builder.Build();
        app.UseHttpsRedirection();
        app.UseCors("DDOStudioViewer");
        app.UseStaticFiles();
        app.MapControllers();

        DatSource.Load();
        IndexLoader.LoadIndexCache();

        app.Run();
    }

    public static IServiceCollection RegisterSingletonService<T1, T2>(this IServiceCollection serviceCollection)
        where T1 : class
        where T2 : class, T1, IHostedService
    {
        serviceCollection.AddSingleton<T1, T2>();
        serviceCollection.AddHostedService(p => (T2)p.GetRequiredService<T1>());
        return serviceCollection;
    }
}