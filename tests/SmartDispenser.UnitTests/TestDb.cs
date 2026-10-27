using Microsoft.EntityFrameworkCore;
using SmartDispenser.Infrastructure.Data;

namespace SmartDispenser.UnitTests;

public static class TestDb
{
    public static AppDbContext Create() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
