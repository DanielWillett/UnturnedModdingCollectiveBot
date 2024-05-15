using System.Text;

namespace UnturnedModdingCollective.Util;
public class ListUtility
{
    public static void AppendCommaList<T>(StringBuilder sb, ICollection<T> collection, Func<T, string> converter)
    {
        int index = 0;
        foreach (T item in collection)
        {
            string toString = converter(item);

            if (index != 0 && collection.Count != 2)
                sb.Append(", ");

            if (index == collection.Count - 1 && index > 0)
            {
                if (collection.Count == 2) sb.Append(' ');
                sb.Append("and ");
            }

            sb.Append(toString);
            ++index;
        }
    }
    // why tf is ICollection not assignable to IReadOnlyCollection
    public static void AppendCommaList<T>(StringBuilder sb, IReadOnlyCollection<T> collection, Func<T, string> converter)
    {
        int index = 0;
        foreach (T item in collection)
        {
            string toString = converter(item);

            if (index != 0 && collection.Count != 2)
                sb.Append(", ");

            if (index == collection.Count - 1 && index > 0)
            {
                if (collection.Count == 2) sb.Append(' ');
                sb.Append("and ");
            }

            sb.Append(toString);
            ++index;
        }
    }
}
