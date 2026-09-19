using System.Runtime.CompilerServices;

// The TestLab harness drives the tool the way a user would — pick an avatar, run, inspect the result — and
// needs the internal hooks for that. It is a development-only assembly that lives outside this package
// (MAIN's ARFTestLab~ folder, which Unity skips), so naming it here costs the shipped package nothing.
[assembly: InternalsVisibleTo("Shugan.ARFTestLab.Editor")]
