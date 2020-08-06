using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace error_transformer
{
    public static class Utils
    {
        public static IEnumerable<T> EmptyIfNull<T>(this IEnumerable<T> collection)
        {
            if (collection == null)
                return Enumerable.Empty<T>();
            else
                return collection;
        }
    }
}
