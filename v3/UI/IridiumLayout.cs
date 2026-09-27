using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using Iridium.Utilities;
using Color = UnityEngine.Color;
using FontStyle = UnityEngine.FontStyle;
using Graphics = System.Drawing.Graphics;
using Iridium.Polyfill;

namespace Iridium.UI;

// Declarative, JSX-like element tree. Build a tree of elements with the static
// factory methods (VBox, HBox, Text, Button, ...) and render it once per frame
// from OnGUI with Render(...). Interaction is expressed via callbacks
// (onClick / onChanged) instead of return values.
//
//   IridiumLayout.Render(
//       IridiumLayout.VBox(ContainerStyle.Padding,
//           IridiumLayout.HBox(
//               IridiumLayout.Button("Save", onClick: Save),
//               IridiumLayout.Text("Hello")
//           )
//       )
//   );
//
// The imperative engine is kept internally (IridiumLayout.Engine) for legacy
// call sites; Iris.Iml no longer bridges to it — the IML renderer now has its
// own shared layout engine + backends (see architectury/Iris.Iml/README.md).
public static class IridiumLayout
{
    public enum ArrowStyle
    {
        Right,
        Down,
        Left,
        Up
    }

    public enum ButtonStyle
    {
        Element,
        Primary
    }

    public enum ContainerDirection
    {
        Horizontal,
        Vertical
    }

    public enum ContainerStyle
    {
        None,
        Padding,
        Background
    }

    public enum IconStyle
    {
        Information,
        Success,
        Warning,
        Error,
        Stop
    }

    public enum TextStyle
    {
        Normal,
        Subtitle,
        Title,
        Secondary
    }

    private static readonly Trigger<int, ResolutionResources> ResolutionTrigger = new();

    private static ResolutionResources Resolution => ResolutionTrigger.Get(
        (int)(Main.Handler?.UIScale ?? 1048576),
        scaleTimes1M => new ResolutionResources(scaleTimes1M)
    );

    // ══════════════════════════════════════════════════════════════
    // Element tree (JSX-like declarative UI)
    // ══════════════════════════════════════════════════════════════

    public abstract class Element
    {
        internal abstract void Render();
    }

    private sealed class ContainerElement : Element
    {
        private readonly ContainerDirection _direction;
        private readonly ContainerStyle _style;
        private readonly Sizes? _sizes;
        private readonly GUILayoutOption[] _options;
        private readonly Element[] _children;

        public ContainerElement(
            ContainerDirection direction,
            ContainerStyle style,
            Sizes? sizes,
            params object[] content
        )
        {
            _direction = direction;
            _style = style;
            _sizes = sizes;
            var options = new List<GUILayoutOption>();
            var children = new List<Element>();
            foreach (var item in content)
            {
                if (item is Element child) children.Add(child);
                else if (item is GUILayoutOption option) options.Add(option);
            }
            _options = options.ToArray();
            _children = children.ToArray();
        }

        internal override void Render()
        {
            Engine.Begin(_direction, _style, _sizes, _options);
            try
            {
                foreach (var child in _children)
                    child.Render();
            }
            finally
            {
                Engine.End();
            }
        }
    }


    private sealed class AreaElement : Element
    {
        private readonly Rect _rect;
        private readonly Element[] _children;

        public AreaElement(Rect rect, params Element[] children)
        {
            _rect = rect;
            _children = children;
        }

        internal override void Render()
        {
            GUILayout.BeginArea(_rect);
            try
            {
                foreach (var child in _children)
                    child.Render();
            }
            finally
            {
                GUILayout.EndArea();
            }
        }
    }

    private sealed class ScrollViewElement : Element
    {
        private readonly Vector2 _scrollPosition;
        private readonly Action<Vector2>? _onScrolled;
        private readonly GUILayoutOption[] _options;
        private readonly Element[] _children;
        private readonly Action[] _callbacks;

        public ScrollViewElement(
            Vector2 scrollPosition,
            Action<Vector2>? onScrolled,
            params object[] content
        )
        {
            _scrollPosition = scrollPosition;
            _onScrolled = onScrolled;
            var options = new List<GUILayoutOption>();
            var children = new List<Element>();
            var callbacks = new List<Action>();
            foreach (var item in content)
            {
                if (item is Element child) children.Add(child);
                else if (item is GUILayoutOption option) options.Add(option);
                else if (item is Action callback) callbacks.Add(callback);
            }
            _options = options.ToArray();
            _children = children.ToArray();
            _callbacks = callbacks.ToArray();
        }

        internal override void Render()
        {
            var newPos = GUILayout.BeginScrollView(_scrollPosition, _options);
            try
            {
                foreach (var child in _children)
                    child.Render();
                foreach (var callback in _callbacks)
                    callback();
            }
            finally
            {
                GUILayout.EndScrollView();
            }
            _onScrolled?.Invoke(newPos);
        }
    }

    private sealed class TextElement : Element
    {
        private readonly string _text;
        private readonly TextStyle _style;
        private readonly GUILayoutOption[] _options;

        public TextElement(string text, TextStyle style, params object[] options)
        {
            _text = text;
            _style = style;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            Engine.Text(_text, _style, _options);
        }
    }

    private sealed class ButtonElement : Element
    {
        private readonly string _text;
        private readonly ButtonStyle _style;
        private readonly Action? _onClick;
        private readonly GUILayoutOption[] _options;

        public ButtonElement(string text, ButtonStyle style, Action? onClick, params object[] options)
        {
            _text = text;
            _style = style;
            _onClick = onClick;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            if (Engine.Button(_text, _style, _options) && _onClick != null)
                _onClick();
        }
    }

    private sealed class SwitchElement : Element
    {
        private readonly bool _on;
        private readonly Action<bool>? _onChanged;
        private readonly GUILayoutOption[] _options;

        public SwitchElement(bool on, Action<bool>? onChanged, params object[] options)
        {
            _on = on;
            _onChanged = onChanged;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            var result = Engine.Switch(_on, _options);
            if (result.HasValue && _onChanged != null)
                _onChanged(result.Value);
        }
    }

    private sealed class CheckboxElement : Element
    {
        private readonly bool _on;
        private readonly Action<bool>? _onChanged;
        private readonly GUILayoutOption[] _options;

        public CheckboxElement(bool on, Action<bool>? onChanged, params object[] options)
        {
            _on = on;
            _onChanged = onChanged;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            var result = Engine.Checkbox(_on, _options);
            if (result.HasValue && _onChanged != null)
                _onChanged(result.Value);
        }
    }

    private sealed class SelectorElement : Element
    {
        private readonly int _selected;
        private readonly IReadOnlyList<string> _selections;
        private readonly Action<int>? _onSelected;
        private readonly ButtonStyle _style;
        private readonly ButtonStyle _styleSelected;
        private readonly GUILayoutOption[] _options;

        public SelectorElement(
            int selected,
            IReadOnlyList<string> selections,
            Action<int>? onSelected,
            ButtonStyle style,
            ButtonStyle styleSelected,
            params object[] options
        )
        {
            _selected = selected;
            _selections = selections;
            _onSelected = onSelected;
            _style = style;
            _styleSelected = styleSelected;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            for (var i = 0; i < _selections.Count; i++)
            {
                if (Engine.Button(_selections[i], i == _selected ? _styleSelected : _style, _options) && _onSelected != null)
                    _onSelected(i);
            }
        }
    }

    private sealed class SelectorStringElement : Element
    {
        private readonly string _selected;
        private readonly IReadOnlyList<(string, string)> _selections;
        private readonly Action<string>? _onSelected;
        private readonly ButtonStyle _style;
        private readonly ButtonStyle _styleSelected;
        private readonly GUILayoutOption[] _options;

        public SelectorStringElement(
            string selected,
            IReadOnlyList<(string, string)> selections,
            Action<string>? onSelected,
            ButtonStyle style,
            ButtonStyle styleSelected,
            params object[] options
        )
        {
            _selected = selected;
            _selections = selections;
            _onSelected = onSelected;
            _style = style;
            _styleSelected = styleSelected;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            foreach (var (key, name) in _selections)
            {
                if (Engine.Button(name, key == _selected ? _styleSelected : _style, _options) && _onSelected != null)
                    _onSelected(key);
            }
        }
    }

    private sealed class TextFieldElement : Element
    {
        private readonly string _content;
        private readonly int? _maxLength;
        private readonly Action<string>? _onChanged;
        private readonly GUILayoutOption[] _options;

        public TextFieldElement(string content, int? maxLength, Action<string>? onChanged, params object[] options)
        {
            _content = content;
            _maxLength = maxLength;
            _onChanged = onChanged;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            var result = Engine.TextField(_content, _maxLength, _options);
            if (result != null && _onChanged != null)
                _onChanged(result);
        }
    }

    private sealed class ClassFieldElement<T> : Element where T : class
    {
        private readonly T _content;
        private readonly IClassFormat<T> _format;
        private readonly Action<T>? _onChanged;
        private readonly GUILayoutOption[] _options;

        public ClassFieldElement(T content, IClassFormat<T> format, Action<T>? onChanged, params object[] options)
        {
            _content = content;
            _format = format;
            _onChanged = onChanged;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            var newContent = Engine.TextField(_format.Format(_content), null, _options);
            if (newContent is null) return;
            var newValue = _format.Parse(newContent);
            if (newValue is not null && _onChanged != null)
                _onChanged(newValue);
        }
    }

    private sealed class StructFieldElement<T> : Element where T : struct
    {
        private readonly T _content;
        private readonly IStructFormat<T> _format;
        private readonly Action<T>? _onChanged;
        private readonly GUILayoutOption[] _options;

