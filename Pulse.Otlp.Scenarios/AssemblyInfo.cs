using Atlas.XUnit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Both mods, staged side by side. StageModsUnderTest lays out mod/ and otlpmod/ with the exact
// contents of the two shipped zips, and Atlas copies each folder into the embedded server's mod
// path under its own name, which is the shape a real Mods directory takes. The base mod has to be
// there: the exporter subscribes to a meter it does not create.
[assembly: AtlasMods("mod", "otlpmod")]
