// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Nats;
using Aspire.NATS.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using Polly;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding NATS resources to the application model.
/// </summary>
public static class NatsBuilderExtensions
{
    /// <summary>
    /// Adds a NATS server resource to the application model. A container is used for local development.
    /// This configures a default user name and password for the NATS server.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="NatsContainerImageTags.Tag"/> tag of the <inheritdoc cref="NatsContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for NATS server.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> AddNats(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
    {
        return AddNats(builder, name, port, null);
    }

    /// <summary>
    /// Adds a NATS server resource to the application model. A container is used for local development.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="NatsContainerImageTags.Tag"/> tag of the <inheritdoc cref="NatsContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port for NATS server.</param>
    /// <param name="userName">The parameter used to provide the user name for the PostgreSQL resource. If <see langword="null"/> a default value will be used.</param>
    /// <param name="password">The parameter used to provide the administrator password for the PostgreSQL resource. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> AddNats(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port = null,
        IResourceBuilder<ParameterResource>? userName = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var nats = new NatsServerResource(name, userName?.Resource, passwordParameter);

        NatsConnection? natsConnection = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(nats, async (@event, ct) =>
        {
            var connectionString = await nats.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false)
            ?? throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{nats.Name}' resource but the connection string was null.");

            var options = NatsOpts.Default with
            {
                LoggerFactory = @event.Services.GetRequiredService<ILoggerFactory>(),
            };

            options = options with
            {
                Url = connectionString,
                AuthOpts = new()
                {
                    Username = await nats.UserNameReference.GetValueAsync(ct).ConfigureAwait(false),
                    Password = nats.PasswordParameter!.Value,
                }
            };

            natsConnection = new NatsConnection(options);
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
          .Add(new HealthCheckRegistration(
              healthCheckKey,
              sp => new NatsHealthCheck(natsConnection!),
              failureStatus: default,
              tags: default,
              timeout: default));

        return builder.AddResource(nats)
            .WithEndpoint(targetPort: 4222, port: port, name: NatsServerResource.PrimaryEndpointName)
            .WithImage(NatsContainerImageTags.Image, NatsContainerImageTags.Tag)
            .WithImageRegistry(NatsContainerImageTags.Registry)
            .WithHealthCheck(healthCheckKey)
            .WithArgs(context =>
            {
                context.Args.Add("--user");
                context.Args.Add(nats.UserNameReference);
                context.Args.Add("--pass");
                context.Args.Add(nats.PasswordParameter!);
            });
    }

    /// <summary>
    /// Adds JetStream support to the NATS server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="srcMountPath">Optional mount path providing persistence between restarts.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("This method is obsolete and will be removed in a future version. Use the overload without the srcMountPath parameter and WithDataBindMount extension instead if you want to keep data locally.")]
    public static IResourceBuilder<NatsServerResource> WithJetStream(this IResourceBuilder<NatsServerResource> builder, string? srcMountPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var args = new List<string> { "-js" };
        if (srcMountPath != null)
        {
            args.Add("-sd");
            args.Add("/data");
            builder.WithBindMount(srcMountPath, "/data");
        }

        return builder.WithArgs(args.ToArray());
    }

