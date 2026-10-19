// 仅供 OptimizationReview 的纯逻辑断言使用的【Unity 框架类型替代品】。
//
// 【为什么只伪造框架类型，不伪造 ItemDefinition】
// ItemDefinition 里有一段真实逻辑（OnValidate 的 ID 兜底、MaxStackSize 的下限），
// 伪造它等于把那部分排除在验证之外。而它用到的 UnityEngine API
// 全是框架设施（特性、Sprite、ScriptableObject）—— 伪造它们没有损失。
//
// 【这个文件的价值：它是一张"Unity 依赖清单"】
// 每次真实源码用了新的 Unity API，这个工程就会编译失败，然后必须在下面补桩。
// 那意味着"ItemDefinition / Inventory / ItemStack 到底依赖 Unity 的哪些部分"
// 是随时可见的 —— 而不是靠人去读。
//
// 【边界】特性的真实作用（Inspector 分组、Tooltip、范围限制）这里完全不实现；
// 序列化行为也无法在 .NET 控制台工程里验证。那些要靠 Unity。
namespace UnityEngine
{
    // ---------------------------------------------------------------- 序列化相关

    /// <summary>标记需要序列化的字段。这里只是个空标记。</summary>
    [System.AttributeUsage(System.AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class SerializeField : System.Attribute
    {
    }

    // ---------------------------------------------------------------- Inspector 特性
    //
    // 这些都是"给编辑器看的装饰"，运行时没有任何行为。
    // 真实工程里它们影响 Inspector 的显示，测试里忽略即可。

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class HeaderAttribute : System.Attribute
    {
        public HeaderAttribute(string header) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class TooltipAttribute : System.Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class TextAreaAttribute : System.Attribute
    {
        public TextAreaAttribute() { }
        public TextAreaAttribute(int minLines, int maxLines) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class MinAttribute : System.Attribute
    {
        public MinAttribute(float min) { }
    }

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class RangeAttribute : System.Attribute
    {
        public RangeAttribute(float min, float max) { }
    }

    /// <summary>让资产能通过右键菜单创建。运行时不参与，空实现即可。</summary>
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public sealed class CreateAssetMenuAttribute : System.Attribute
    {
        public string fileName;
        public string menuName;
        public int order;
    }

    // ---------------------------------------------------------------- 资产类型

    /// <summary>
    /// Unity 所有资产类型的根。这里只需要 CreateInstance 能造出实例。
    ///
    /// 【为什么必须是 static 方法】真实工程里 CreateInstance&lt;T&gt;() 是
    /// UnityEngine.Object 上的静态方法，调用点是 ItemDefinition.CreateRuntime。
    /// 写成实例方法的话那行真实源码就编译不过了。
    /// </summary>
    public class Object
    {
        /// <summary>真实工程里会创建并注册一个资产实例。测试里只需要一个对象。</summary>
        public static T CreateInstance<T>() where T : Object, new()
        {
            return new T();
        }

        /// <summary>真实工程里是资产/对象名。</summary>
        public string name = string.Empty;

        public override string ToString() => string.IsNullOrEmpty(name) ? GetType().Name : name;
    }

    /// <summary>ScriptableObject 的替代品。真实那个提供持久化资产能力，这里只需要能被构造。</summary>
    public class ScriptableObject : Object
    {
    }

    /// <summary>
    /// 图片资产。真实那个持有纹理和矩形信息，
    /// 这里的替代只需要"有 texture 属性、且 texture 可能是 null"这个形状 ——
    /// 界面代码判断 <c>item.Icon != null &amp;&amp; item.Icon.texture != null</c> 就是这个形状。
    /// </summary>
    public class Sprite : Object
    {
        public Texture2D texture;
    }

    /// <summary>纹理资产。测试里只用来让 Sprite.texture 有类型。</summary>
    public class Texture2D : Object
    {
    }

    // ---------------------------------------------------------------- 数学

    /// <summary>
    /// Mathf 的替代品。
    ///
    /// 【只实现被用到的那几个】真实工程里 Mathf 有几十个成员。
    /// 用到新的就补 —— 补的时候正好会想一下"这个函数在真实 Unity 里是什么语义"。
    /// </summary>
    public static class Mathf
    {
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;

        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;

        public static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        public static float Clamp01(float value) => Clamp(value, 0f, 1f);

        public static float Abs(float value) => value < 0f ? -value : value;

        public static bool Approximately(float a, float b)
            => Abs(b - a) < Max(1e-6f * Max(Abs(a), Abs(b)), 1.1754944E-38f * 8f);

        public static int FloorToInt(float value)
            => value >= 0f ? (int)value : (int)value - (value == (int)value ? 0 : 1);

        public static int RoundToInt(float value) => (int)System.MathF.Round(value);

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    }
}
