using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace UnturnedModdingCollective.Util;
public static class DbContextExtensions
{
    // https://stackoverflow.com/a/16625307
    // in https://stackoverflow.com/questions/2519866/how-do-i-delete-multiple-rows-in-entity-framework-without-foreach
    public static Task<int> RemoveWhere<T>(this DbContext dbContext, Expression<Func<T, bool>> whereClause, CancellationToken token = default) where T : class
    {
        string selectQuery = dbContext.Set<T>().Where(whereClause).ToQueryString();
        int fromIndex = selectQuery.IndexOf("FROM", StringComparison.Ordinal);
        int whereIndex = selectQuery.IndexOf("WHERE", StringComparison.Ordinal);

        string fromSql = selectQuery.Substring(fromIndex, whereIndex - fromIndex);
        string whereSql = selectQuery.Substring(whereIndex);
        string aliasSQl = fromSql.IndexOf(" AS ", StringComparison.Ordinal) > -1 ? fromSql.Substring(fromSql.IndexOf(" AS ", StringComparison.Ordinal) + 4) : "";
        string deleteSql = string.Join(" ", "DELETE ", aliasSQl.Trim(), fromSql.Trim(), whereSql.Trim());

        return dbContext.Database.ExecuteSqlRawAsync(deleteSql, token);
    }
}
