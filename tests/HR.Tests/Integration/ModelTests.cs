using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace HR.Tests.Integration;

public class ModelTests
{
    [Fact]
    public void AppDbContext_has_no_pending_model_changes()
    {
        // Compares the model with the latest migration snapshot; no database connection is opened.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=HRPayroll_ModelCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes not captured in a migration. Add a migration.");
    }

    [Fact]
    public void ApplicationUser_columns_are_configured()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=.\\SQLEXPRESS;Database=HRPayroll_ModelCheck;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(options);

        var user = db.Model.FindEntityType(typeof(ApplicationUser))!;
        var fullName = user.FindProperty(nameof(ApplicationUser.FullName))!;
        Assert.False(fullName.IsNullable);
        Assert.Equal(ApplicationUser.FullNameMaxLength, fullName.GetMaxLength());
        Assert.False(user.FindProperty(nameof(ApplicationUser.IsActive))!.IsNullable);
        Assert.Equal(typeof(DateTimeOffset), user.FindProperty(nameof(ApplicationUser.CreatedAt))!.ClrType);
    }
}
