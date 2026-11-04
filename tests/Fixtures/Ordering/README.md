# Member ordering regression fixture

This .NET 10 executable isolates the layout contract. The project pins
`StyleCop.Analyzers` to `1.2.0-beta.556`, disables inherited build configuration,
and enables only SA1201, SA1202, and SA1203 as errors. Compiler errors remain
enabled; unused fixture-field warnings and unrelated analyzer diagnostics are disabled.

The ordinary cases start deliberately out of order. After the pinned ReSharper
`ReorderOnly` pass uses the staged `solution.DotSettings.template`, they must
compile without these three diagnostics. No formatter, build, or executable has
been run while creating this fixture.

| Case | Expected result after cleanup |
| --- | --- |
| `MixedFieldsCase` | Fields and constants use public, internal, protected internal, protected, private protected, private access order. Within one access group, constants precede fields. In particular the internal readonly field precedes the private constant. |
| `AccessOrderCase` | All six method accessibility levels use the explicit StyleCop order; internal methods precede protected methods. |
| `PropertyIndexerCase` | All properties precede indexers, even a private property versus a public indexer. Public properties precede private properties. |
| `RegionPropertyCase` | The existing region is removed and public properties precede private properties. |
| `RegionNestedTypeCase` | The existing region is removed and the property precedes the nested class. |
| `RegionDestructorCase` | The existing regions are removed and fields precede the destructor. |
| `NestedKindsCase` | Structs and record structs share one access-sorted group before classes and record classes, which share another access-sorted group. |
| `RecordClassMembersCase` | A record class uses the ordinary member layout; its field precedes its method. |
| `GeneratedRegexCase` | The existing generated regex attribute and partial method move before the nested class without changing the pattern. |
| `RegexGenerationCandidate` | A subsequent analyzer code-fix may generate a regex partial method. The complete pipeline must leave that method before the nested class; a reorder-only pass cannot fix a declaration introduced afterward. |

The following types are valid under the three enforced rules before cleanup and
must remain unchanged, including their regions and field order:

- `PreservedStruct` and `PreservedRecordStruct`: implicit value-type physical layout.
- `PreservedLayoutClass`: explicit sequential `StructLayout`.
- `PreservedInitializerCase`: the `JetBrains.Annotations.NoReorderAttribute` guard.
- `PreservedEnum`, `IPreservedInterface`, and `PreservedDelegate`: existing contract exclusions.

The fixture supplies a local `NoReorderAttribute` marker with the exact CLR name;
no JetBrains.Annotations package is needed. The template also guards
`ExtendedLayoutAttribute`; that SDK-dependent attribute is not exercised here.

Running the executable must print exactly:

```text
Ordering fixture invariants passed.
```

Its assertions check enum numeric values, sequential field offsets, regex
results, instance initialization order, and opt-out initialization side effects.
`StrictInitializerCase` additionally checks static dependency order and values.
Its narrowly scoped SA1202 suppression is intentional: the private field must
initialize before the public dependent field. The explicit static constructor
prevents `beforefieldinit` timing from affecting the trace assertions.

`StaticFieldReorderingPolicy="Strict"` protects recognized static dependencies.
It is not a general proof about external side effects or arbitrary instance
initializers. The ordinary instance case verifies that removing unnecessary
readonly sorting preserves the order of same-access initializers; unsafe types
that need broader preservation should use the opt-out guard.

Validation should retain the original fixture, apply the actual pinned cleanup
to a disposable copy, inspect the resulting member order and excluded-type text,
compile the copy, and run the executable. Also run the complete formatting
pipeline on a fresh copy to check late generated-regex declarations and confirm
that a second run leaves ordering unchanged.
