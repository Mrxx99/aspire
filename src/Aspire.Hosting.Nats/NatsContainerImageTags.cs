// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Nats;

internal static class NatsContainerImageTags
{
    /// <remarks>docker.io</remarks>
    public const string Registry = "docker.io";

    /// <remarks>library/nats</remarks>
    public const string Image = "library/nats";

    /// <remarks>2.11</remarks>
    public const string Tag = "2.11";

    public const string NuiRegistry = "ghcr.io";

    /// <remarks>nats-nui/nui</remarks>
    public const string NuiImage = "nats-nui/nui";

    /// <remarks>0.6.1</remarks>
    public const string NuiTag = "0.6.1";
}
