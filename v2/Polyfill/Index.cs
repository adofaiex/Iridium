namespace System
{
    public readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        // 核心：编译器寻找的构造函数
        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "必须是非负数");
            _value = fromEnd ? ~value : value;
        }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        // 关键所在：编译器要求的成员
        public int GetOffset(int length)
        {
            int offset = _value;
            if (IsFromEnd)
            {
                offset += length + 1;
            }
            return offset;
        }

        public override bool Equals(object obj) => obj is Index other && Equals(other);
        public bool Equals(Index other) => _value == other._value;
        public override int GetHashCode() => _value;
        public static implicit operator Index(int value) => new Index(value);
        
        public override string ToString() => IsFromEnd ? "^" + (uint)Value : ((uint)Value).ToString();
    }
}