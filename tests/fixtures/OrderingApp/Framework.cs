// Minimal stand-ins for the MediatR, ASP.NET Core MVC, and Microsoft.Extensions framework types the
// OrderingApp fixture uses. They live in the real framework namespaces so the semantic analyzers detect them
// exactly as they would the real packages, while keeping the fixture hermetic: it compiles in-memory from
// source with no NuGet restore. This file is test scaffolding, not production code.

namespace MediatR
{
    public struct Unit { }

    public interface IRequest<out TResponse> { }

    public interface IRequest : IRequest<Unit> { }

    public interface IRequestHandler<in TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        System.Threading.Tasks.Task<TResponse> Handle(TRequest request, System.Threading.CancellationToken cancellationToken);
    }

    public interface INotification { }

    public interface INotificationHandler<in TNotification>
        where TNotification : INotification
    {
        System.Threading.Tasks.Task Handle(TNotification notification, System.Threading.CancellationToken cancellationToken);
    }

    public interface ISender
    {
        System.Threading.Tasks.Task<TResponse> Send<TResponse>(IRequest<TResponse> request, System.Threading.CancellationToken cancellationToken = default);
    }

    public interface IMediator : ISender { }
}

namespace Microsoft.AspNetCore.Mvc
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RouteAttribute : System.Attribute
    {
        public RouteAttribute(string template) => Template = template;

        public string Template { get; }
    }

    public abstract class HttpMethodAttribute : System.Attribute
    {
        protected HttpMethodAttribute(string? template) => Template = template;

        public string? Template { get; }
    }

    public sealed class HttpGetAttribute : HttpMethodAttribute
    {
        public HttpGetAttribute(string? template = null) : base(template) { }
    }

    public sealed class HttpPostAttribute : HttpMethodAttribute
    {
        public HttpPostAttribute(string? template = null) : base(template) { }
    }

    public sealed class HttpPutAttribute : HttpMethodAttribute
    {
        public HttpPutAttribute(string? template = null) : base(template) { }
    }

    public sealed class HttpDeleteAttribute : HttpMethodAttribute
    {
        public HttpDeleteAttribute(string? template = null) : base(template) { }
    }

    public sealed class ApiControllerAttribute : System.Attribute { }

    public interface IActionResult { }

    public abstract class ControllerBase
    {
        protected IActionResult Ok() => null!;

        protected IActionResult Ok(object value) => null!;
    }
}

namespace Microsoft.Extensions.Options
{
    public interface IOptions<out TOptions>
        where TOptions : class
    {
        TOptions Value { get; }
    }
}

namespace Microsoft.Extensions.Configuration
{
    public interface IConfiguration
    {
        IConfigurationSection GetSection(string key);
    }

    public interface IConfigurationSection : IConfiguration
    {
        string Key { get; }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
            where TImplementation : class, TService => services;

        public static IServiceCollection AddScoped<TService>(this IServiceCollection services) => services;

        public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services)
            where TImplementation : class, TService => services;

        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services)
            where TImplementation : class, TService => services;

        public static IServiceCollection AddScoped(this IServiceCollection services, System.Type serviceType, System.Type implementationType) => services;

        public static IServiceCollection TryAddSingleton<TService, TImplementation>(this IServiceCollection services)
            where TImplementation : class, TService => services;

        public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services)
            where THostedService : class, Microsoft.Extensions.Hosting.IHostedService => services;

        public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, System.Func<System.IServiceProvider, TService> factory) => services;

        // Scrutor-style decoration registration.
        public static IServiceCollection Decorate<TService, TDecorator>(this IServiceCollection services)
            where TDecorator : TService => services;

        // Keyed DI (net8+): the service key is a value argument; the service and implementation are the type args.
        public static IServiceCollection AddKeyedScoped<TService, TImplementation>(this IServiceCollection services, object serviceKey)
            where TImplementation : class, TService => services;

        // Typed HttpClient (Microsoft.Extensions.Http): AddHttpClient<TClient, TImplementation>() registers the
        // implementation as the typed client for TClient. The real method returns IHttpClientBuilder; the fixture
        // returns the collection since it does not chain, which is all the analyzer's type-argument extraction needs.
        public static IServiceCollection AddHttpClient<TClient, TImplementation>(this IServiceCollection services)
            where TClient : class
            where TImplementation : class, TClient => services;
    }

    public static class OptionsServiceCollectionExtensions
    {
        public static IServiceCollection Configure<TOptions>(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
            where TOptions : class => services;
    }
}

namespace Microsoft.Extensions.Hosting
{
    // Stubs of the hosting abstractions so the fixture can declare a background worker without a package.
    public interface IHostedService
    {
        System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken);

        System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken);
    }

    public abstract class BackgroundService : IHostedService
    {
        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken) =>
            ExecuteAsync(cancellationToken);

        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;

        protected abstract System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken);
    }
}

namespace MediatR
{
    // The pipeline-behavior abstraction: a behavior wraps request handling.
    public interface IPipelineBehavior<TRequest, TResponse>
    {
        System.Threading.Tasks.Task<TResponse> Handle(
            TRequest request, System.Func<System.Threading.Tasks.Task<TResponse>> next, System.Threading.CancellationToken cancellationToken);
    }
}

namespace Microsoft.AspNetCore.SignalR
{
    // Minimal SignalR hub base so the fixture can declare a hub mapped with MapHub<T>.
    public abstract class Hub { }
}

namespace Microsoft.AspNetCore.Builder
{
    // Minimal endpoint-builder stubs so the fixture can map minimal-API, gRPC, and SignalR endpoints.
    public sealed class WebApplication
    {
        public WebApplication MapGet(string pattern, System.Delegate handler) => this;

        public WebApplication MapPost(string pattern, System.Delegate handler) => this;

        public WebApplication MapPut(string pattern, System.Delegate handler) => this;

        public WebApplication MapDelete(string pattern, System.Delegate handler) => this;

        public WebApplication MapGroup(string prefix) => this;

        public WebApplication MapGrpcService<TService>() where TService : class => this;

        public WebApplication MapHub<THub>(string pattern) where THub : Microsoft.AspNetCore.SignalR.Hub => this;
    }
}

namespace Microsoft.EntityFrameworkCore
{
    // Minimal EF Core stubs: a DbContext exposing DbSet<TEntity> and an entity type configuration interface.
    public class DbSet<TEntity> where TEntity : class { }

    public abstract class DbContext { }

    public interface IEntityTypeConfiguration<TEntity> where TEntity : class { }
}

namespace Xunit
{
    public sealed class FactAttribute : System.Attribute { }

    public sealed class TheoryAttribute : System.Attribute { }
}
