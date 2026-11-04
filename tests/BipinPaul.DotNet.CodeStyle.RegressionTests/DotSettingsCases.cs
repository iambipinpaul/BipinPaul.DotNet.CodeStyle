using System.Reflection;
using System.Text;
using System.Xml.Linq;
using static BipinPaul.DotNet.CodeStyle.RegressionTests.HarnessEnvironment;

namespace BipinPaul.DotNet.CodeStyle.RegressionTests;

internal static class DotSettingsCases
{
    private const string FileName = "Sample.slnx.DotSettings";
    private const string LayoutKey = "/Default/CodeStyle/CSharpFileLayoutPatterns/Pattern/@EntryValue";
    private const string ProfileKey = "/Default/CodeStyle/CodeCleanup/Profiles/=ReorderOnly/@EntryIndexedValue";
    private const string UnrelatedKey = "/Default/Example/RetainedSetting/@EntryValue";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace System = "clr-namespace:System;assembly=mscorlib";

    internal static IReadOnlyList<(string Name, Func<HarnessEnvironment, Task> Run)> Cases =>
    [
        ("DotSettings preserves custom layout and unrelated resources", PreservesCustomAsync),
        ("DotSettings with only a layout gains exactly one profile", LayoutOnlyAsync),
        ("DotSettings with only a profile gains exactly one layout", ProfileOnlyAsync),
        ("legacy generated DotSettings upgrades and remains byte-idempotent", UpgradesLegacyAsync),
        ("UTF-16 DotSettings migration remains valid XML", Utf16Async),
        ("DotSettings migration handles alternative XML prefixes", AlternativePrefixesAsync),
        ("duplicate owned DotSettings keys fail without rewriting", DuplicateKeysAsync)
    ];

