using System.Collections.Generic;
using System.Runtime.Loader;

namespace Avalonia.Collectible
{
    /// <summary>
    /// Internal marker for framework types that can report references to retiring collectible
    /// <see cref="AssemblyLoadContext"/> instances without reflection.
    /// </summary>
    internal interface ICollectibleContextReferencer
    {
        bool ReferencesContexts(IReadOnlySet<AssemblyLoadContext> retiringContexts);
    }

    /// <summary>
    /// Internal marker for observable targets that can prune retiring observers without reflection.
    /// </summary>
    internal interface ICollectibleObserverTarget
    {
        void SweepCollectibleObservers(IReadOnlySet<AssemblyLoadContext> retiringContexts);
    }

    /// <summary>
    /// Internal marker for weak-event subscription instances tracked for collectible ALC cleanup.
    /// </summary>
    internal interface ICollectibleSubscriptionTarget
    {
        void SweepSubscriptionCollectible(IReadOnlySet<AssemblyLoadContext> retiringContexts);
    }
}
