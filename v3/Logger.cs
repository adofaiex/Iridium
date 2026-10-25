//#define UNSAFE_MODE

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;


namespace Iridium
{
    public class Logger
    {
        private struct LogData
        {
            public object[] contents;
            // public long timespan;
            public int type;
            public int flag;

            public LogData(object[] contents, int type, int flag = 0)
            {
                this.contents = contents;
                this.type = type;
                this.flag = flag;
            }
        }

        // 多线程队列
        // 注意: 此UNSAFE_MODE只能保证大部分情况下不会出现List被多次写入 小部分情况无法解决 只能改为无锁等待转换(主线程->Task->主线程)
        private const string MODNAME_INFO = "[Iridium/INFO] "; // Prefix Name
        private const string MODNAME_WARN = "[Iridium/WARN] "; // Prefix Name
        private const string MODNAME_ERROR = "[Iridium/ERROR] "; // Prefix Name
        private static readonly ConcurrentQueue<LogData> _writeQueue = new();
        private static Task? _task;
        private static readonly List<string> _history = new();
        private const int _historyCapacity = 500;
#if UNSAFE_MODE
        public static void TaskRun()
        {
            if (_task != null && !_task.IsCompleted)
                return;
            _task = Task.Run(ThreadTask);
        }
        private static void ThreadTask()
        {
            int count = _writeQueue.Count;
            for (int i = 0; i < count; i++)
            {
                while (_writeQueue.TryDequeue(out var data))
                {
                    switch (data.type)
                    {
                        case 1: // WARN
                            _history.Add(MODNAME_WARN + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_WARN + Args2String(data.contents));
                            break;
                        case 2: // ERROR
                            _history.Add(MODNAME_ERROR + "Exception: " + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_ERROR + "Exception: " + Args2String(data.contents));
                            break;
                        case 3: // Dir
                            _history.Add(MODNAME_INFO + "Object:\n" + FormatObject(data.contents[0], data.flag));
                            Console.WriteLine(MODNAME_INFO + "Object:\n" + Object(data.contents[0], data.flag));
                            break;
                        default:// INFO | Other
                            _history.Add(MODNAME_INFO + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_INFO + Args2String(data.contents));
                            break;
                    }
                    if (_history.Count >= _historyCapacity * 2)
                    {
                        string[] collection = _history.Skip(_historyCapacity).ToArray();
                        _history.Clear();
                        _history.AddRange(collection);
                    }
                }
            }
        }
#else
        private static readonly ConcurrentQueue<string> _writeBack = new();
        public static void TaskRun()
        {
            if (_writeQueue.IsEmpty) return;
            if (_task != null && !_task.IsCompleted)
                return;
            _task = Task.Run(ThreadTask);
            // 不每次都释放_history 减少占用
            int wcount = _writeBack.Count;
            if (wcount + _history.Count >= _historyCapacity * 2)
            {
                int hcount = _history.Count;
                _history.Clear();
                int count = (wcount + hcount) / _historyCapacity * _historyCapacity - hcount - _historyCapacity;
                for (int i = 0; i < count; i++)
                {
                    while (!_writeBack.TryDequeue(out _)) ;
                }
            }
            while (_writeBack.TryDequeue(out string res))
            {
                _history.Add(res);
            }
        }
        private static void ThreadTask()
        {
            int count = _writeQueue.Count;
            for (int i = 0; i < count; i++)
            {
                while (_writeQueue.TryDequeue(out var data))
                {
                    switch (data.type)
                    {
                        case 1: // WARN
                            _writeBack.Enqueue(MODNAME_WARN + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_WARN + Args2String(data.contents));
                            Main.Handler?.Warning(Args2String(data.contents));
                            break;
                        case 2: // ERROR
                            _writeBack.Enqueue(MODNAME_ERROR + "Exception: " + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_ERROR + "Exception: " + Args2String(data.contents));
                            Main.Handler?.Error(Args2String(data.contents));
                            break;
                        case 3: // Dir
                            _writeBack.Enqueue(MODNAME_INFO + "Object:\n" + FormatObject(data.contents[0], data.flag));
                            Console.WriteLine(MODNAME_INFO + "Object:\n" + Object(data.contents[0], data.flag));
                            break;
                        default:// INFO | Other
                            _writeBack.Enqueue(MODNAME_INFO + FormatArgs2String(data.contents));
                            Console.WriteLine(MODNAME_INFO + Args2String(data.contents));
                            Main.Handler?.Log(Args2String(data.contents));
                            break;
                    }
                }
            }
        }

#endif

        // 定义颜色常量
        private const string COLOR_RESET = "</color>";
        private const string COLOR_KEYWORD = "<color=#569CD6>"; // 例如：class, struct
        private const string COLOR_STRING = "<color=#CE9178>"; // 字符串
        private const string COLOR_NUMBER = "<color=#B5CEA8>"; // 数字
        private const string COLOR_BOOLEAN = "<color=#569CD6>"; // 布尔值
        private const string COLOR_NULL = "<color=#569CD6>"; // null
        private const string COLOR_OBJECT = "<color=#9CDCFE>"; // 对象名/类型名
        private const string COLOR_PROPERTY_NAME = "<color=#9CDCFE>"; // 对象属性名
        private const string COLOR_BRACKET = "<color=#FFD700>"; // 括号 [] {}
        private const string COLOR_COMMENT = "<color=#6A9955>"; // 注释或额外信息
        private const string COLOR_ERROR = "<color=#F44747>"; // 错误信息
        private const string COLOR_TYPE_NAME = "<color=#4EC9B0>"; // 类型名称