        public StructFieldElement(T content, IStructFormat<T> format, Action<T>? onChanged, params object[] options)
        {
            _content = content;
            _format = format;
            _onChanged = onChanged;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            var newContent = Engine.TextField(_format.Format(_content), null, _options);
            if (newContent is null) return;
            var newValue = _format.Parse(newContent);
            if (newValue is not null && _onChanged != null)
                _onChanged(newValue.Value);
        }
    }

    private sealed class IconElement : Element
    {
        private readonly IconStyle _style;
        private readonly Action? _onClick;
        private readonly GUILayoutOption[] _options;

        public IconElement(IconStyle style, Action? onClick, params object[] options)
        {
            _style = style;
            _onClick = onClick;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            if (Engine.Icon(_style, _options) && _onClick != null)
                _onClick();
        }
    }

    private sealed class ArrowButtonElement : Element
    {
        private readonly ArrowStyle _style;
        private readonly Action? _onClick;
        private readonly GUILayoutOption[] _options;

        public ArrowButtonElement(ArrowStyle style, Action? onClick, params object[] options)
        {
            _style = style;
            _onClick = onClick;
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            if (Engine.ArrowButton(_style, _options) && _onClick != null)
                _onClick();
        }
    }

    private sealed class SeparatorElement : Element
    {
        private readonly GUILayoutOption[] _options;

        public SeparatorElement(params object[] options)
        {
            _options = options.OfType<GUILayoutOption>().ToArray();
        }

        internal override void Render()
        {
            Engine.Separator(_options);
        }
    }

    private sealed class SpaceElement : Element
    {
        private readonly double _size;

        public SpaceElement(double size)
        {
            _size = size;
        }

        internal override void Render()
        {
            Engine.Space(_size);
        }
    }

    private sealed class FillElement : Element
    {
        internal override void Render()
        {
            Engine.Fill();
        }
    }

    private sealed class AlignElement : Element
    {
        private readonly double _ratio;
        private readonly double _offset;
        private readonly Element[] _children;

        public AlignElement(double ratio, double offset, params Element[] children)
        {
            _ratio = ratio;
            _offset = offset;
            _children = children;
        }

        internal override void Render()
        {
            Engine.PushAlign(_ratio, _offset);
            try
            {
                foreach (var child in _children)
                    child.Render();
            }
            finally
            {
                Engine.PopAlign();
            }
        }
    }

    private sealed class SizesElement : Element
    {
        private readonly Sizes _sizes;
        private readonly Element[] _children;

        public SizesElement(Sizes sizes, params Element[] children)
        {
            _sizes = sizes;
            _children = children;
        }

