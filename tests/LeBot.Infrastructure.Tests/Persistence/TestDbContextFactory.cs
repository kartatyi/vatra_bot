using LeBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeBot.Infrastructure.Tests.Persistence;

/// <summary>
/// Minimal <see cref="IDbContextFactory{TContext}"/> over fixed options, so tests can drive the
/// singleton-style store and initializer without spinning up DI. The interface supplies
/// <c>CreateDbContextAsync</c> by default, so only the sync factory method is needed here.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<LeBotDbContext> options)
    : IDbContextFactory<LeBotDbContext>
{
    public LeBotDbContext CreateDbContext() => new(options);
}
