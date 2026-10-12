using Challenge.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Challenge.Api.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db)
    {
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var now = DateTime.UtcNow;
        var hasher = new PasswordHasher<User>();

        var admin = new User
        {
            Username = "admin",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = now
        };
        admin.PasswordHash = hasher.HashPassword(admin, "Admin123!");

        var sales = new User
        {
            Username = "sales",
            Role = UserRole.Sales,
            IsActive = true,
            CreatedAt = now
        };
        sales.PasswordHash = hasher.HashPassword(sales, "Sales123!");

        db.Users.AddRange(admin, sales);

        db.Products.AddRange(
            P("LAPTOP-001", "Laptop 14 inch", 1_500.00m, 5, now),
            P("MOUSE-001", "Wireless Mouse", 25.00m, 80, now),
            P("KEYBOARD-001", "Mechanical Keyboard", 90.00m, 40, now),
            P("MONITOR-001", "27-inch Monitor", 320.00m, 18, now),
            P("HEADSET-001", "Office Headset", 45.00m, 2, now),
            P("DOCK-001", "USB-C Dock", 140.00m, 22, now),
            P("WEBCAM-001", "1080p Webcam", 60.00m, 35, now),
            P("SSD-001", "1TB SSD", 110.00m, 50, now),
            P("CHAIR-001", "Office Chair", 210.00m, 12, now),
            P("CABLE-001", "HDMI Cable", 8.00m, 200, now)
        );

        db.Customers.AddRange(
            C("CUST-001", "Acme Trading", "09120000001", now),
            C("CUST-002", "Northwind Ltd", "09120000002", now),
            C("CUST-003", "Contoso Retail", "09120000003", now),
            C("CUST-004", "Sahand Store", "09120000004", now),
            C("CUST-005", "Pars Office", "09120000005", now, isActive: false)
        );

        await db.SaveChangesAsync();
    }

    private static Product P(string code, string name, decimal price, int stock, DateTime now) => new()
    {
        Code = code,
        Name = name,
        Price = price,
        StockQuantity = stock,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static Customer C(string code, string name, string phone, DateTime now, bool isActive = true) => new()
    {
        Code = code,
        Name = name,
        PhoneNumber = phone,
        IsActive = isActive,
        CreatedAt = now,
        UpdatedAt = now
    };
}
