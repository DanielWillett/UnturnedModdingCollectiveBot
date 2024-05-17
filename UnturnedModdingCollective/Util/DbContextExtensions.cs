using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;

namespace UnturnedModdingCollective.Util;
public static class DbContextExtensions
{
    // modified from https://stackoverflow.com/a/16625307
    // in https://stackoverflow.com/questions/2519866/how-do-i-delete-multiple-rows-in-entity-framework-without-foreach
    public static Task<int> RemoveWhere<T>(this DbContext dbContext, Expression<Func<T, bool>> whereClause, CancellationToken token, params object[] parameters) where T : class
    {
        parameters ??= Array.Empty<object>();

        IQueryable<T> query = dbContext.Set<T>().Where(whereClause);

        string selectQuery = query.ToQueryString();
        StringBuilder newQuery = new StringBuilder(selectQuery.Length);

        int nextSetIndexLook = 0;
        int paramIndex = 0;
        while (true)
        {
            int setIndex = selectQuery.IndexOf("SET", nextSetIndexLook, StringComparison.Ordinal);
            if (setIndex == -1)
                break;

            nextSetIndexLook = setIndex + 3;

            int equals = selectQuery.IndexOf('=', nextSetIndexLook);
            if (equals == -1)
                break;

            nextSetIndexLook = equals + 1;

            int semiColon = selectQuery.IndexOf(';', nextSetIndexLook);
            if (semiColon == -1)
                break;

            nextSetIndexLook = semiColon + 1;

            newQuery.Append("SET ");

            ReadOnlySpan<char> varName = selectQuery.AsSpan(setIndex + 3, equals - (setIndex + 3)).Trim();
            newQuery.Append(varName);
            newQuery.Append(" = ");
            newQuery.Append("@p").Append(paramIndex.ToString(CultureInfo.InvariantCulture)).Append(';').Append(Environment.NewLine);
            ++paramIndex;
        }

        if (paramIndex > 0)
            newQuery.Append(Environment.NewLine);

        int selectIndex = selectQuery.IndexOf("SELECT", StringComparison.Ordinal);
        int fromIndex = selectQuery.IndexOf("FROM", StringComparison.Ordinal);
        int whereIndex = selectQuery.IndexOf("WHERE", StringComparison.Ordinal);

        ReadOnlySpan<char> beforeSelect = selectQuery.AsSpan(nextSetIndexLook, selectIndex - nextSetIndexLook);
        ReadOnlySpan<char> fromSql = selectQuery.AsSpan(fromIndex, whereIndex - fromIndex);
        ReadOnlySpan<char> whereSql = selectQuery.AsSpan(whereIndex);
        int asIndex = fromSql.IndexOf(" AS ", StringComparison.Ordinal);
        ReadOnlySpan<char> aliasSQl = asIndex > -1 ? fromSql.Slice(asIndex + 4) : default;

        newQuery.Append(beforeSelect.Trim());
        newQuery.Append("DELETE ");
        newQuery.Append(aliasSQl.Trim());
        newQuery.Append(' ');
        newQuery.Append(fromSql.Trim());
        newQuery.Append(' ');
        newQuery.Append(whereSql.Trim());

        return dbContext.Database.ExecuteSqlRawAsync(newQuery.ToString(), parameters, token);
    }
}