        internal override void Render()
        {
            Engine.PushSizes(_sizes);
            try
            {
                foreach (var child in _children)
                    child.Render();
            }
            finally
            {
                Engine.PopSizes();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Factory methods
    // ══════════════════════════════════════════════════════════════

    public static Element VBox(
        ContainerStyle style = ContainerStyle.None,
        Sizes? sizes = null,
        params object[] content
    )
    {
        return new ContainerElement(ContainerDirection.Vertical, style, sizes, content);
    }

    public static Element HBox(
        ContainerStyle style = ContainerStyle.None,
        Sizes? sizes = null,
        params object[] content
    )
    {
        return new ContainerElement(ContainerDirection.Horizontal, style, sizes, content);
    }

    public static Element Text(
        string text,
        TextStyle style = TextStyle.Normal,
        params object[] options
    )
    {
        return new TextElement(text, style, options);
    }

    public static Element Button(
        string text,
        ButtonStyle style = ButtonStyle.Primary,
        Action? onClick = null,
        params object[] options
    )
    {
        return new ButtonElement(text, style, onClick, options);
    }

    public static Element Switch(
        bool on,
        Action<bool>? onChanged = null,
        params object[] options
    )
    {
        return new SwitchElement(on, onChanged, options);
    }

    public static Element Checkbox(
        bool on,
        Action<bool>? onChanged = null,
        params object[] options
    )
    {
        return new CheckboxElement(on, onChanged, options);
    }

    public static Element Selector(
        int selected,
        IReadOnlyList<string> selections,
        Action<int>? onSelected = null,
        ButtonStyle style = ButtonStyle.Element,
        ButtonStyle styleSelected = ButtonStyle.Primary,
        params object[] options
    )
    {
        return new SelectorElement(selected, selections, onSelected, style, styleSelected, options);
    }

    public static Element Selector(
        string selected,
        IReadOnlyList<(string, string)> selections,
        Action<string>? onSelected = null,
        ButtonStyle style = ButtonStyle.Element,
        ButtonStyle styleSelected = ButtonStyle.Primary,
        params object[] options
    )
    {
        return new SelectorStringElement(selected, selections, onSelected, style, styleSelected, options);
    }

    public static Element TextField(
        string content,
        Action<string>? onChanged = null,
        int? maxLength = null,
        params object[] options
    )
    {
        return new TextFieldElement(content, maxLength, onChanged, options);
    }

    public static Element ClassField<T>(
        T content,
        IClassFormat<T> format,
        Action<T>? onChanged = null,
        params object[] options
    ) where T : class
    {
        return new ClassFieldElement<T>(content, format, onChanged, options);
    }

    public static Element StructField<T>(
        T content,
        IStructFormat<T> format,
        Action<T>? onChanged = null,
        params object[] options
    ) where T : struct
    {
        return new StructFieldElement<T>(content, format, onChanged, options);
    }

    public static Element Icon(
        IconStyle style = IconStyle.Information,
        Action? onClick = null,
        params object[] options
    )
    {
        return new IconElement(style, onClick, options);
    }

    public static Element ArrowButton(
        ArrowStyle style,
        Action? onClick = null,
        params object[] options
    )
    {
        return new ArrowButtonElement(style, onClick, options);
    }

    public static Element Separator(params object[] options)
    {
        return new SeparatorElement(options);
    }

    public static Element Space(double size)
    {
        return new SpaceElement(size);
    }

    public static Element Fill()
    {
        return new FillElement();
    }

    public static Element Align(double ratio, double offset, params Element[] children)
    {
        return new AlignElement(ratio, offset, children);
    }

    public static Element WithSizes(Sizes sizes, params Element[] children)
    {
        return new SizesElement(sizes, children);
    }

    /// <summary>
    /// Render an element tree. Call this from OnGUI each frame. Containers are
    /// automatically Begin/End paired and the stack is unwound on exceptions.
    /// </summary>
    public static Element Area(Rect rect, params Element[] children)
    {
        return new AreaElement(rect, children);
    }

    public static Element ScrollView(
        Vector2 scrollPosition,
        Action<Vector2>? onScrolled = null,
        params object[] content
    )
    {
        return new ScrollViewElement(scrollPosition, onScrolled, content);
    }

    public static void Render(params Element[] roots)
    {
        var initialDepth = Engine.ContainerStack.Count;
        try
        {
            foreach (var root in roots)
                root.Render();
        }
        finally
        {
            while (Engine.ContainerStack.Count > initialDepth)
            {
                try { Engine.End(); }
                catch { break; }
            }
        }
    }

    // Expose GUIStyles for external use (e.g. IrisRenderer style registry)
    public static GUIStyle PrimaryButton => Resolution.PrimaryButton;
    public static GUIStyle ElementButton => Resolution.ElementButton;
    public static GUIStyle NormalText => Resolution.NormalText;
    public static GUIStyle SubtitleText => Resolution.SubtitleText;
    public static GUIStyle TitleText => Resolution.TitleText;
    public static GUIStyle SecondaryText => Resolution.SecondaryText;
    public static GUIStyle PaddingContainer => Resolution.PaddingContainer;
    public static GUIStyle Background0Container => Resolution.Background0Container;
    public static GUIStyle Background1Container => Resolution.Background1Container;
    public static GUIStyle HorizontalSeparator => Resolution.HorizontalSeparator;
    public static GUIStyle SwitchOn => Resolution.SwitchOn;
    public static GUIStyle SwitchOff => Resolution.SwitchOff;
    public static GUIStyle CheckboxOn => Resolution.CheckboxOn;
    public static GUIStyle CheckboxOff => Resolution.CheckboxOff;
    public static GUIStyle TextFieldStyle => Resolution.TextField;

    public static void EnsureTexturesAlive()
    {
        // Iris.Iml 现在自带独立渲染管线（共享布局引擎 + 后端自绘），
        // 不再注入 ExternalArrowButtonRenderer —— IML 渲染与 IridiumLayout 彻底解耦。
        if (Resolution.Textures.Any(x => x == null))
        {
            var oldResources = ResolutionTrigger.ResetWithOld();
            if (oldResources != null)
                oldResources.DestroyTextures();
        }
    }

    /// <summary>
    /// 箭头按钮合成纹理：圆角方块背景 + 边框 + DrawArrow 描边三角，
    /// 单张纹理一次绘制。dir: 0=Right 1=Down 2=Left 3=Up（Iris.Iml ArrowDir）。
    /// </summary>
    public static Texture2D RenderArrowButtonTexture(int size, int dir, Color fill, Color border, int borderWidth, int radius, Color strokeColor)
    {
        return Resolution.RenderArrowButtonOnly(size, dir, fill, border, borderWidth, radius, strokeColor);
    }

    public static GUILayoutOption WidthMin => GUILayout.ExpandWidth(false);

    public static GUILayoutOption WidthMax => GUILayout.ExpandWidth(true);

    public static GUILayoutOption Width(double width)
    {
        return GUILayout.Width((float)Resolution.Scaled(width));
    }

    public static GUILayoutOption Height(double height)
    {
        return GUILayout.Height((float)Resolution.Scaled(height));
    }

    public static GUILayoutOption MinWidth(double width)
    {
        return GUILayout.MinWidth((float)Resolution.Scaled(width));
    }

    public static GUILayoutOption MaxWidth(double width)
    {
        return GUILayout.MaxWidth((float)Resolution.Scaled(width));
    }

    public static GUILayoutOption MinHeight(double height)
    {
        return GUILayout.MinHeight((float)Resolution.Scaled(height));
    }

    public static GUILayoutOption MaxHeight(double height)
    {
        return GUILayout.MaxHeight((float)Resolution.Scaled(height));
    }

    public static IStructFormat<double> DoubleFormat(
        int? precision = null,
        double min = double.NegativeInfinity,
        double max = double.PositiveInfinity
    )
    {
        return new DoubleFormatImpl(precision, min, max);
    }

    public static IStructFormat<int> IntFormat(
        int min = int.MinValue,
        int max = int.MaxValue
    )
    {
        return new IntFormatImpl(min, max);
    }

    public interface IClassFormat<T> where T : class
    {
        string Format(T value);

        T? Parse(string text);
    }

    public interface IStructFormat<T> where T : struct
    {
        string Format(T value);

        T? Parse(string text);
    }

    private sealed class DoubleFormatImpl(
        int? precision,
        double lower,
        double upper
    ) : IStructFormat<double>
    {
        public string Format(double value)
        {
            if (precision is not null) return value.ToString($"F{precision}");
            var text = $"{value:R}";
            if (text.Contains('.') || !Polyfill.Double.IsFinite(value)) return text;
            var exponentIndex = text.IndexOfAny(['e', 'E']);
            if (exponentIndex < 0) exponentIndex = text.Length;
            return text.Insert(exponentIndex, ".0");
        }

        public double? Parse(string text)
        {
            if (text.IsNullOrEmpty()) return 0;
            if (!double.TryParse(text, out var result)) return null;
            return Polyfill.MathI.Clamp(result, lower, upper);
        }
    }

    private sealed class IntFormatImpl(
        int lower,
        int upper
    ) : IStructFormat<int>
    {
        public string Format(int value)
        {
            return value.ToString();
        }

        public int? Parse(string text)
        {
            if (text.IsNullOrEmpty()) return 0;
            if (!int.TryParse(text, out var result)) return null;
            return Polyfill.MathI.Clamp(result, lower, upper);
        }
    }

    public class Sizes
    {
        private readonly List<double> _recorded = [];

        private int _readIndex;

        private int _writeIndex;

        public int MaxMargin { get; private set; }

        public int NextMaxMargin { get; set; }

        public double? Max => _recorded.Count == 0 ? null : _recorded.Max();

        public double? Next => _readIndex < _recorded.Count ? _recorded[_readIndex++] : null;

        public void Begin()
        {
            _readIndex = 0;
            _writeIndex = 0;
            MaxMargin = NextMaxMargin;
            NextMaxMargin = 0;
        }

        public void Put(double value)
        {
            if (_writeIndex < _recorded.Count) _recorded[_writeIndex] = value;
            else _recorded.Add(value);
            ++_writeIndex;
        }
    }

    public class SizesGroup
    {
        private SizesGroup()
        {
        }

        private readonly List<Sizes> _sizesPool = [];

        private readonly List<SizesGroup> _groupsPool = [];

        private int _sizesIndex;

        private int _groupsIndex;

        public Sizes Sizes
        {
            get
            {
                while (_sizesIndex >= _sizesPool.Count) _sizesPool.Add(new Sizes());
                return _sizesPool[_sizesIndex++];
            }
        }

        public SizesGroup Group
        {
            get
            {
                while (_groupsIndex >= _groupsPool.Count) _groupsPool.Add(new SizesGroup());
                var group = _groupsPool[_groupsIndex++];
                group.Begin();
                return group;
            }
        }

        public void Begin()
        {
            _sizesIndex = 0;
            _groupsIndex = 0;
        }

        public static implicit operator Sizes(SizesGroup group)
        {
            return group.Sizes;
        }

        public class Holder
        {
            private SizesGroup Group { get; } = new();

            public SizesGroup Begin()
            {
                Group.Begin();
                return Group;
            }
        }
    }
    internal static class Engine
    {
        private sealed class Frame
        {
            public ContainerDirection Direction { get; set; }

            public int ElementCount { get; set; }

            public bool IsBackground { get; set; }

            public bool ApplyPreMarginHorizontal { get; set; }

            public bool ApplyPreMarginVertical { get; set; }
        }

        internal static List<ContainerDirection> ContainerStack { get; } = [ContainerDirection.Vertical];

        private static readonly List<Frame> Frames = [new Frame { Direction = ContainerDirection.Vertical }];

        private static readonly List<(double?, Sizes)?> SizesScopes = [null];

        private static readonly List<(double, double)?> AlignmentScopes = [null];

        private static double TrailingMargin;

        private static bool AlternateBackground;

        private static GUILayoutOption[] BuildOptions(object[] options)
        {
            return options.OfType<GUILayoutOption>().Append(GUILayout.ExpandHeight(false)).ToArray();
        }

        private static GUIStyle OffsetStyle(GUIStyle source)
        {
            var frame = Frames[^1];
            var count = frame.ElementCount++;
            var isHorizontal = frame.Direction == ContainerDirection.Horizontal;
            var prependAllowed = count > 0 || (isHorizontal
                ? frame.ApplyPreMarginHorizontal
                : frame.ApplyPreMarginVertical);

            var margin = (int)((count > 0 ? Resolution.Margin : 0) + TrailingMargin);

            var shift = !prependAllowed
                ? new RectOffset(0, 0, 0, 0)
                : isHorizontal
                    ? new RectOffset(margin, 0, 0, 0)
                    : new RectOffset(0, 0, margin, 0);

            var adjusted = new GUIStyle(source);
            adjusted.margin = new RectOffset(
                adjusted.margin.left + shift.left,
                adjusted.margin.right + shift.right,
                adjusted.margin.top + shift.top,
                adjusted.margin.bottom + shift.bottom
            );

            if (!prependAllowed)
            {
                if (isHorizontal) adjusted.margin.left = 0;
                else adjusted.margin.top = 0;
            }

            var sizesScope = SizesScopes[^1];
            var alignmentScope = AlignmentScopes[^1];

            if (sizesScope is not null)
            {
                var (maxSize, sizes) = sizesScope.Value;
                var leadingEdge = Math.Max(
                    0,
                    isHorizontal ? adjusted.margin.top : adjusted.margin.left
                );
                sizes.NextMaxMargin = Math.Max(0, leadingEdge);

                if (alignmentScope is not null)
                {
                    var size = sizes.Next;
                    var (ratio, offset) = alignmentScope.Value;
                    if (maxSize is not null && size is not null)
                    {
                        var leftover = Math.Max(0, maxSize.Value - size.Value);
                        var push = (int)Math.Floor(Math.Max(0, leftover * ratio + offset + sizes.MaxMargin - leadingEdge));
                        if (isHorizontal) adjusted.margin.top += push;
                        else adjusted.margin.left += push;
                    }
                }
            }

            TrailingMargin = isHorizontal ? adjusted.margin.right : adjusted.margin.bottom;

            return adjusted;
        }

        internal static void AddMargin(double size)
        {
            TrailingMargin += Resolution.Scaled(size);
        }

        internal static void Space(double size)
        {
            GUILayout.Space((float)Resolution.Scaled(size));
        }

        internal static void Fill()
        {
            if (ContainerStack[^1] != ContainerDirection.Horizontal)
                throw new InvalidOperationException("Fill can only be used in Horizontal containers");
            GUILayout.FlexibleSpace();
        }

        internal static void PushSizes(Sizes? sizes = null)
        {
            if (sizes is null)
            {
                SizesScopes.Add(null);
                return;
            }

            sizes.Begin();
            SizesScopes.Add((sizes.Max, sizes));
        }

        internal static void PopSizes()
        {
            SizesScopes.RemoveAt(SizesScopes.Count - 1);
        }

        internal static void UpdateMaxSize()
        {
            if (Event.current.type != EventType.Repaint) return;
            var sizesScope = SizesScopes[^1];
            if (sizesScope is null) return;
            var (_, sizes) = sizesScope.Value;
            var rect = GUILayoutUtility.GetLastRect();
            var isHorizontal = ContainerStack[^1] == ContainerDirection.Horizontal;
            sizes.Put(Math.Max(0, isHorizontal ? rect.height : rect.width));
        }

        internal static void Begin(
            ContainerDirection direction,
            ContainerStyle style = ContainerStyle.None,
            Sizes? sizes = null,
            params object[] options
        )
        {
            if (style == ContainerStyle.Background) AlternateBackground = !AlternateBackground;

            var guiStyle = OffsetStyle(style switch
            {
                ContainerStyle.None => Resolution.Container,
                ContainerStyle.Padding => Resolution.PaddingContainer,
                ContainerStyle.Background => AlternateBackground
                    ? Resolution.Background1Container
                    : Resolution.Background0Container,
                _ => Resolution.Container
            });

            if (direction == ContainerDirection.Horizontal) GUILayout.BeginHorizontal(guiStyle, BuildOptions(options));
            else GUILayout.BeginVertical(guiStyle, BuildOptions(options));

            TrailingMargin = 0;
            ContainerStack.Add(direction);
            Frames.Add(new Frame
            {
                Direction = direction,
                IsBackground = style == ContainerStyle.Background,
                ApplyPreMarginHorizontal = style == ContainerStyle.None && Frames[^1].ApplyPreMarginHorizontal,
                ApplyPreMarginVertical = style == ContainerStyle.None && Frames[^1].ApplyPreMarginVertical
            });
            PushSizes(sizes);
        }

        internal static void End()
        {
            var frame = Frames[^1];
            var direction = frame.Direction;
            if (frame.IsBackground) AlternateBackground = !AlternateBackground;

            TrailingMargin = 0;

            if (direction == ContainerDirection.Horizontal)
            {
                GUILayout.EndHorizontal();
                UpdateMaxSize();
                ContainerStack.RemoveAt(ContainerStack.Count - 1);
                Frames.RemoveAt(Frames.Count - 1);
                SizesScopes.RemoveAt(SizesScopes.Count - 1);
                Frames[^1].ApplyPreMarginHorizontal = true;
            }
            else
            {
                GUILayout.EndVertical();
                UpdateMaxSize();
                ContainerStack.RemoveAt(ContainerStack.Count - 1);
                Frames.RemoveAt(Frames.Count - 1);
                SizesScopes.RemoveAt(SizesScopes.Count - 1);
                Frames[^1].ApplyPreMarginVertical = true;
            }
        }

        internal static void PushAlign(double ratio = 0, double offset = 0)
        {
            AlignmentScopes.Add((ratio, offset));
        }

        internal static void PushNoAlign()
        {
            AlignmentScopes.Add(null);
        }

        internal static void PopAlign()
        {
            AlignmentScopes.RemoveAt(AlignmentScopes.Count - 1);
        }

        internal static void Separator(params object[] options)
        {
            var isHorizontal = ContainerStack[^1] == ContainerDirection.Horizontal;

            GUILayout.Label(
                GUIContent.none,
                OffsetStyle(isHorizontal ? Resolution.VerticalSeparator : Resolution.HorizontalSeparator),
                BuildOptions(options)
            );
            UpdateMaxSize();
        }

        internal static bool Text(
            string text,
            TextStyle style = TextStyle.Normal,
            params object[] options
        )
        {
            var guiStyle = OffsetStyle(style switch
            {
                TextStyle.Normal => Resolution.NormalText,
                TextStyle.Subtitle => Resolution.SubtitleText,
                TextStyle.Title => Resolution.TitleText,
                TextStyle.Secondary => Resolution.SecondaryText,
                _ => Resolution.NormalText
            });

            var result = GUILayout.Button(text, guiStyle, BuildOptions(options));
            UpdateMaxSize();
            return result;
        }

        /// <summary>
        /// 超链接文字：下划线 + 主题粉，点击返回 true（由调用方负责 OpenURL）。
        /// </summary>
        internal static bool Link(string text, params object[] options)
        {
            var guiStyle = OffsetStyle(Resolution.LinkText);
            var result = GUILayout.Button(new GUIContent($"<u>{text}</u>"), guiStyle, BuildOptions(options));
            UpdateMaxSize();
            return result;
        }

        internal static bool Button(
            string text,
            ButtonStyle style = ButtonStyle.Primary,
            params object[] options
        )
        {
            var guiStyle = OffsetStyle(style switch
            {
                ButtonStyle.Element => Resolution.ElementButton,
                ButtonStyle.Primary => Resolution.PrimaryButton,
                _ => Resolution.ElementButton
            });

            var result = GUILayout.Button(text, guiStyle, BuildOptions(options));
            UpdateMaxSize();
            return result;
        }

        internal static bool? Checkbox(bool on, params object[] options)
        {
            bool? result = null;

            if (
                GUILayout.Button(
                    GUIContent.none,
                    OffsetStyle(on ? Resolution.CheckboxOn : Resolution.CheckboxOff),
                    BuildOptions(options)
                )
            ) result = !on;
            UpdateMaxSize();
            return result;
        }

        internal static bool? Checkbox(ref bool on, params object[] options)
        {
            var result = Checkbox(on, options);
            if (result is not null) on = result.Value;
            return result;
        }

        internal static bool ArrowButton(ArrowStyle style, params object[] options)
        {
            var guiStyle = OffsetStyle(style switch
            {
                ArrowStyle.Right => Resolution.ArrowButtonRight,
                ArrowStyle.Down => Resolution.ArrowButtonDown,
                ArrowStyle.Left => Resolution.ArrowButtonLeft,
                ArrowStyle.Up => Resolution.ArrowButtonUp,
                _ => Resolution.ArrowButtonRight
            });

            var result = GUILayout.Button(GUIContent.none, guiStyle, BuildOptions(options));
            UpdateMaxSize();
            return result;
        }

        internal static bool? Switch(bool on, params object[] options)
        {
            bool? result = null;

            if (
                GUILayout.Button(
                    GUIContent.none,
                    OffsetStyle(on ? Resolution.SwitchOn : Resolution.SwitchOff),
                    BuildOptions(options)
                )
            ) result = !on;
            UpdateMaxSize();

            return result;
        }

        internal static bool? Switch(ref bool on, params object[] options)
        {
            var result = Switch(on, options);
            if (result is not null)
            {
                on = result.Value;
                GUI.changed = true;
            }
            return result;
        }

        internal static string? TextField(
            string content,
            int? maxLength = null,
            params object[] options
        )
        {
            string? result = null;
            string newContent;

            if (
                (newContent = GUILayout.TextField(
                    content,
                    maxLength ?? -1,
                    OffsetStyle(Resolution.TextField),
                    BuildOptions(options)
                )) != content
            ) result = newContent;
            UpdateMaxSize();

            return result;
        }

        internal static string? TextField(
            ref string? content,
            int? maxLength = null,
            params object[] options
        )
        {
            var result = TextField(content ?? string.Empty, maxLength, options);
            if (result is not null) content = result;
            return result;
        }

        internal static T? ClassField<T>(
            T content,
            IClassFormat<T> format,
            params object[] options
        ) where T : class
        {
            var oldContent = format.Format(content);
            var newContent = TextField(oldContent, null, options);
            if (newContent is null) return null;
            var newValue = format.Parse(newContent);
            return newValue;
        }

        internal static T? StructField<T>(
            T content,
            IStructFormat<T> format,
            params object[] options
        ) where T : struct
        {
            var oldContent = format.Format(content);
            var newContent = TextField(
                oldContent,
                null,
                options
            );
            if (newContent is null) return null;
            var newValue = format.Parse(newContent);
            return newValue;
        }

        internal static bool Icon(
            IconStyle style = IconStyle.Information,
            params object[] options
        )
        {
            var guiStyle = OffsetStyle(style switch
            {
                IconStyle.Information => Resolution.IconInformation,
                IconStyle.Success => Resolution.IconSuccess,
                IconStyle.Warning => Resolution.IconWarning,
                IconStyle.Error => Resolution.IconError,
                IconStyle.Stop => Resolution.IconStop,
                _ => Resolution.IconInformation
            });

            var result = GUILayout.Button(GUIContent.none, guiStyle, BuildOptions(options));
            UpdateMaxSize();
            return result;
        }
    }
    private class ResolutionResources
    {
        private const double BaseTextSize = 12;

        private const double SubtitleTextSize = 18;

        private const double TitleTextSize = 24;

        private const double SecondaryTextSize = BaseTextSize;

        private const double BaseMargin = 8;

        private const double SubtitleAdditionalMargin = 4;

        private const double TitleAdditionalMargin = 8;

        private const double ContainerPadding = 8;

        private const double BackgroundRadius = 16;

        private const double ButtonRadius = 8;

        private const double SquareIconSize = 20;

        private const double SquareIconRadius = 4;

        private const double SquareIconBorder = 1;

        private const double SwitchWidth = 36;

        private const double SwitchHeight = 20;

        private const double SwitchButtonRadius = 7;

        private const double TextFieldRadius = 8;

        private const double TextFieldBorder = 1;

        private const double IconSize = 20;

        private const double IconBorder = 2;

        private static Dictionary<string, Color>? _loadedColors;

        private static void LoadColors()
        {
            _loadedColors = new Dictionary<string, Color>();
            try
            {
                var modPath = Main.Handler?.ModPath;
                if (modPath == null) return;
                var path = System.IO.Path.Combine(modPath, "Resources", "ui", "Colors.iml");
                if (!System.IO.File.Exists(path)) return;
                var text = System.IO.File.ReadAllText(path);
                var styleRegex = new System.Text.RegularExpressions.Regex(
                    @"<Style\s+name=""([^""]*)""[^>]*>(.*?)</Style>",
                    System.Text.RegularExpressions.RegexOptions.Singleline
                );
                var setterRegex = new System.Text.RegularExpressions.Regex(
                    @"<Setter\s+property=""([^""]*)""\s+value=""#?([0-9A-Fa-f]{6,8})""",
                    System.Text.RegularExpressions.RegexOptions.Singleline
                );
                foreach (System.Text.RegularExpressions.Match styleMatch in styleRegex.Matches(text))
                {
                    var styleName = styleMatch.Groups[1].Value;
                    var block = styleMatch.Groups[2].Value;
                    foreach (System.Text.RegularExpressions.Match setterMatch in setterRegex.Matches(block))
                    {
                        var prop = setterMatch.Groups[1].Value;
                        var hex = setterMatch.Groups[2].Value;
                        var key = $"{styleName}.{prop}";
                        var val = Convert.ToInt64(hex, 16);
                        _loadedColors[key] = hex.Length == 8 ? ARGB(val) : RGB(val);
                    }
                }
                Main.Logger?.Log($"Loaded {_loadedColors.Count} colors from Colors.iml");
            }
            catch (Exception ex)
            {
                Main.Logger?.Log($"Failed to load Colors.iml: {ex.Message}");
            }
        }

        private static Color LookupColor(string key, Color fallback)
        {
            if (_loadedColors == null) LoadColors();
            return _loadedColors.TryGetValue(key, out var c) ? c : fallback;
        }

        private static Color ScaleChannel(Color c, double factor)
        {
            return new Color(
                (float)Math.Max(0, Math.Min(1, c.r * factor)),
                (float)Math.Max(0, Math.Min(1, c.g * factor)),
                (float)Math.Max(0, Math.Min(1, c.b * factor)),
                c.a
            );
        }

        private static ColorGroup LoadShadeGroup(string style, string prop, long fallback, double hoverScale = 1.0, double activeScale = 1.0)
        {
            var normal = LookupColor($"{style}.{prop}", RGB(fallback));
            return new ColorGroup(normal, ScaleChannel(normal, hoverScale), ScaleChannel(normal, activeScale));
        }

        private static ColorGroup LoadSolidGroup(string style, string prop, long fallback)
        {
            var color = LookupColor($"{style}.{prop}", RGB(fallback));
            return new ColorGroup(color, color, color, color);
        }

        private static readonly ColorGroup Background0Colors = LoadSolidGroup("bg-default", "background", 0x151617);

        private static readonly ColorGroup Background1Colors = LoadSolidGroup("bg-alt", "background", 0x0D0E0F);

        private static readonly ColorGroup SeparatorColors = new(LookupColor("bg-separator.background", ARGB(0x20FFFFFF)));

        private static readonly ColorGroup PrimaryColors = LoadShadeGroup("primary", "background", 0xD973A5, 0.89, 0.72);

        // 超链接文字：常态主题粉、悬停提亮、按下加深
        private static readonly ColorGroup LinkTextColors = LoadShadeGroup("primary", "background", 0xD973A5, 1.18, 0.89);

        private static readonly ColorGroup ElementColors = LoadShadeGroup("element", "background", 0x313338, 1.12, 1.0);

        private static readonly ColorGroup ElementBorderColors = LoadSolidGroup("bg-element-border", "background", 0x494F5C);

        private static readonly ColorGroup NormalTextColors = LoadSolidGroup("text-normal", "color", 0xE9ECEF);

        private static readonly ColorGroup SubtitleTextColors = LoadSolidGroup("text-subtitle", "color", 0xF1F3F5);

        private static readonly ColorGroup TitleTextColors = LoadSolidGroup("text-title", "color", 0xF8F9FA);

        private static readonly ColorGroup SecondaryTextColors = LoadSolidGroup("text-secondary", "color", 0x7D7E7F);

        private static readonly ColorGroup CheckboxOffColors = ElementColors;

        private static readonly ColorGroup CheckboxOffBorderColors = ElementBorderColors;

        private static readonly ColorGroup CheckboxOnColors = new(PrimaryColors.Normal);

        private static readonly ColorGroup CheckboxOnBorderColors = PrimaryColors;

        private static readonly ColorGroup CheckboxCheckmarkColors = TitleTextColors;

        private static readonly ColorGroup ArrowButtonColors = ElementColors;

        private static readonly ColorGroup ArrowButtonBorderColors = ElementBorderColors;

        private static readonly ColorGroup ArrowButtonArrowColors = TitleTextColors;

        private static readonly ColorGroup SwitchOffColors = ElementColors;

        private static readonly ColorGroup SwitchOnColors = PrimaryColors;

        private static readonly ColorGroup SwitchButtonColors = TitleTextColors;

        private static readonly ColorGroup TextFieldColors = LoadSolidGroup("bg-default", "background", 0x151719);

        private static readonly ColorGroup TextFieldBorderColors = new(
            LookupColor("bg-textfield-border.background", RGB(0x222326)),
            LookupColor("bg-textfield-border.background", RGB(0x222326)),
            LookupColor("primary.background", RGB(0xD973A5)),
            LookupColor("primary.background", RGB(0xD973A5))
        );

        private static readonly ColorGroup IconInformationColors = ElementBorderColors;

        private static readonly ColorGroup IconInformationBorderColors = new(ElementColors.Hovered);

        private static readonly ColorGroup IconSuccessColors = new(RGB(0x039855));

        private static readonly ColorGroup IconSuccessBorderColors = new(RGB(0x027948));

        private static readonly ColorGroup IconWarningColors = new(RGB(0xF79009));

        private static readonly ColorGroup IconWarningBorderColors = new(RGB(0xDC6803));

        private static readonly ColorGroup IconErrorColors = new(RGB(0xD92020));

        private static readonly ColorGroup IconErrorBorderColors = new(RGB(0xB41818));

        private static readonly ColorGroup IconStopColors = new(RGB(0xD92020));

        private static readonly ColorGroup IconStopBorderColors = new(RGB(0xB41818));

        private static readonly ColorGroup IconStrokeColors = TitleTextColors;

        public ResolutionResources(int scaleTimes1M)
        {
            Scale = scaleTimes1M / 1048576.0;

            Main.Logger?.Log($"loading resources for scale {Scale}");

            Margin = Scaled(BaseMargin);

            Base = BuildBaseStyle();

            Container = new GUIStyle(Base)
            {
                name = "Iridium Container"
            };

            var scaledPadding = ScaledInt(ContainerPadding);

            PaddingContainer = new GUIStyle(Base)
            {
                name = "Iridium Padding Container",
                padding = new RectOffset(scaledPadding, scaledPadding, scaledPadding, scaledPadding)
            };

            Background0Container = BuildBackground("Iridium Container With Background 0", Scaled(BackgroundRadius), Background0Colors);

            Background1Container = BuildBackground("Iridium Container With Background 1", Scaled(BackgroundRadius), Background1Colors);

            HorizontalSeparator = BuildPlainFill("Iridium Horizontal Separator", SeparatorColors, fixedHeight: 1);

            VerticalSeparator = BuildPlainFill("Iridium Vertical Separator", SeparatorColors, fixedWidth: 1);

            NormalText = BuildText("Iridium Normal Text", ScaledInt(BaseTextSize), NormalTextColors);

            LinkText = BuildLinkText("Iridium Link Text", ScaledInt(BaseTextSize), LinkTextColors);

            var subtitleMargin = (int)Scaled(SubtitleAdditionalMargin);

            SubtitleText = BuildText("Iridium Subtitle Text", ScaledInt(SubtitleTextSize), SubtitleTextColors, subtitleMargin);

            var titleMargin = (int)Scaled(TitleAdditionalMargin);

            TitleText = BuildText("Iridium Title Text", ScaledInt(TitleTextSize), TitleTextColors, titleMargin);

            SecondaryText = BuildText("Iridium Secondary Text", ScaledInt(SecondaryTextSize), SecondaryTextColors);

            ElementButton = BuildButton("Iridium Element Button", Scaled(ButtonRadius), ElementColors, TitleTextColors);

            PrimaryButton = BuildButton("Iridium Primary Button", Scaled(ButtonRadius), PrimaryColors, TitleTextColors);

            var squareIconSize = ScaledInt(SquareIconSize);

            CheckboxOff = BuildSquareGlyph(
                "Iridium Checkbox Off",
                squareIconSize,
                CheckboxOffColors,
                CheckboxOffBorderColors,
                CheckboxCheckmarkColors,
                (_, _, _) => { }
            );

            CheckboxOn = BuildSquareGlyph(
                "Iridium Checkbox On",
                squareIconSize,
                CheckboxOnColors,
                CheckboxOnBorderColors,
                CheckboxCheckmarkColors,
                DrawCheckmark
            );

            ArrowButtonRight = BuildSquareGlyph("Iridium Arrow Button Right", squareIconSize, ArrowButtonColors, ArrowButtonBorderColors, ArrowButtonArrowColors, DrawRightArrow);

            ArrowButtonDown = BuildSquareGlyph("Iridium Arrow Button Down", squareIconSize, ArrowButtonColors, ArrowButtonBorderColors, ArrowButtonArrowColors, DrawDownArrow);

            ArrowButtonLeft = BuildSquareGlyph("Iridium Arrow Button Left", squareIconSize, ArrowButtonColors, ArrowButtonBorderColors, ArrowButtonArrowColors, DrawLeftArrow);

            ArrowButtonUp = BuildSquareGlyph("Iridium Arrow Button Up", squareIconSize, ArrowButtonColors, ArrowButtonBorderColors, ArrowButtonArrowColors, DrawUpArrow);

            var switchWidth = ScaledInt(SwitchWidth);
            var switchHeight = ScaledInt(SwitchHeight);

            SwitchOff = BuildSwitch("Iridium Switch Off", switchWidth, switchHeight, false, SwitchOffColors, SwitchButtonColors);

            SwitchOn = BuildSwitch("Iridium Switch On", switchWidth, switchHeight, true, SwitchOnColors, SwitchButtonColors);

            TextField = BuildTextField(
                "Iridium Text Field",
                ScaledInt(BaseTextSize),
                Scaled(TextFieldRadius),
                Scaled(TextFieldBorder),
                TextFieldColors,
                TextFieldBorderColors
            );

            var iconSize = ScaledInt(IconSize);

            IconInformation = BuildIcon("Iridium Icon Information", iconSize, IconInformationColors, IconInformationBorderColors, IconStrokeColors, DrawInformation);

            IconSuccess = BuildIcon("Iridium Icon Success", iconSize, IconSuccessColors, IconSuccessBorderColors, IconStrokeColors, DrawSuccess);

            IconWarning = BuildIcon("Iridium Icon Warning", iconSize, IconWarningColors, IconWarningBorderColors, IconStrokeColors, DrawWarning);

            IconError = BuildIcon("Iridium Icon Error", iconSize, IconErrorColors, IconErrorBorderColors, IconStrokeColors, DrawError);

            IconStop = BuildIcon("Iridium Icon Stop", iconSize, IconStopColors, IconStopBorderColors, IconStrokeColors, DrawStop);
        }

        private double Scale { get; }

        public GUIStyle Base { get; }

        public GUIStyle LinkText { get; }

        public GUIStyle Container { get; }

        public GUIStyle PaddingContainer { get; }

        public GUIStyle Background0Container { get; }

        public GUIStyle Background1Container { get; }

        public GUIStyle HorizontalSeparator { get; }

        public GUIStyle VerticalSeparator { get; }

        public GUIStyle NormalText { get; }

        public GUIStyle SubtitleText { get; }

        public GUIStyle TitleText { get; }

        public GUIStyle SecondaryText { get; }

        public GUIStyle ElementButton { get; }

        public GUIStyle PrimaryButton { get; }

        public GUIStyle CheckboxOff { get; }

        public GUIStyle CheckboxOn { get; }

        public GUIStyle ArrowButtonRight { get; }

        public GUIStyle ArrowButtonDown { get; }

        public GUIStyle ArrowButtonLeft { get; }

        public GUIStyle ArrowButtonUp { get; }

        public GUIStyle SwitchOff { get; }

        public GUIStyle SwitchOn { get; }

        public GUIStyle TextField { get; }

        public GUIStyle IconInformation { get; }

        public GUIStyle IconSuccess { get; }

        public GUIStyle IconWarning { get; }

        public GUIStyle IconError { get; }

        public GUIStyle IconStop { get; }

        public double Margin { get; }

        public List<Texture2D> Textures { get; } = [];

        public void DestroyTextures()
        {
            foreach (var texture in Textures)
            {
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
            }
            Textures.Clear();
        }

        public double Scaled(double value)
        {
            return Scale * value;
        }

        public int ScaledInt(double value)
        {
            return (int)(Scale * value);
        }

        private static Color RGB(long rgb)
        {
            return new Color(
                ((rgb >> 16) & 0xFF) / 255F,
                ((rgb >> 8) & 0xFF) / 255F,
                (rgb & 0xFF) / 255F,
                1
            );
        }

        private static Color ARGB(long rgb)
        {
            return new Color(
                ((rgb >> 16) & 0xFF) / 255F,
                ((rgb >> 8) & 0xFF) / 255F,
                (rgb & 0xFF) / 255F,
                ((rgb >> 24) & 0xFF) / 255F
            );
        }

        private Texture2D RenderImage(int width, int height, Action<Graphics> renderer)
        {
            Color[] pixels;
            var stride = width * 4;

            {
                byte[] rawBytes;

                {
                    using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.Clear(System.Drawing.Color.Transparent);
                    renderer(graphics);
                    var rect = new Rectangle(0, 0, width, height);
                    var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
                    rawBytes = new byte[Math.Abs(bitmapData.Stride) * bitmap.Height];
                    Marshal.Copy(bitmapData.Scan0, rawBytes, 0, rawBytes.Length);
                    bitmap.UnlockBits(bitmapData);
                    stride = Math.Abs(bitmapData.Stride);
                }

                pixels = new Color[width * height];

                for (var row = 0; row < height; row++)
                {
                    var sourceRow = (height - 1 - row) * stride;
                    for (var col = 0; col < width; col++)
                    {
                        var source = sourceRow + col * 4;
                        pixels[row * width + col] = new Color(
                            rawBytes[source + 2] / 255F,
                            rawBytes[source + 1] / 255F,
                            rawBytes[source] / 255F,
                            rawBytes[source + 3] / 255F
                        );
                    }
                }
            }

            var texture = new Texture2D(width, height, TextureFormat.ARGB32, false);
            texture.SetPixels(pixels);
            texture.Apply();
            Textures.Add(texture);
            return texture;
        }

        /// <summary>
        /// 箭头按钮单纹理合成：边框色填充外圆角矩形、背景色填充内圆角矩形，
        /// 再用 DrawArrow 画描边三角——与 <see cref="RenderSquareGlyph"/> 同一管线。
        /// dir: 0=Right 1=Down 2=Left 3=Up（Iris.Iml ArrowDir）。
        /// </summary>
        public Texture2D RenderArrowButtonOnly(int size, int dir, Color fill, Color borderCol, int borderWidth, int radius, Color strokeColor)
        {
            return RenderImage(size, size, graphics =>
            {
                using (var path = new GraphicsPath())
                {
                    AppendRoundRect(path, 0, 0, size, size, radius);
                    using var brush = new SolidBrush(DrawingColor(borderCol));
                    graphics.FillPath(brush, path);
                }
                using (var path = new GraphicsPath())
                {
                    AppendRoundRect(path, borderWidth, borderWidth, size - borderWidth * 2, size - borderWidth * 2, radius - borderWidth);
                    using var brush = new SolidBrush(DrawingColor(fill));
                    graphics.FillPath(brush, path);
                }
                switch (dir)
                {
                    case 1: DrawDownArrow(graphics, size, strokeColor); break;
                    case 2: DrawLeftArrow(graphics, size, strokeColor); break;
                    case 3: DrawUpArrow(graphics, size, strokeColor); break;
                    default: DrawRightArrow(graphics, size, strokeColor); break;
                }
            });
        }

        private static void AppendRoundRect(
            GraphicsPath path,
            double x,
            double y,
            double width,
            double height,
            double radius
        )
        {
            radius = Math.Max(0.0, Math.Min(Math.Min(radius, width / 2.0), height / 2.0));
            var r = (float)radius;
            var diameter = r + r;
            var right = (float)(x + width);
            var bottom = (float)(y + height);
            path.AddArc(right - diameter, (float)y, diameter, diameter, 270, 90);
            path.AddArc(right - diameter, bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc((float)x, bottom - diameter, diameter, diameter, 90, 90);
            path.AddArc((float)x, (float)y, diameter, diameter, 180, 90);
            path.CloseFigure();
        }

        private static void AppendCornerArc(
            GraphicsPath path,
            double radius,
            double xA,
            double yA,
            double xC,
            double yC,
            double xB,
            double yB
        )
        {
            var dx1 = xA - xC;
            var dy1 = yA - yC;
            var dx2 = xB - xC;
            var dy2 = yB - yC;
            var len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            var len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            dx1 /= len1;
            dy1 /= len1;
            dx2 /= len2;
            dy2 /= len2;
            var dot = dx1 * dx2 + dy1 * dy2;
            var startAngle = (Math.Atan2(dy1, dx1) * 180 / Math.PI % 360 + 360) % 360;
            var endAngle = (Math.Atan2(dy2, dx2) * 180 / Math.PI % 360 + 360) % 360;
            if (startAngle > endAngle) (startAngle, endAngle) = (endAngle, startAngle);
            if (endAngle - startAngle > 180) (startAngle, endAngle) = (endAngle, startAngle + 360);

            var scale = radius / Math.Sqrt(1 - dot * dot);
            var centerX = xC + (dx1 + dx2) * scale;
            var centerY = yC + (dy1 + dy2) * scale;

            path.AddArc(
                (float)(centerX - radius),
                (float)(centerY - radius),
                (float)(radius + radius),
                (float)(radius + radius),
                (float)(endAngle + 90),
                (float)(startAngle - endAngle + 180)
            );
        }

        private static double CornerProjection(
            double radius,
            double xA,
            double yA,
            double xC,
            double yC,
            double xB,
            double yB
        )
        {
            var dx1 = xA - xC;
            var dy1 = yA - yC;
            var dx2 = xB - xC;
            var dy2 = yB - yC;
            var len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            var len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            dx1 /= len1;
            dy1 /= len1;
            dx2 /= len2;
            dy2 /= len2;
            var dot = dx1 * dx2 + dy1 * dy2;
            var sx = dx1 + dx2;
            var sy = dy1 + dy2;
            return radius * Math.Sqrt((sx * sx + sy * sy) / (2 - dot - dot));
        }

        private static void AppendRoundedPolyline(
            GraphicsPath path,
            double radius,
            bool close,
            params PointF[] points
        )
        {
            var count = points.Length;

            if (count <= 1) return;

            if (count == 2)
            {
                path.AddLine(points[0], points[1]);
                return;
            }

            radius = Math.Max(radius, 0);

            List<double> segmentLengths = [];

            for (var i = 0; i < count; i++)
            {
                var from = points[i];
                var to = points[i + 1 == count ? 0 : i + 1];
                var dx = to.X - from.X;
                var dy = to.Y - from.Y;
                segmentLengths.Add(Math.Sqrt(dx * dx + dy * dy));
            }

            List<double> cornerRadii = [];

            for (var i = 1; i < count - 1; i++)
                cornerRadii.Add(Math.Min(radius, Math.Min(segmentLengths[i - 1] / 2, segmentLengths[i] / 2)));

            if (close)
            {
                cornerRadii.Add(Math.Min(radius, Math.Min(segmentLengths[^2] / 2, segmentLengths[^1] / 2)));
                cornerRadii.Add(Math.Min(radius, Math.Min(segmentLengths[^1] / 2, segmentLengths[0] / 2)));
            }
            else
            {
                cornerRadii[0] = Math.Min(radius, Math.Min(segmentLengths[0], segmentLengths[1] / 2));
                cornerRadii[^1] = Math.Min(
                    radius,
                    Math.Min(segmentLengths[^3] / 2, segmentLengths[^2])
                );
                var leadLength = segmentLengths[0] - CornerProjection(
                    cornerRadii[0],
                    points[0].X,
                    points[0].Y,
                    points[1].X,
                    points[1].Y,
                    points[2].X,
                    points[2].Y
                );
                var leadX = (points[1].X - points[0].X) * leadLength / segmentLengths[0];
                var leadY = (points[1].Y - points[0].Y) * leadLength / segmentLengths[0];
                path.AddLine(points[0], points[0] + new SizeF((float)leadX, (float)leadY));
            }

            for (var i = 0; i < cornerRadii.Count; i++)
            {
                var cornerRadius = cornerRadii[i];
                var p1 = points[i];
                var p2 = points[i + 1 >= count ? i + 1 - count : i + 1];
                var p3 = points[i + 2 >= count ? i + 2 - count : i + 2];
                AppendCornerArc(path, cornerRadius, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
            }

            if (close) path.CloseFigure();
            else path.AddLine(path.GetLastPoint(), points[^1]);
        }

        private Texture2D RenderFilledRect(int width, int height, Color color)
        {
            return RenderImage(width, height, graphics =>
            {
                using var brush = new SolidBrush(DrawingColor(color));
                graphics.FillRectangle(brush, 0, 0, width, height);
            });
        }

        private Texture2D RenderRoundedRect(int width, int height, double radius, Color color)
        {
            return RenderImage(width, height, graphics =>
            {
                using var path = new GraphicsPath();
                AppendRoundRect(path, 0, 0, width, height, radius);
                using var brush = new SolidBrush(DrawingColor(color));
                graphics.FillPath(brush, path);
            });
        }

        private Texture2D RenderBorderedRoundedRect(
            int width,
            int height,
            double radius,
            double border,
            Color color,
            Color borderColor
        )
        {
            return RenderImage(width, height, graphics =>
            {
                {
                    using var path = new GraphicsPath();
                    AppendRoundRect(path, 0, 0, width, height, radius);
                    using var brush = new SolidBrush(DrawingColor(borderColor));
                    graphics.FillPath(brush, path);
                }
                {
                    using var path = new GraphicsPath();
                    AppendRoundRect(
                        path,
                        border,
                        border,
                        width - border - border,
                        height - border - border,
                        radius - border
                    );
                    using var brush = new SolidBrush(DrawingColor(color));
                    graphics.FillPath(brush, path);
                }
            });
        }

        private Texture2D RenderSquareGlyph(
            Color color,
            Color borderColor,
            Color strokeColor,
            Action<Graphics, int, Color> stroke
        )
        {
            var size = ScaledInt(SquareIconSize);
            var radius = Scaled(SquareIconRadius);
            var border = Scaled(SquareIconBorder);
            return RenderImage(size, size, graphics =>
            {
                {
                    using var path = new GraphicsPath();
                    AppendRoundRect(path, 0, 0, size, size, radius);
                    using var brush = new SolidBrush(DrawingColor(borderColor));
                    graphics.FillPath(brush, path);
                }
                {
                    using var path = new GraphicsPath();
                    AppendRoundRect(
                        path,
                        border,
                        border,
                        size - border - border,
                        size - border - border,
                        radius - border
                    );
                    using var brush = new SolidBrush(DrawingColor(color));
                    graphics.FillPath(brush, path);
                }
                stroke(graphics, size, strokeColor);
            });
        }

        private Texture2D RenderSwitchGlyph(
            bool on,
            Color color,
            Color buttonColor
        )
        {
            var width = ScaledInt(SwitchWidth);
            var height = ScaledInt(SwitchHeight);
            var radius = height / 2F;
            var buttonRadius = (float)Scaled(SwitchButtonRadius);
            var buttonX = on ? width - radius - buttonRadius : radius - buttonRadius;
            var buttonY = radius - buttonRadius;
            return RenderImage(width, height, graphics =>
            {
                {
                    using var path = new GraphicsPath();
                    AppendRoundRect(path, 0, 0, width, height, radius);
                    using var brush = new SolidBrush(DrawingColor(color));
                    graphics.FillPath(brush, path);
                }
                {
                    using var path = new GraphicsPath();
                    path.AddArc(
                        buttonX,
                        buttonY,
                        buttonRadius + buttonRadius,
                        buttonRadius + buttonRadius,
                        0,
                        360
                    );
                    path.CloseFigure();
                    using var brush = new SolidBrush(DrawingColor(buttonColor));
                    graphics.FillPath(brush, path);
                }
            });
        }

        private Texture2D RenderCircleGlyph(
            Color color,
            Color borderColor,
            Color strokeColor,
            Action<Graphics, int, Color> stroke
        )
        {
            var size = ScaledInt(IconSize);
            var border = (float)ScaledInt(IconBorder);
            return RenderImage(size, size, graphics =>
            {
                {
                    using var path = new GraphicsPath();
                    path.AddArc(0, 0, size, size, 0, 360);
                    path.CloseFigure();
                    using var brush = new SolidBrush(DrawingColor(borderColor));
                    graphics.FillPath(brush, path);
                }
                {
                    using var path = new GraphicsPath();
                    path.AddArc(
                        border,
                        border,
                        size - border - border,
                        size - border - border,
                        0,
                        360
                    );
                    path.CloseFigure();
                    using var brush = new SolidBrush(DrawingColor(color));
                    graphics.FillPath(brush, path);
                }
                stroke(graphics, size, strokeColor);
            });
        }

        private static void DrawCheckmark(Graphics graphics, int size, Color strokeColor)
        {
            using var path = new GraphicsPath();
            path.AddLines([
                new PointF(size * 9 / 32F, size * 17 / 32F),
                new PointF(size * 13 / 32F, size * 21 / 32F),
                new PointF(size * 23 / 32F, size * 11 / 32F)
            ]);
            using var pen = new Pen(DrawingColor(strokeColor), size * 2 / 20F);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            graphics.DrawPath(pen, path);
        }

        private static void DrawArrow(Graphics graphics, int size, Color strokeColor, bool flip, bool rotate)
        {
            using var path = new GraphicsPath();
            path.AddLines([
                Transform(new PointF(size * 13 / 32F, size * 8 / 32F)),
                Transform(new PointF(size * 21 / 32F, size * 16 / 32F)),
                Transform(new PointF(size * 13 / 32F, size * 24 / 32F))
            ]);
            using var pen = new Pen(DrawingColor(strokeColor), size * 2 / 20F);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            graphics.DrawPath(pen, path);

            return;

            PointF Transform(PointF point)
            {
                var p = point;
                if (flip) p.X = size - p.X;
                if (rotate) p = new PointF(size - p.Y, p.X);
                return p;
            }
        }

        private static void DrawRightArrow(Graphics graphics, int size, Color strokeColor)
        {
            DrawArrow(graphics, size, strokeColor, false, false);
        }

        private static void DrawDownArrow(Graphics graphics, int size, Color strokeColor)
        {
            DrawArrow(graphics, size, strokeColor, false, true);
        }

        private static void DrawLeftArrow(Graphics graphics, int size, Color strokeColor)
        {
            DrawArrow(graphics, size, strokeColor, true, false);
        }

        private static void DrawUpArrow(Graphics graphics, int size, Color strokeColor)
        {
            DrawArrow(graphics, size, strokeColor, true, true);
        }

        private static Pen StrokePen(int size, Color strokeColor, float thickness)
        {
            var pen = new Pen(DrawingColor(strokeColor), size * thickness / 20F);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            return pen;
        }

        private static void FillDot(Graphics graphics, float x, float y, float diameter, Color strokeColor)
        {
            using var path = new GraphicsPath();
            path.AddArc(x, y, diameter, diameter, 0, 360);
            path.CloseFigure();
            using var brush = new SolidBrush(DrawingColor(strokeColor));
            graphics.FillPath(brush, path);
        }

        private static void DrawRingWithStem(Graphics graphics, int size, Color strokeColor, float stemTop, float stemBottom)
        {
            using var path = new GraphicsPath();
            path.AddArc(size * 4 / 20F, size * 4 / 20F, size * 12 / 20F, size * 12 / 20F, 0, 360);
            path.StartFigure();
            path.AddLine(size * 10 / 20F, size * stemTop / 20F, size * 10 / 20F, size * stemBottom / 20F);
            using var pen = StrokePen(size, strokeColor, 1.5F);
            graphics.DrawPath(pen, path);
        }

        private static void DrawInformation(Graphics graphics, int size, Color strokeColor)
        {
            DrawRingWithStem(graphics, size, strokeColor, 9.5F, 13F);
            FillDot(graphics, size * 9.25F / 20F, size * 6.25F / 20F, size * 1.5F / 20F, strokeColor);
        }

        private static void DrawSuccess(Graphics graphics, int size, Color strokeColor)
        {
            var path = new GraphicsPath();
            path.AddArc(size * 4 / 20F, size * 4 / 20F, size * 12 / 20F, size * 12 / 20F, 0, 285);
            path.StartFigure();
            path.AddLines([
                new PointF(size * 8 / 20F, size * 9F / 20F),
                new PointF(size * 10 / 20F, size * 11F / 20F),
                new PointF(size * 16 / 20F, size * 5F / 20F)
            ]);
            using var pen = StrokePen(size, strokeColor, 1.5F);
            graphics.DrawPath(pen, path);
        }

        private static void DrawWarning(Graphics graphics, int size, Color strokeColor)
        {
            using var path = new GraphicsPath();
            AppendRoundedPolyline(
                path,
                size * 2 / 20F,
                true,
                PolarPoint(size, 30, 9),
                PolarPoint(size, 150, 9),
                PolarPoint(size, 270, 9)
            );
            path.CloseFigure();
            path.AddLine(size * 10 / 20F, size * 7 / 20F, size * 10 / 20F, size * 10.5F / 20F);
            using var pen = StrokePen(size, strokeColor, 1.5F);
            graphics.DrawPath(pen, path);

            FillDot(graphics, size * 9.25F / 20F, size * 12.25F / 20F, size * 1.5F / 20F, strokeColor);
        }

        private static void DrawError(Graphics graphics, int size, Color strokeColor)
        {
            DrawRingWithStem(graphics, size, strokeColor, 7F, 10.5F);
            FillDot(graphics, size * 9.25F / 20F, size * 12.25F / 20F, size * 1.5F / 20F, strokeColor);
        }

        private static PointF PolarPoint(int size, double angleDegrees, double distance)
        {
            return new PointF(
                (float)(size * (10 + distance * Math.Cos(angleDegrees / 180 * Math.PI)) / 20),
                (float)(size * (11 + distance * Math.Sin(angleDegrees / 180 * Math.PI)) / 20)
            );
        }

        private static void DrawStop(Graphics graphics, int size, Color strokeColor)
        {
            using var path = new GraphicsPath();
            var inset = size * 4.5F / 20F;
            var block = size * 11F / 20F;
            path.AddRectangle(new RectangleF(inset, inset, block, block));
            using var brush = new SolidBrush(DrawingColor(strokeColor));
            graphics.FillPath(brush, path);
        }

        private static void ApplyFontSize(
            GUIStyle style,
            double size,
            bool isText = false
        )
        {
            style.fontSize = (int)size;
            style.contentOffset = isText
                ? new Vector2(0, -(float)(size * 0.1))
                : new Vector2(0, 0);
        }

        private static void ApplyTextPalette(
            GUIStyle style,
            ColorGroup textColors
        )
        {
            style.onNormal.textColor = style.normal.textColor = textColors.Normal;
            style.onHover.textColor = style.hover.textColor = textColors.Hovered;
            style.onActive.textColor = style.active.textColor = textColors.Active;
            style.onFocused.textColor = style.focused.textColor = textColors.Focused;
        }

        private void ApplyRectFill(
            GUIStyle style,
            ColorGroup colors
        )
        {
            const int size = 256;
            style.padding = style.border = new RectOffset(0, 0, 0, 0);
            style.onNormal.background = style.normal.background = RenderFilledRect(size, size, colors.Normal);
            style.onHover.background = style.hover.background = RenderFilledRect(size, size, colors.Hovered);
            style.onActive.background = style.active.background = RenderFilledRect(size, size, colors.Active);
            style.onFocused.background = style.focused.background = RenderFilledRect(size, size, colors.Focused);
        }

        private void ApplyRoundFill(
            GUIStyle style,
            double radius,
            ColorGroup colors
        )
        {
            var borderSize = (int)Math.Ceiling(radius);
            var size = borderSize + 256;
            style.padding = style.border =
                new RectOffset(borderSize, borderSize, borderSize, borderSize);
            style.onNormal.background = style.normal.background = RenderRoundedRect(size, size, radius, colors.Normal);
            style.onHover.background = style.hover.background = RenderRoundedRect(size, size, radius, colors.Hovered);
            style.onActive.background = style.active.background = RenderRoundedRect(size, size, radius, colors.Active);
            style.onFocused.background = style.focused.background = RenderRoundedRect(size, size, radius, colors.Focused);
        }

        private void ApplyBorderedRoundFill(
            GUIStyle style,
            double radius,
            double border,
            ColorGroup colors,
            ColorGroup borderColors
        )
        {
            var borderSize = (int)Math.Ceiling(radius);
            var size = borderSize + 256;
            style.padding = style.border =
                new RectOffset(borderSize, borderSize, borderSize, borderSize);
            style.onNormal.background = style.normal.background = RenderBorderedRoundedRect(size, size, radius, border, colors.Normal, borderColors.Normal);
            style.onHover.background = style.hover.background = RenderBorderedRoundedRect(size, size, radius, border, colors.Hovered, borderColors.Hovered);
            style.onActive.background = style.active.background = RenderBorderedRoundedRect(size, size, radius, border, colors.Active, borderColors.Active);
            style.onFocused.background = style.focused.background = RenderBorderedRoundedRect(size, size, radius, border, colors.Focused, borderColors.Focused);
        }

        private void ApplyGlyphStates(
            GUIStyle style,
            ColorGroup colors,
            ColorGroup borderColors,
            ColorGroup strokeColors,
            Action<Graphics, int, Color> stroke
        )
        {
            style.onNormal.background = style.normal.background = RenderSquareGlyph(colors.Normal, borderColors.Normal, strokeColors.Normal, stroke);
            style.onHover.background = style.hover.background = RenderSquareGlyph(colors.Hovered, borderColors.Hovered, strokeColors.Hovered, stroke);
            style.onActive.background = style.active.background = RenderSquareGlyph(colors.Active, borderColors.Active, strokeColors.Active, stroke);
            style.onFocused.background = style.focused.background = RenderSquareGlyph(colors.Focused, borderColors.Focused, strokeColors.Focused, stroke);
        }

        private void ApplySwitchStates(
            GUIStyle style,
            bool on,
            ColorGroup colors,
            ColorGroup buttonColors
        )
        {
            style.onNormal.background = style.normal.background = RenderSwitchGlyph(on, colors.Normal, buttonColors.Normal);
            style.onHover.background = style.hover.background = RenderSwitchGlyph(on, colors.Hovered, buttonColors.Hovered);
            style.onActive.background = style.active.background = RenderSwitchGlyph(on, colors.Active, buttonColors.Active);
            style.onFocused.background = style.focused.background = RenderSwitchGlyph(on, colors.Focused, buttonColors.Focused);
        }

        private void ApplyIconStates(
            GUIStyle style,
            ColorGroup colors,
            ColorGroup borderColors,
            ColorGroup strokeColors,
            Action<Graphics, int, Color> stroke
        )
        {
            style.onNormal.background = style.normal.background = RenderCircleGlyph(colors.Normal, borderColors.Normal, strokeColors.Normal, stroke);
            style.onHover.background = style.hover.background = RenderCircleGlyph(colors.Hovered, borderColors.Hovered, strokeColors.Hovered, stroke);
            style.onActive.background = style.active.background = RenderCircleGlyph(colors.Active, borderColors.Active, strokeColors.Active, stroke);
            style.onFocused.background = style.focused.background = RenderCircleGlyph(colors.Focused, borderColors.Focused, strokeColors.Focused, stroke);
        }

        private static System.Drawing.Color DrawingColor(Color color)
        {
            return System.Drawing.Color.FromArgb(
                (int)(color.a * 255),
                (int)(color.r * 255),
                (int)(color.g * 255),
                (int)(color.b * 255)
            );
        }

        private GUIStyle BuildBaseStyle()
        {
            var style = new GUIStyle
            {
                name = "Iridium Base",
                imagePosition = ImagePosition.ImageLeft,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                clipping = TextClipping.Overflow,
                fontStyle = FontStyle.Normal,
                richText = true,
                border = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0)
            };
            ApplyFontSize(style, ScaledInt(BaseTextSize));
            ApplyTextPalette(style, NormalTextColors);
            return style;
        }

        private GUIStyle BuildBackground(string name, double radius, ColorGroup colors)
        {
            var style = new GUIStyle(Base)
            {
                name = name
            };
            ApplyRoundFill(style, radius, colors);
            return style;
        }

        private GUIStyle BuildPlainFill(string name, ColorGroup colors, int? fixedWidth = null, int? fixedHeight = null)
        {
            var style = new GUIStyle(Base)
            {
                name = name
            };
            if (fixedWidth is not null) style.fixedWidth = fixedWidth.Value;
            if (fixedHeight is not null) style.fixedHeight = fixedHeight.Value;
            ApplyRectFill(style, colors);
            return style;
        }

        private GUIStyle BuildText(string name, double fontSize, ColorGroup textColors, int? verticalMargin = null)
        {
            var style = new GUIStyle(Base)
            {
                name = name,
                alignment = TextAnchor.MiddleLeft,
                margin = verticalMargin is null
                    ? new RectOffset(0, 0, 0, 0)
                    : new RectOffset(0, 0, verticalMargin.Value, verticalMargin.Value)
            };
            ApplyFontSize(style, fontSize, true);
            ApplyTextPalette(style, textColors);
            return style;
        }

        private GUIStyle BuildLinkText(string name, double fontSize, ColorGroup textColors)
        {
            var style = BuildText(name, fontSize, textColors);
            style.richText = true;
            return style;
        }

        private GUIStyle BuildButton(string name, double radius, ColorGroup background, ColorGroup text)
        {
            var style = new GUIStyle(Base)
            {
                name = name
            };
            ApplyFontSize(style, ScaledInt(BaseTextSize), true);
            ApplyTextPalette(style, text);
            ApplyRoundFill(style, radius, background);
            return style;
        }

        private GUIStyle BuildSquareGlyph(
            string name,
            int size,
            ColorGroup colors,
            ColorGroup borderColors,
            ColorGroup strokeColors,
            Action<Graphics, int, Color> stroke
        )
        {
            var style = new GUIStyle(Base)
            {
                name = name,
                fixedWidth = size,
                fixedHeight = size
            };
            ApplyGlyphStates(style, colors, borderColors, strokeColors, stroke);
            return style;
        }

        private GUIStyle BuildSwitch(
            string name,
            int width,
            int height,
            bool on,
            ColorGroup colors,
            ColorGroup buttonColors
        )
        {
            var style = new GUIStyle(Base)
            {
                name = name,
                fixedWidth = width,
                fixedHeight = height
            };
            ApplySwitchStates(style, on, colors, buttonColors);
            return style;
        }

        private GUIStyle BuildTextField(
            string name,
            double fontSize,
            double radius,
            double border,
            ColorGroup colors,
            ColorGroup borderColors
        )
        {
            var style = new GUIStyle(Base)
            {
                name = name,
                alignment = TextAnchor.MiddleLeft
            };
            ApplyFontSize(style, fontSize, true);
            ApplyBorderedRoundFill(style, radius, border, colors, borderColors);
            return style;
        }

        private GUIStyle BuildIcon(
            string name,
            int size,
            ColorGroup colors,
            ColorGroup borderColors,
            ColorGroup strokeColors,
            Action<Graphics, int, Color> stroke
        )
        {
            var style = new GUIStyle(Base)
            {
                name = name,
                fixedWidth = size,
                fixedHeight = size
            };
            ApplyIconStates(style, colors, borderColors, strokeColors, stroke);
            return style;
        }

        private class ColorGroup(Color normal, Color hovered, Color active, Color? focused = null)
        {
            public ColorGroup(Color color) : this(color, color, color, color)
            {
            }

            public Color Normal { get; } = normal;
            public Color Hovered { get; } = hovered;
            public Color Active { get; } = active;
            public Color Focused { get; } = focused ?? normal;
        }
    }
}
