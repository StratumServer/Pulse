using Atlas.XUnit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

// The StageModUnderTest target lays out mod/ with modinfo.json and Pulse.dll, the same shape as
// the shipped zip. Atlas copies the folder into the embedded server's Mods directory.
[assembly: AtlasMods("mod")]