        // 格式化限制
        private const int MAX_DEPTH = 5; // 最大递归深度
        private const int MAX_ARRAY_ELEMENTS = 100; // 数组最大显示元素数量
        private const int MAX_OBJECT_PROPERTIES = 50; // 对象最大显示属性数量

        public Logger() { }

        public void Log(params object[] args)
        {
            _writeQueue.Enqueue(new LogData(args, 0));
        }

        public void Warning(params object[] args)
        {
            _writeQueue.Enqueue(new LogData(args, 1));
        }

        public void Error(params object[] args)
        {
            _writeQueue.Enqueue(new LogData(args, 2));
        }

        public void Dir(object obj)
        {
            _writeQueue.Enqueue(new LogData(new object[1] { obj }, 3));
        }

        public void Debug(params object[] args) => Log(args);

        public void Info(params object[] args) => Log(args);

        private static string Args2String(object[] args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;

            return string.Join(" ", args.Select(arg => Value(arg)));
        }

        private static string Value(object value, int depth = 0)
        {
            if (value == null)
                return "null";

            if (value is string str)
                return $"\"{EscapeString(str)}\"";
            if (value is bool b)
                return b.ToString().ToLower();
            if (value.GetType().IsPrimitive || value is decimal)
                return value.ToString();
            if (value.GetType().IsEnum)
                return $"{value.GetType().Name}.{value}";

            if (value is IEnumerable enumerable)
                return Enumerable(enumerable, depth);

            return Object(value, depth);
        }

        private static string Enumerable(IEnumerable enumerable, int depth)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(item);
            }

            if (list.Count == 0)
                return $"Array[]";

            if (depth >= MAX_DEPTH)
                return $"Array({list.Count})[...]";

            var indent = GetIndent(depth);
            var nextIndent = GetIndent(depth + 1);

            var items = list.Take(MAX_ARRAY_ELEMENTS)
                            .Select(item => Value(item, depth + 1))
                            .ToList();

            if (list.Count > MAX_ARRAY_ELEMENTS)
            {
                items.Add($"... {list.Count - MAX_ARRAY_ELEMENTS} more ...");
            }

