using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Threading;

namespace Avalonia.CollectibleTests;

[TestFixture]
public sealed class CollectibleAssemblyCleanupTests
{
    private sealed class TestCollectibleLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    public class HostGenericOuter<TOuter>
    {
        public class Inner<TInner>;
    }

    private static class HostHelper
    {
        public static uint Zero() => 0;
        public static int HostInt() => 42;
        public static string DescribeType<T>() => typeof(T).FullName ?? typeof(T).Name;
    }

    [Test]
    public void ReferencesAndCollectContexts_CrossAlcCompoundTypesAndDelegates_MatchAllReferencedContexts()
    {
        var alcA = new TestCollectibleLoadContext();
        var alcB = new TestCollectibleLoadContext();
        var alcUnrelated = new TestCollectibleLoadContext();

        try
        {
            var asmA = BuildSampleAssembly(alcA, $"CollectibleSampleA_{Guid.NewGuid():N}", "SampleA");
            var asmB = BuildSampleAssembly(alcB, $"CollectibleSampleB_{Guid.NewGuid():N}", "SampleB");

            var typeA = asmA.GetType("SampleA.Payload", throwOnError: true)!;
            var typeB = asmB.GetType("SampleB.Payload", throwOnError: true)!;

            var compoundType = typeof(Dictionary<,>).MakeGenericType(typeA, typeB);
            var arrayType = typeof(List<>).MakeGenericType(typeA.MakeArrayType());
            var openNestedType = typeof(HostGenericOuter<>.Inner<>);
            var nestedWithModOuter = openNestedType.MakeGenericType(typeA, typeof(CollectibleAssemblyCleanupTests));

            var staticMethod = typeA.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static)!;
            var staticDelegate = Delegate.CreateDelegate(typeof(Func<uint>), staticMethod);

            var hostGenericMethod = typeof(HostHelper)
                .GetMethod(nameof(HostHelper.DescribeType), BindingFlags.Public | BindingFlags.Static)!
                .MakeGenericMethod(typeA);
            var hostGenericDelegate = Delegate.CreateDelegate(typeof(Func<string>), hostGenericMethod);

            var multicast = Delegate.Combine((Func<uint>)HostHelper.Zero, staticDelegate);
            var instanceB = Activator.CreateInstance(typeB)!;
            var instanceMethod = typeB.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance)!;
            var instanceDelegate = Delegate.CreateDelegate(typeof(Func<int>), instanceB, instanceMethod);
            var hostPlusInstanceMulticast = Delegate.Combine((Func<int>)HostHelper.HostInt, instanceDelegate);

            var setOnlyA = new HashSet<AssemblyLoadContext> { alcA };
            var setOnlyB = new HashSet<AssemblyLoadContext> { alcB };
            var setUnrelated = new HashSet<AssemblyLoadContext> { alcUnrelated };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(CollectibleAssemblyCleanup.References(typeof(string), setOnlyA), Is.False);
                Assert.That(CollectibleAssemblyCleanup.References(typeof(Dictionary<string, int>), setOnlyA), Is.False);

                Assert.That(CollectibleAssemblyCleanup.References(compoundType, setOnlyA), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(compoundType, setOnlyB), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(compoundType, setUnrelated), Is.False);

                Assert.That(CollectibleAssemblyCleanup.References(arrayType, setOnlyA), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(openNestedType, setOnlyA), Is.False);
                Assert.That(CollectibleAssemblyCleanup.References(nestedWithModOuter, setOnlyA), Is.True);

                Assert.That(staticDelegate.GetType().IsCollectible, Is.False);
                Assert.That(staticDelegate.Target, Is.Null);
                Assert.That(CollectibleAssemblyCleanup.References(staticDelegate, setOnlyA), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(staticDelegate, setOnlyB), Is.False);

                Assert.That(hostGenericDelegate.Target, Is.Null);
                Assert.That(CollectibleAssemblyCleanup.References(hostGenericDelegate, setOnlyA), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(hostGenericDelegate, setOnlyB), Is.False);

                Assert.That(CollectibleAssemblyCleanup.References(multicast, setOnlyA), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(multicast, setOnlyB), Is.False);
                Assert.That(CollectibleAssemblyCleanup.References(hostPlusInstanceMulticast, setOnlyB), Is.True);
                Assert.That(CollectibleAssemblyCleanup.References(hostPlusInstanceMulticast, setOnlyA), Is.False);
                Assert.That(CollectibleAssemblyCleanup.ReferencesObject(compoundType, setOnlyB), Is.True);
                Assert.That(CollectibleAssemblyCleanup.ReferencesObject(staticDelegate, setOnlyA), Is.True);