    private static async Task PreservesCustomAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        XElement layout = Resource(LayoutKey,
            "<Patterns xmlns=\"urn:schemas-jetbrains-com:member-reordering-patterns\"><TypePattern DisplayName=\"My custom layout\" /></Patterns>");
        XElement profile = Resource(ProfileKey, "<Profile name=\"ReorderOnly\"><CSReorderTypeMembers>False</CSReorderTypeMembers></Profile>");
        fixture.Write(FileName, Dictionary(layout, profile, Resource(UnrelatedKey, "preserve this unrelated resource")).ToString());
        Success(await fixture.RunAsync(new(), "init"));
        XDocument updated = Read(fixture);
        SameValue(layout, Find(updated, LayoutKey));
        SameValue(profile, Find(updated, ProfileKey));
        Require(Find(updated, UnrelatedKey).Value == "preserve this unrelated resource", "Init changed an unrelated DotSettings resource.");
    }

    private static Task LayoutOnlyAsync(HarnessEnvironment environment) => SingleOwnedKeyAsync(environment, LayoutKey);

    private static Task ProfileOnlyAsync(HarnessEnvironment environment) => SingleOwnedKeyAsync(environment, ProfileKey);

    private static async Task SingleOwnedKeyAsync(HarnessEnvironment environment, string key)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        XDocument template = ReadTemplate(environment, legacy: false);
        XElement original = key == LayoutKey
            ? Resource(LayoutKey, "<Patterns xmlns=\"urn:schemas-jetbrains-com:member-reordering-patterns\"><TypePattern DisplayName=\"Keep this layout\" /></Patterns>")
            : new XElement(Find(template, key));
        fixture.Write(FileName, Dictionary(original, Resource(UnrelatedKey, "unrelated")).ToString());
        Success(await fixture.RunAsync(new(), "init"));
        XDocument updated = Read(fixture);
        Require(Owned(updated, LayoutKey).Count() == 1 && Owned(updated, ProfileKey).Count() == 1,
            "Init did not produce exactly one resource for each owned key.");
        SameValue(original, Find(updated, key));
        Require(Find(updated, UnrelatedKey).Value == "unrelated", "Adding the missing key lost an unrelated resource.");
        string missing = key == LayoutKey ? ProfileKey : LayoutKey;
        SameValue(Find(template, missing), Find(updated, missing));
    }

    private static async Task UpgradesLegacyAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        XDocument legacy = ReadTemplate(environment, legacy: true);
        legacy.Root!.Add(Resource(UnrelatedKey, "retained during migration"));
        fixture.Write(FileName, legacy.ToString());
        Success(await fixture.RunAsync(new(), "init"));
        XDocument updated = Read(fixture);
        XDocument template = ReadTemplate(environment, legacy: false);
        SameValue(Find(template, LayoutKey), Find(updated, LayoutKey));
        SameValue(Find(template, ProfileKey), Find(updated, ProfileKey));
        Require(Find(updated, UnrelatedKey).Value == "retained during migration", "Migration lost an unrelated resource.");

        XDocument layout = XDocument.Parse(Find(updated, LayoutKey).Value);
        XElement? ordering = layout.Descendants().SingleOrDefault(element => element.Name.LocalName == "TypePattern" &&
            (string?)element.Attribute("DisplayName") == "StyleCop ordering");
        Require((string?)ordering?.Attribute("RemoveRegions") == "All", "Legacy migration did not enable removal of ordering-blocking regions.");
        XElement[] access = layout.Descendants().Where(element => element.Name.LocalName == "Access").ToArray();
        Require(access.Length > 0 && access.All(element =>
                (string?)element.Attribute("Order") == "Public Internal ProtectedInternal Protected PrivateProtected Private"),
            "Legacy migration did not set explicit StyleCop access precedence.");

        byte[] first = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, FileName));
        Success(await fixture.RunAsync(new(), "init"));
        byte[] second = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, FileName));
        Require(first.SequenceEqual(second),
            "A second init changed migrated DotSettings bytes.");
    }

    private static async Task AlternativePrefixesAsync(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        XDocument current = ReadTemplate(environment, legacy: false);
        XDocument alternate = Dictionary(new XElement(Find(current, LayoutKey)), Resource(UnrelatedKey, "alternate prefixes"));
        alternate.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Remove();
        alternate.Root.Add(new XAttribute(XNamespace.Xmlns + "ui", Wpf.NamespaceName));
        alternate.Root.Add(new XAttribute(XNamespace.Xmlns + "values", System.NamespaceName));
        alternate.Root.Add(new XAttribute(XNamespace.Xmlns + "keys", Xaml.NamespaceName));
        fixture.Write(FileName, alternate.ToString());
        Success(await fixture.RunAsync(new(), "init"));
        XDocument updated = Read(fixture);
        Require(Owned(updated, LayoutKey).Count() == 1 && Owned(updated, ProfileKey).Count() == 1,
            "Alternative prefixes caused missing or duplicated owned keys.");
        SameValue(Find(current, LayoutKey), Find(updated, LayoutKey));
        SameValue(Find(current, ProfileKey), Find(updated, ProfileKey));
        Require(Find(updated, UnrelatedKey).Value == "alternate prefixes", "Prefix-independent merging lost an unrelated resource.");
    }

    private static async Task DuplicateKeysAsync(HarnessEnvironment environment)
    {
        foreach (string key in new[] { LayoutKey, ProfileKey })
        {
            using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
            XDocument current = ReadTemplate(environment, legacy: false);
            XElement first = Find(current, key);
            fixture.Write(FileName, Dictionary(new XElement(first), new XElement(first), Resource(UnrelatedKey, "untouched")).ToString());
            byte[] before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, FileName));
            ProcessResult result = await fixture.RunAsync(new(), "init");
            Require(result.ExitCode != 0, "Init accepted duplicate owned DotSettings keys.");
            byte[] after = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, FileName));
            Require(before.SequenceEqual(after),
                "Init rewrote a DotSettings document containing duplicate owned keys.");
        }
    }

    private static async Task Utf16Async(HarnessEnvironment environment)
    {
        using RegressionFixture fixture = environment.CreateFixture(styleConfiguration: false);
        XDocument legacy = ReadTemplate(environment, legacy: true);
        legacy.Declaration = new XDeclaration("1.0", "utf-16", null);
        legacy.Root!.Add(Resource(UnrelatedKey, "Größe"));
        string path = Path.Combine(fixture.Root, FileName);
        await File.WriteAllTextAsync(path, legacy.Declaration + "\n" + legacy, Encoding.Unicode);
        Success(await fixture.RunAsync(new(), "init"));
        XDocument updated = Read(fixture);
        Require(Find(updated, UnrelatedKey).Value == "Größe", "Migration corrupted a non-ASCII unrelated setting.");
        SameValue(Find(ReadTemplate(environment, legacy: false), LayoutKey), Find(updated, LayoutKey));
    }

    private static XDocument ReadTemplate(HarnessEnvironment environment, bool legacy)
    {
        Assembly tool = Assembly.LoadFrom(environment.Tool);
        string suffix = legacy ? ".solution.DotSettings.legacy.template" : ".solution.DotSettings.template";
        string? resource = tool.GetManifestResourceNames().SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resource is null && legacy)
        {
            // The unchanged baseline predates a separate legacy resource.
            resource = tool.GetManifestResourceNames().Single(name => name.EndsWith(".solution.DotSettings.template", StringComparison.Ordinal));
        }
        Require(resource is not null, "Tool DLL is missing the DotSettings template resource.");
        using Stream stream = tool.GetManifestResourceStream(resource!)!;
        return XDocument.Load(stream);
    }

    private static XDocument Read(RegressionFixture fixture) => XDocument.Load(Path.Combine(fixture.Root, FileName));

    private static XDocument Dictionary(params XElement[] elements) => new(new XElement(Wpf + "ResourceDictionary",
        new XAttribute(XNamespace.Xmlns + "wpf", Wpf.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "s", System.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName), elements));

    private static XElement Resource(string key, string value) => new(System + "String", new XAttribute(Xaml + "Key", key), value);

    private static IEnumerable<XElement> Owned(XDocument document, string key) =>
        document.Root!.Elements().Where(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XElement Find(XDocument document, string key) => Owned(document, key).Single();

    private static void SameValue(XElement expected, XElement actual) =>
        Require(XNode.DeepEquals(XDocument.Parse(expected.Value), XDocument.Parse(actual.Value)),
            "Init changed the nested XML value for " + (string?)expected.Attribute(Xaml + "Key"));

    private static void Success(ProcessResult result) =>
        Require(result.ExitCode == 0, $"Expected init success, got {result.ExitCode}: {result.Output}");
}
