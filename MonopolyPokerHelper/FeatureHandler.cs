using System;

namespace MonopolyPokerHelper
{
    /// <summary>
    /// Base class for handlers to share properties like IsEnabled and IsResolved.
    /// By using a generic type parameter (CRTP), each handler gets its own independent static state.
    /// </summary>
    internal abstract class FeatureHandler<T> where T : FeatureHandler<T>
    {
        public static bool IsEnabled { get; set; } = true;
        public static bool IsResolved { get; protected set; }
    }
}