    /// <summary>
    /// Adds JetStream support to the NATS server resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> WithJetStream(this IResourceBuilder<NatsServerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithArgs("-js");
    }

    /// <summary>
    /// Configures a container resource for Nui (a web UI for NATS) which is pre-configured to connect to the <see cref="NatsServerResource"/> that this method is used on.
    /// </summary>
    /// <param name="builder">The NATS server resource builder.</param>
    /// <param name="configureContainer">Configuration callback for Nui container resource.</param>
    /// <param name="containerName">The name of the container (Optional).</param>
    /// <example>
    /// Use in application host with a NATS resource
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var nats = builder.AddNats("nats")
    ///    .WithJetStream()
    ///    .WithNui();
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(nats);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="NatsContainerImageTags.NuiTag"/> tag of the <inheritdoc cref="NatsContainerImageTags.NuiImage"/> container image.
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> WithNui(this IResourceBuilder<NatsServerResource> builder,
        Action<IResourceBuilder<NuiContainerResource>>? configureContainer = null,
        string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder!.Resources.OfType<NuiContainerResource>().SingleOrDefault() is { } existingNuiResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingNuiResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= $"{builder.Resource.Name}-nui";

            builder.ApplicationBuilder.Services.AddHttpClient();

            var nuiContainer = new NuiContainerResource(containerName);
            var nuiContainerBuilder = builder.ApplicationBuilder.AddResource(nuiContainer)
                                                 .WithImage(NatsContainerImageTags.NuiImage, NatsContainerImageTags.NuiTag)
                                                 .WithImageRegistry(NatsContainerImageTags.NuiRegistry)
                                                 .WithHttpEndpoint(targetPort: 31311, name: "http")
                                                 .WithVolume(VolumeNameGenerator.Generate(builder, "db"), "/db")
                                                 .ExcludeFromManifest();

            builder.ApplicationBuilder.Eventing.Subscribe<AfterResourcesCreatedEvent>(async (e, ct) =>
            {
                var natsInstances = builder.ApplicationBuilder.Resources.OfType<NatsServerResource>();

                if (!natsInstances.Any())
                {
                    // No-op if there are no NATS resources present.
                    return;
                }

                var nuiInstance = builder.ApplicationBuilder.Resources.OfType<NuiContainerResource>().Single();
                var nuiEnpoint = nuiInstance.PrimaryEndpoint;

                var httpClientFactory = e.Services.GetRequiredService<IHttpClientFactory>();

                foreach (var natsInstance in natsInstances)
                {
                    if (natsInstance.PrimaryEndpoint.IsAllocated)
                    {
                        var natsEndpoint = natsInstance.PrimaryEndpoint;

                        var nuiConnectionConfig = new NuiConnectionConfig(natsInstance.Name)
                        {
                            Hosts = [$"{natsEndpoint.Resource.Name}:{natsEndpoint.TargetPort}"]
                        };

                        if (!string.IsNullOrEmpty(natsInstance.PasswordParameter?.Value))
                        {
                            nuiConnectionConfig.Auth = [
                                new NuiConnectionAuth
                                {
                                    Active = true,
                                    Mode = "auth_user_password",
                                    Username = await natsInstance.UserNameReference.GetValueAsync(ct).ConfigureAwait(false),
                                    Password = natsInstance.PasswordParameter?.Value
                                }
                            ];
                        }

                        var client = httpClientFactory.CreateClient();
                        client.BaseAddress = new Uri($"{nuiEnpoint.Scheme}://{nuiEnpoint.Host}:{nuiEnpoint.Port}");

                        var pipeline = new ResiliencePipelineBuilder().AddRetry(new Polly.Retry.RetryStrategyOptions
                        {
                            Delay = TimeSpan.FromSeconds(2),
                            MaxRetryAttempts = 5,
                        }).Build();

                        await pipeline.ExecuteAsync(async (ctx) =>
                        {
                            var response = await client.PostAsJsonAsync("/api/connection", nuiConnectionConfig, ctx)
                                .ConfigureAwait(false);

                            response.EnsureSuccessStatusCode();

                            var d = await response.Content.ReadFromJsonAsync<NuiConnectionConfig>(ctx).ConfigureAwait(false);

                        }, ct).ConfigureAwait(false);
                    }
                }
            });

            configureContainer?.Invoke(nuiContainerBuilder);

            return builder;
        }
    }

    /// <summary>
    /// Configures the host port that the Nui resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for Nui.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for Nui.</returns>
    /// <example>
    /// Use in application host with a Nui resource
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var nats = builder.AddNats("nats")
    ///    .WithNui(c => c.WithHostPort(1000));
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithReference(nats);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    public static IResourceBuilder<NuiContainerResource> WithHostPort(this IResourceBuilder<NuiContainerResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only volume.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> WithDataVolume(this IResourceBuilder<NatsServerResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/var/lib/nats",
                isReadOnly)
            .WithArgs("-sd", "/var/lib/nats");
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a NATS container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">A flag that indicates if this is a read-only mount.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    public static IResourceBuilder<NatsServerResource> WithDataBindMount(this IResourceBuilder<NatsServerResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/var/lib/nats", isReadOnly)
            .WithArgs("-sd", "/var/lib/nats");
    }
}
