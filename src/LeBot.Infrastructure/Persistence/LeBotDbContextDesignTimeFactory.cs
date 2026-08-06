using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LeBot.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations</c> build the context without booting the whole Host. The connection
/// string is a throwaway — generating a migration needs the model shape, not a live database.
/// </summary>
public sealed class LeBotDbContextDesignTimeFactory : IDesignTimeDbContextFactory<LeBotDbContext>
{
    public LeBotDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LeBotDbContext>()
            .UseSqlite("Data Source=lebot.design.db")
            .Options;

        return new LeBotDbContext(options);
    }
}