                Assert.That(CollectibleAssemblyCleanup.IsCollectible(typeof(string)), Is.False);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(typeof(Dictionary<string, int>)), Is.False);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible((Func<uint>)HostHelper.Zero), Is.False);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(compoundType), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(arrayType), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(openNestedType), Is.False);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(nestedWithModOuter), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(staticDelegate), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(hostGenericDelegate), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(multicast), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectible(hostPlusInstanceMulticast), Is.True);
                Assert.That(CollectibleAssemblyCleanup.IsCollectibleObject(instanceB), Is.True);
            }

            var compoundContexts = new HashSet<AssemblyLoadContext>();
            CollectibleAssemblyCleanup.CollectContexts(compoundType, compoundContexts);
            Assert.That(compoundContexts, Is.EquivalentTo(new[] { alcA, alcB }));

            var delegateContexts = new HashSet<AssemblyLoadContext>();
            CollectibleAssemblyCleanup.CollectContexts(hostGenericDelegate, delegateContexts);
            Assert.That(delegateContexts, Is.EquivalentTo(new[] { alcA }));
        }
        finally
        {
            alcA.Unload();
            alcB.Unload();
            alcUnrelated.Unload();
        }
    }

    [Test]
    public void RegisterCleanup_CrossAlcOnceHandle_DisposesOnceAndAllowsRetiringAlcCollectionWhileSiblingLives()
    {
        var disposals = 0;
        var alcA = new TestCollectibleLoadContext();
        var weakRefB = RegisterAndSweepCrossAlcCleanup(alcA, () => disposals++);

        try
        {
            Assert.That(disposals, Is.EqualTo(1));

            for (var i = 0; i < 8 && weakRefB.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.That(weakRefB.IsAlive, Is.False);

            CollectibleAssemblyCleanup.Sweep(new HashSet<AssemblyLoadContext> { alcA });
            Assert.That(disposals, Is.EqualTo(1));
        }
        finally
        {
            CollectibleAssemblyCleanup.Sweep(new HashSet<AssemblyLoadContext> { alcA });
            alcA.Unload();
        }
    }

    [Test]
    public void SweepUiAndSweep_SeparationAndFaultIsolation_RunAllRegisteredHandlers()
    {
        var alc = new TestCollectibleLoadContext();
        var retiring = new HashSet<AssemblyLoadContext> { alc };
        var events = new List<string>();

        using var ui1 = CollectibleAssemblyCleanup.RegisterSweeper(_ => events.Add("ui-1"), isUIThreadRequired: true);
        var uiUnregistered = CollectibleAssemblyCleanup.RegisterSweeper(_ => events.Add("ui-removed"), isUIThreadRequired: true);
        using var syncThrowing = CollectibleAssemblyCleanup.RegisterSweeper(_ =>
        {
            events.Add("sync-throw");
            throw new InvalidOperationException("Boom");
        });
        using var sync2 = CollectibleAssemblyCleanup.RegisterSweeper(_ => events.Add("sync-2"));

        uiUnregistered.Dispose();

        try
        {
            CollectibleAssemblyCleanup.SweepUI(retiring);
            Assert.That(events, Is.EqualTo(new[] { "ui-1" }));

            events.Clear();
            var error = Assert.Throws<AggregateException>(() => CollectibleAssemblyCleanup.Sweep(retiring));
            Assert.That(error?.InnerExceptions, Has.Count.EqualTo(1));
            Assert.That(events, Is.EqualTo(new[] { "sync-throw", "sync-2" }));
        }
        finally
        {
            alc.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterAndSweepCrossAlcCleanup(AssemblyLoadContext alcA, Action onDispose)
    {
        var alcB = new TestCollectibleLoadContext();
        var weakRefB = new WeakReference(alcB);
        var asmB = BuildSampleAssembly(alcB, $"CollectibleCross_{Guid.NewGuid():N}", "Cross");
        var typeB = asmB.GetType("Cross.Payload", throwOnError: true)!;
        var instance = Activator.CreateInstance(typeB)!;

        CollectibleAssemblyCleanup.RegisterCleanup(
            new[] { alcA, alcB },
            () =>
            {
                GC.KeepAlive(instance);
                onDispose();
            });

        alcB.Unload();
        CollectibleAssemblyCleanup.Sweep(new HashSet<AssemblyLoadContext> { alcB });
        return weakRefB;
    }

    private static Assembly BuildSampleAssembly(AssemblyLoadContext alc, string assemblyName, string ns)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var tb = mb.DefineType($"{ns}.Payload", TypeAttributes.Public | TypeAttributes.Class);
        tb.DefineDefaultConstructor(MethodAttributes.Public);

        var compute = tb.DefineMethod("Compute", MethodAttributes.Public | MethodAttributes.Static, typeof(uint), Type.EmptyTypes);
        var computeIl = compute.GetILGenerator();
        computeIl.Emit(OpCodes.Ldc_I4_7);
        computeIl.Emit(OpCodes.Ret);

        var getValue = tb.DefineMethod("GetValue", MethodAttributes.Public, typeof(int), Type.EmptyTypes);
        var getIl = getValue.GetILGenerator();
        getIl.Emit(OpCodes.Ldc_I4, 42);
        getIl.Emit(OpCodes.Ret);

        tb.CreateType();

        using var stream = new MemoryStream();
        ab.Save(stream);
        stream.Position = 0;
        return alc.LoadFromStream(stream);
    }
}
