using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Iridium.Native
{
    /// <summary>
    /// Materializes the native flat JSON DOM (iridium_core) into the same
    /// managed object graph GDMiniJSON produces:
    /// Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / int /
    /// float / bool / null.
    ///
    /// Every entry point falls back to the managed parser by returning false;
    /// callers must treat false as "run the original".
    /// </summary>
    internal static class NativeJson
    {
        /// <summary>Below this many UTF-16 chars the managed parser wins.</summary>
        private const int MinLength = 4096;

        public static bool TryParse(string json, string? section, out object? result)
        {
            result = null;
            if (json == null || json.Length < MinLength) return false;
            if (!IridiumNative.HasJson) return false;
            if (!IridiumNative.JsonParse(json, section)) return false;

            try
            {
                if (!IridiumNative.JsonGetView(out var view)) return false;
                return Materialize(view, out result);
            }
            finally
            {
                IridiumNative.JsonRelease();
            }
        }

        private static bool Materialize(IridiumNative.JsonView view, out object? result)
        {
            result = null;
            int nodeCount = (int)view.NodeCount;
            if (nodeCount <= 0 || view.NodeCount > int.MaxValue) return false;
            int childCount = (int)view.ChildCount;
            int stringCount = (int)view.StringCount;
            int poolLen = (int)view.StringPoolLen;
            if (view.ChildCount > int.MaxValue || view.StringCount > int.MaxValue ||
                view.StringPoolLen > int.MaxValue)
                return false;

            var kinds = new byte[nodeCount];
            var a = new int[nodeCount];
            var b = new int[nodeCount];
            var values = new float[nodeCount];
            var children = childCount > 0 ? new int[childCount] : Array.Empty<int>();
            var strOff = stringCount > 0 ? new int[stringCount] : Array.Empty<int>();
            var strLen = stringCount > 0 ? new int[stringCount] : Array.Empty<int>();

            Marshal.Copy(view.Kinds, kinds, 0, nodeCount);
            Marshal.Copy(view.A, a, 0, nodeCount);
            Marshal.Copy(view.B, b, 0, nodeCount);
            Marshal.Copy(view.Values, values, 0, nodeCount);
            if (childCount > 0) Marshal.Copy(view.Children, children, 0, childCount);
            if (stringCount > 0)
            {
                Marshal.Copy(view.StrOff, strOff, 0, stringCount);
                Marshal.Copy(view.StrLen, strLen, 0, stringCount);
            }

            // UTF-16 pool copied as shorts to avoid Marshal.Copy(char[]) ambiguity.
            string[]? strings = null;
            if (poolLen > 0)
            {
                var pool = new char[poolLen];
                var poolShorts = new short[poolLen];
                Marshal.Copy(view.Strings, poolShorts, 0, poolLen);
                Buffer.BlockCopy(poolShorts, 0, pool, 0, poolLen * 2);
                strings = new string[stringCount];
                for (int i = 0; i < stringCount; i++)
                {
                    strings[i] = new string(pool, strOff[i], strLen[i]);
                }
            }

            int root = (int)view.Root;
            if ((uint)root >= (uint)nodeCount) return false;

            try
            {
                result = Build(kinds, a, b, values, children, strings, root, 0);
            }
            catch (IndexOutOfRangeException)
            {
                result = null;
                return false;
            }
            catch (ArgumentException)
            {
                result = null;
                return false;
            }
            return true;
        }

        private static object? Build(
            byte[] kinds, int[] a, int[] b, float[] values,
            int[] children, string[]? strings, int node, int depth)
        {
            if (depth > 256) return null;
            switch (kinds[node])
            {
                case 0: return null;
                case 1: return false;
                case 2: return true;
                case 3: return a[node];
                case 4: return values[node];
                case 5: return strings != null ? strings[a[node]] : "";
                case 6:
                {
                    int start = a[node];
                    int count = b[node];
                    var list = new List<object>(count);
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(Build(kinds, a, b, values, children, strings, children[start + i], depth + 1));
                    }
                    return list;
                }
                case 7:
                {
                    int start = a[node];
                    int count = b[node];
                    var dict = new Dictionary<string, object>(count);
                    for (int i = 0; i < count; i++)
                    {
                        int keyNode = children[start + i * 2];
                        int valNode = children[start + i * 2 + 1];
                        // Key nodes are always strings; MiniJSON's indexer
                        // assignment means duplicate keys resolve to last.
                        string key = strings != null ? strings[a[keyNode]] : "";
                        dict[key] = Build(kinds, a, b, values, children, strings, valNode, depth + 1);
                    }
                    return dict;
                }
                default:
                    return null;
            }
        }
    }
}
