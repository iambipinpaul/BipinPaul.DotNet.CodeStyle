using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace JetBrains.Annotations
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    internal sealed class NoReorderAttribute : Attribute
    {
    }
}

namespace OrderingFixtures
{
    internal delegate int PreservedDelegate(int value);

    internal enum PreservedEnum
    {
        First = 7,
        Second,
        Third
    }

    internal interface IPreservedInterface
    {
        int Value { get; }

        void Touch();
    }

    // Both fields deliberately share access. Readonly sorting must not alter layout.
    internal struct PreservedStruct
    {
        #region Preserved struct fields

        public int First;
        public readonly int Second;

        #endregion Preserved struct fields
    }

    internal record struct PreservedRecordStruct
    {
        #region Preserved record struct fields

        public int First;
        public readonly int Second;

        #endregion Preserved record struct fields
    }

    internal static class Program
    {
        public static int Main()
        {
            InitializationTrace.Reset();
            Require(StrictInitializerCase.Second == 3, "Strict static dependency value");
            Require(InitializationTrace.Snapshot == "strict-first,strict-second", "Strict static initializer order");

            InitializationTrace.Reset();
            PreservedInitializerCase preserved = new();
            Require(preserved.First == 1 && preserved.Second == 2, "NoReorder initializer values");
            Require(InitializationTrace.Snapshot == "preserved-first,preserved-second", "NoReorder external side effects");

            InitializationTrace.Reset();
            InstanceInitializerCase instance = new();
            Require(instance.First == 1 && instance.Second == 2, "Instance initializer values");
            Require(InitializationTrace.Snapshot == "instance-first,instance-second", "Instance initializer order");

            Require(Marshal.OffsetOf<PreservedStruct>(nameof(PreservedStruct.First)).ToInt64() == 0, "Struct first offset");
            Require(Marshal.OffsetOf<PreservedStruct>(nameof(PreservedStruct.Second)).ToInt64() == 4, "Struct second offset");
            Require(Marshal.OffsetOf<PreservedRecordStruct>(nameof(PreservedRecordStruct.First)).ToInt64() == 0, "Record struct first offset");
            Require(Marshal.OffsetOf<PreservedRecordStruct>(nameof(PreservedRecordStruct.Second)).ToInt64() == 4, "Record struct second offset");
            Require(Marshal.OffsetOf<PreservedLayoutClass>(nameof(PreservedLayoutClass.First)).ToInt64() == 0, "Layout class first offset");
            Require(Marshal.OffsetOf<PreservedLayoutClass>(nameof(PreservedLayoutClass.Second)).ToInt64() == 4, "Layout class second offset");

            Require((int)PreservedEnum.Second == 8 && (int)PreservedEnum.Third == 9, "Enum implicit values");
            Require(GeneratedRegexCase.Collapse("a  b") == "a b", "Existing generated regex result");
            Require(RegexGenerationCandidate.Collapse("a  b") == "a b", "Regex conversion candidate result");
            Console.WriteLine("Ordering fixture invariants passed.");
            return 0;
        }

        private static void Require(bool condition, string invariant)
        {
            if (!condition)
            {
                throw new InvalidOperationException(invariant);
            }
        }
    }

    internal class MixedFieldsCase
    {
        private const int HiddenConstant = 3;
        internal readonly int VisibleField = 4;
        public int PublicField = 1;
        private int hiddenField = 2;
        internal const int InternalConstant = 5;
        public const int PublicConstant = 6;
    }

    internal class AccessOrderCase
    {
        protected void ProtectedMethod() { }
        internal void InternalMethod() { }
        public void PublicMethod() { }
        private protected void PrivateProtectedMethod() { }
        protected internal void ProtectedInternalMethod() { }
        private void PrivateMethod() { }
    }

    internal class PropertyIndexerCase
    {
        public int this[int index] => index + HiddenProperty;

        private int HiddenProperty => 7;

        public int PublicProperty => 1;
    }

    internal class RegionPropertyCase
    {
        private int HiddenProperty => 2;

        #region Financial tracking

        public int FirstProperty => 1;
        public int SecondProperty => 2;

        #endregion Financial tracking
    }

    internal class RegionNestedTypeCase
    {
        private sealed class NestedClass { }

        #region Container Volume

        public int Volume { get; set; }

        #endregion Container Volume
    }

    internal class RegionDestructorCase
    {
        #region Other

        ~RegionDestructorCase() { }

        #endregion Other

        #region Fields and Constants

        internal Queue<Action> queuedTasks = new();
        private bool disposed;

        #endregion Fields and Constants

        public bool Disposed => disposed;
    }

    internal class NestedKindsCase
    {
        private sealed record HiddenRecord(int Value);
        public readonly record struct PublicRecordStruct(int Value);
        private readonly record struct HiddenRecordStruct(int Value);
        public sealed class PublicClass { }
        public struct PublicStruct { public int Value; }
        private struct HiddenStruct { public int Value; }
        public sealed record PublicRecord(int Value);
        private sealed class HiddenClass { }
    }

    internal record RecordClassMembersCase
    {
        public int Read() => value;

        private int value = 7;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal sealed class PreservedLayoutClass
    {
        #region Physical layout

        public int First;
        public readonly int Second;

        #endregion Physical layout
    }

    [JetBrains.Annotations.NoReorder]
    internal sealed class PreservedInitializerCase
    {
        #region Ordered external side effects

        public int First = InitializationTrace.Record("preserved-first");
        public readonly int Second = InitializationTrace.Record("preserved-second");

        #endregion Ordered external side effects
    }

    internal sealed class InstanceInitializerCase
    {
        public int First = InitializationTrace.Record("instance-first");
        public readonly int Second = InitializationTrace.Record("instance-second");
    }

    // This type intentionally needs lower-access dependency initialization first.
    // The pinned StyleCop version does not exempt dependent fields from SA1202.
    #pragma warning disable SA1202
    internal static class StrictInitializerCase
    {
        private static readonly int first = InitializationTrace.Record("strict-first");
        public static readonly int Second = first + InitializationTrace.Record("strict-second");

        static StrictInitializerCase() { }
    }
    #pragma warning restore SA1202

    internal static class InitializationTrace
    {
        private static readonly List<string> events = new();

        public static string Snapshot => string.Join(",", events);

        public static int Record(string name)
        {
            events.Add(name);
            return events.Count;
        }

        public static void Reset() => events.Clear();
    }

    internal static partial class GeneratedRegexCase
    {
        public static string Collapse(string text) => MyRegex().Replace(text, " ");

        private sealed class NestedClass { }

        [GeneratedRegex(@"\s+")]
        private static partial Regex MyRegex();
    }

    // Keep this ordinary Regex call to exercise a later analyzer code-fix pass.
    internal static class RegexGenerationCandidate
    {
        public static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

        private sealed class NestedClass { }
    }
}
