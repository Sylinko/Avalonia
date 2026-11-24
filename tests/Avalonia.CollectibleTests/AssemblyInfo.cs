global using NUnit.Framework;
global using Avalonia.Headless.NUnit;

using Avalonia.CollectibleTests;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestApplication))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
