namespace HR.Tests.Integration.Infrastructure;

/// <summary>Base class: shares the run-wide fixture and resets the test database before each test.</summary>
[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTest(TestDatabaseFixture fixture) : IAsyncLifetime
{
    protected TestDatabaseFixture Fixture { get; } = fixture;

    public Task InitializeAsync() => Fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}
