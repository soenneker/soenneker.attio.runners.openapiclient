using Soenneker.Tests.HostedUnit;

namespace Soenneker.Attio.Runners.OpenApiClient.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class AttioOpenApiClientRunnerTests : HostedUnitTest
{
    public AttioOpenApiClientRunnerTests(Host host) : base(host)
    {
    }

    [Test]
    public void Default()
    {

    }
}
