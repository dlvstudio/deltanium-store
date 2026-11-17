using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace DeltaniumStore.Data
{
    public static class StoreSchema
    {
        /// <summary>
        /// Create this context's tables if missing. EnsureCreated() does nothing once the database has ANY table,
        /// so prefixed tables in a shared database are created with CREATE TABLE IF NOT EXISTS instead.
        /// </summary>
        public static void EnsureTables(StoreDbContext db)
        {
            if (!db.Database.IsRelational())
            {
                db.Database.EnsureCreated();
                return;
            }
            var script = db.Database.GenerateCreateScript();
            foreach (var statement in script.Split(';').Select(s => s.Trim()))
            {
                if (!statement.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase)) continue;
                var sql = Regex.Replace(statement, "^CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", RegexOptions.IgnoreCase);
                db.Database.ExecuteSqlRaw(sql.Replace("{", "{{").Replace("}", "}}"));
            }
        }
    }
}
