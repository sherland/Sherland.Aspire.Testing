using Xunit;

namespace Sherland.Aspire.DemoTests;

[CollectionDefinition("DemoTests", DisableParallelization = true)]
public class DemoCollection : ICollectionFixture<DemoFixture>
{
}
