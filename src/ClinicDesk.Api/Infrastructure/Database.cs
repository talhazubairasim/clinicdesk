using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Infrastructure;

public static class Database
{
    /// <summary>
    /// Creates the schema, then adds what EF can't express: an exclusion constraint so a
    /// practitioner can never have two live appointments that overlap, even under concurrent bookings.
    /// </summary>
    public static async Task InitialiseAsync(IServiceProvider services, ILogger log)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                break;
            }
            catch (Exception e) when (attempt < 10)
            {
                log.LogWarning("Database not ready ({Message}); retrying", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        await db.Database.ExecuteSqlRawAsync("""
            CREATE EXTENSION IF NOT EXISTS btree_gist;
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'appointments_no_overlap') THEN
                ALTER TABLE appointments ADD CONSTRAINT appointments_no_overlap
                  EXCLUDE USING gist ("PractitionerId" WITH =, tstzrange("StartsAt", "EndsAt") WITH &&)
                  WHERE ("Status" <> 'Cancelled');
              END IF;
            END $$;
            """);
    }
}
