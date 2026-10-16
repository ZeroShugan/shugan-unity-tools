// Copyright (c) 2026 Shugan. All rights reserved.
// Part of Shugan Unity Tools. Use is subject to LICENSE.md in the package root.
// No redistribution or resale, modified or not. Not to be used to train AI models, nor given
// to an AI system to reproduce, rewrite or port it.

using System.Runtime.CompilerServices;

// The TestLab harness drives the tool the way a user would — pick an avatar, run, inspect the result — and
// needs the internal hooks for that. It is a development-only assembly that lives outside this package
// (MAIN's ARFTestLab~ folder, which Unity skips), so naming it here costs the shipped package nothing.
[assembly: InternalsVisibleTo("Shugan.ARFTestLab.Editor")]