            return $"Array({list.Count}) [\n{nextIndent}{string.Join($",\n{nextIndent}", items)}\n{indent}]";
        }

        private static string Object(object obj, int depth)
        {
            if (obj == null)
                return "null";

            string typeName = obj.GetType().Name;

            if (depth >= MAX_DEPTH)
                return typeName + " {...}";

            var indent = GetIndent(depth);
            var nextIndent = GetIndent(depth + 1);

            var propStrings = new List<string>();

            var properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.CanRead)
                            .OrderBy(p => p.Name)
                            .ToList();

            var fields = obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                            .OrderBy(f => f.Name)
                            .ToList();

            for (int i = 0; i < properties.Count; i++)
            {
                if (propStrings.Count >= MAX_OBJECT_PROPERTIES) break;

                var prop = properties[i];
                try
                {
                    var value = prop.GetValue(obj);
                    propStrings.Add($"{prop.Name}: {Value(value, depth + 1)}");
                }
                catch (Exception ex)
                {
                    propStrings.Add($"{prop.Name}: [Error: {ex.Message}]");
                }
            }

            for (int i = 0; i < fields.Count; i++)
            {
                if (propStrings.Count >= MAX_OBJECT_PROPERTIES) break;

                var field = fields[i];
                try
                {
                    var value = field.GetValue(obj);
                    propStrings.Add($"{field.Name}: {Value(value, depth + 1)}");
                }
                catch (Exception ex)
                {
                    propStrings.Add($"{field.Name}: [Error: {ex.Message}]");
                }
            }

            if (properties.Count + fields.Count > MAX_OBJECT_PROPERTIES)
            {
                propStrings.Add($"... more properties/fields ...");
            }

            if (!propStrings.Any())
                return typeName + "{}";

            return $"{typeName} {{\n{nextIndent}{string.Join($",\n{nextIndent}", propStrings)}\n{indent}}}";
        }

        private static string FormatArgs2String(object[] args)
        {
            if (args == null || args.Length == 0)
                return string.Empty;

            return string.Join(" ", args.Select(arg => FormatValue(arg)));
        }

        private static string FormatValue(object value, int depth = 0)
        {
            if (value == null)
                return $"{COLOR_NULL}null{COLOR_RESET}";

            if (value is string str)
                return $"{COLOR_STRING}\"{EscapeString(str)}\"{COLOR_RESET}";
            if (value is bool b)
                return $"{COLOR_BOOLEAN}{b.ToString().ToLower()}{COLOR_RESET}";
            if (value.GetType().IsPrimitive || value is decimal)
                return $"{COLOR_NUMBER}{value}{COLOR_RESET}";
            if (value.GetType().IsEnum)
                return $"{COLOR_KEYWORD}{value.GetType().Name}{COLOR_RESET}.{COLOR_PROPERTY_NAME}{value}{COLOR_RESET}";

            if (value is IEnumerable enumerable)
                return FormatEnumerable(enumerable, depth);

            return FormatObject(value, depth);
        }

        private static string FormatEnumerable(IEnumerable enumerable, int depth)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(item);
            }

            if (list.Count == 0)
                return $"{COLOR_TYPE_NAME}Array{COLOR_RESET}{COLOR_BRACKET}[{COLOR_RESET}]";

            if (depth >= MAX_DEPTH)
                return $"{COLOR_TYPE_NAME}Array({list.Count}){COLOR_RESET}{COLOR_BRACKET}[...]{COLOR_RESET}";

            var indent = GetIndent(depth);
            var nextIndent = GetIndent(depth + 1);

            var items = list.Take(MAX_ARRAY_ELEMENTS)
                            .Select(item => FormatValue(item, depth + 1))
                            .ToList();

            if (list.Count > MAX_ARRAY_ELEMENTS)
            {
                items.Add($"{COLOR_COMMENT}... {list.Count - MAX_ARRAY_ELEMENTS} more ...{COLOR_RESET}");
            }

            return $"{COLOR_TYPE_NAME}Array({list.Count}){COLOR_RESET} {COLOR_BRACKET}[{COLOR_RESET}\n{nextIndent}{string.Join($",\n{nextIndent}", items)}\n{indent}{COLOR_BRACKET}]{COLOR_RESET}";
        }

        private static string FormatObject(object obj, int depth)
        {
            if (obj == null)
                return $"{COLOR_NULL}null{COLOR_RESET}";

            string typeName = obj.GetType().Name;

            if (depth >= MAX_DEPTH)
                return $"{COLOR_TYPE_NAME}{typeName}{COLOR_RESET} {COLOR_BRACKET}{{{COLOR_RESET}...{COLOR_BRACKET}}}{COLOR_RESET}";

            var indent = GetIndent(depth);
            var nextIndent = GetIndent(depth + 1);

            var propStrings = new List<string>();

            var properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.CanRead)
                            .OrderBy(p => p.Name)
                            .ToList();

            var fields = obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                            .OrderBy(f => f.Name)
                            .ToList();

            for (int i = 0; i < properties.Count; i++)
            {
                if (propStrings.Count >= MAX_OBJECT_PROPERTIES) break;

                var prop = properties[i];
                try
                {
                    var value = prop.GetValue(obj);
                    propStrings.Add($"{COLOR_PROPERTY_NAME}{prop.Name}{COLOR_RESET}: {FormatValue(value, depth + 1)}");
                }
                catch (Exception ex)
                {
                    propStrings.Add($"{COLOR_PROPERTY_NAME}{prop.Name}{COLOR_RESET}: {COLOR_ERROR}[Error: {ex.Message}]{COLOR_RESET}");
                }
            }

            for (int i = 0; i < fields.Count; i++)
            {
                if (propStrings.Count >= MAX_OBJECT_PROPERTIES) break;

                var field = fields[i];
                try
                {
                    var value = field.GetValue(obj);
                    propStrings.Add($"{COLOR_PROPERTY_NAME}{field.Name}{COLOR_RESET}: {FormatValue(value, depth + 1)}");
                }
                catch (Exception ex)
                {
                    propStrings.Add($"{COLOR_PROPERTY_NAME}{field.Name}{COLOR_RESET}: {COLOR_ERROR}[Error: {ex.Message}]{COLOR_RESET}");
                }
            }

            if (properties.Count + fields.Count > MAX_OBJECT_PROPERTIES)
            {
                propStrings.Add($"{COLOR_COMMENT}... more properties/fields ...{COLOR_RESET}");
            }

            if (!propStrings.Any())
                return $"{COLOR_TYPE_NAME}{typeName}{COLOR_RESET} {COLOR_BRACKET}{{{COLOR_RESET}{COLOR_BRACKET}}}{COLOR_RESET}";

            return $"{COLOR_TYPE_NAME}{typeName}{COLOR_RESET} {COLOR_BRACKET}{{{COLOR_RESET}\n{nextIndent}{string.Join($",\n{nextIndent}", propStrings)}\n{indent}{COLOR_BRACKET}}}{COLOR_RESET}";
        }

        private static readonly string[] _indentCache = new string[MAX_DEPTH + 2];

        private static string GetIndent(int depth)
        {
            if (depth < _indentCache.Length)
            {
                ref string cached = ref _indentCache[depth];
                if (cached == null)
                    cached = new string(' ', depth * 4);
                return cached;
            }
            return new string(' ', depth * 4);
        }

        private static string EscapeString(string str)
        {
            return str.Replace("\"", "\\\"")
                      .Replace("\n", "\\n")
                      .Replace("\r", "\\r")
                      .Replace("\t", "\\t");
        }
    }
}